using LyfStack.Agent.Windows.Collectors;
using LyfStack.Agent.Windows.Configuration;
using LyfStack.Agent.Windows.Models;
using LyfStack.Agent.Windows.Persistence;

namespace LyfStack.Agent.Windows.Services;

/// <summary>
/// Orchestrates collectors + session tracking, local persistence, and console output.
/// Kept free of networking/UI frameworks for future agent architecture.
/// </summary>
public sealed class ActivityTrackingService
{
    private readonly AgentOptions _options;
    private readonly ForegroundWindowCollector _foregroundCollector;
    private readonly IdleDetector _idleDetector;
    private readonly SessionTracker _sessionTracker;
    private readonly SqliteSessionStore? _sessionStore;

    private DateTimeOffset _lastPrintedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPersistedAt = DateTimeOffset.MinValue;
    private string? _lastPrintedProcess;
    private ActivityState? _lastPrintedState;

    public ActivityTrackingService(
        AgentOptions options,
        SqliteSessionStore? sessionStore = null,
        ForegroundWindowCollector? foregroundCollector = null,
        IdleDetector? idleDetector = null,
        SessionTracker? sessionTracker = null)
    {
        _options = options;
        _sessionStore = sessionStore;
        _foregroundCollector = foregroundCollector ?? new ForegroundWindowCollector();
        _idleDetector = idleDetector ?? new IdleDetector();
        _sessionTracker = sessionTracker ?? new SessionTracker();
    }

    public SessionTracker SessionTracker => _sessionTracker;

    public bool IsPaused { get; set; }

    public Func<AgentSettings>? SettingsProvider { get; set; }

    public event Action<ActivitySample, SessionUpdateResult?>? SampleProcessed;

    public ActivitySample CollectSample(DateTimeOffset? timestamp = null)
    {
        DateTimeOffset now = timestamp ?? DateTimeOffset.UtcNow;
        TimeSpan idleDuration = _idleDetector.GetIdleDuration();

        if (IsPaused)
        {
            return new ActivitySample
            {
                Timestamp = now,
                State = ActivityState.Unknown,
                IdleDuration = idleDuration,
                SkipAttribution = true
            };
        }

        if (!_foregroundCollector.TryGetForegroundApp(out ForegroundAppInfo? app) || app is null)
        {
            return new ActivitySample
            {
                Timestamp = now,
                State = ActivityState.Unknown,
                IdleDuration = idleDuration
            };
        }

        AgentSettings settings = SettingsProvider?.Invoke() ?? new AgentSettings();
        if (ProcessIgnore.IsIgnored(app.ProcessName, settings.IgnoredProcesses))
        {
            return new ActivitySample
            {
                Timestamp = now,
                State = ActivityState.Unknown,
                ApplicationName = app.ApplicationName,
                ProcessName = app.ProcessName,
                ProcessId = app.ProcessId,
                IdleDuration = idleDuration,
                SkipAttribution = true
            };
        }

        ActivityState state = idleDuration >= _options.IdleThreshold
            ? ActivityState.Idle
            : ActivityState.Active;

        return new ActivitySample
        {
            Timestamp = now,
            State = state,
            ApplicationName = app.ApplicationName,
            ProcessName = app.ProcessName,
            ProcessId = app.ProcessId,
            ExecutablePath = app.ExecutablePath,
            IdleDuration = idleDuration
        };
    }

    public SessionUpdateResult? ProcessSample(ActivitySample sample)
    {
        SessionUpdateResult? update = _sessionTracker.Apply(sample);

        if (update is null)
        {
            SampleProcessed?.Invoke(sample, null);
            return null;
        }

        PersistUpdate(update, sample.Timestamp);

        if (!_options.QuietConsole)
        {
            bool meaningfulChange =
                update.SessionStarted
                || update.ApplicationChanged
                || update.StateChanged;

            bool shouldPrint = meaningfulChange || ShouldPrintPeriodic(sample);

            if (shouldPrint)
            {
                PrintStatusLine(sample);

                if (meaningfulChange)
                {
                    PrintCurrentSessionSummary(update.CurrentSession);
                }

                _lastPrintedAt = sample.Timestamp;
                _lastPrintedProcess = sample.ProcessName;
                _lastPrintedState = sample.State;
            }
        }

        SampleProcessed?.Invoke(sample, update);
        return update;
    }

    public async Task RunAsync(CancellationToken cancellationToken, WaitHandle? stopSignal = null)
    {
        PrintBanner();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (stopSignal is not null && stopSignal.WaitOne(0))
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] STOP requested");
                break;
            }

            try
            {
                ActivitySample sample = CollectSample();
                if (sample.SkipAttribution)
                {
                    CloseCurrentSessionIfAny(sample.Timestamp);
                    SampleProcessed?.Invoke(sample, null);
                }
                else
                {
                    ProcessSample(sample);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] ERROR           {ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                if (stopSignal is null)
                {
                    await Task.Delay(_options.SamplingInterval, cancellationToken);
                }
                else
                {
                    // Wake early if a stop signal arrives during the sampling wait.
                    bool stopRequested = await Task.Run(
                        () => stopSignal.WaitOne(_options.SamplingInterval),
                        cancellationToken);

                    if (stopRequested)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] STOP requested");
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        UsageSession? closed = _sessionTracker.EndCurrentSession(DateTimeOffset.UtcNow);
        if (closed is not null)
        {
            TryPersist(closed);

            Console.WriteLine();
            Console.WriteLine("----------------------------------------");
            Console.WriteLine("Final Session (saved)");
            PrintSessionDetails(closed);
            Console.WriteLine("----------------------------------------");
        }
    }

    private void CloseCurrentSessionIfAny(DateTimeOffset at)
    {
        UsageSession? closed = _sessionTracker.EndCurrentSession(at);
        if (closed is not null)
        {
            TryPersist(closed);
        }
    }

    private void PersistUpdate(SessionUpdateResult update, DateTimeOffset timestamp)
    {
        if (_sessionStore is null)
        {
            return;
        }

        if (update.ClosedSession is not null)
        {
            TryPersist(update.ClosedSession);
        }

        bool shouldPersistCurrent =
            update.SessionStarted
            || update.StateChanged
            || timestamp - _lastPersistedAt >= _options.PersistenceInterval;

        if (shouldPersistCurrent)
        {
            TryPersist(update.CurrentSession);
            _lastPersistedAt = timestamp;
        }
    }

    private void TryPersist(UsageSession session)
    {
        if (_sessionStore is null)
        {
            return;
        }

        try
        {
            _sessionStore.Upsert(session);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] SAVE ERROR      {ex.Message}");
        }
    }

    private bool ShouldPrintPeriodic(ActivitySample sample)
    {
        if (sample.State == ActivityState.Unknown || sample.ProcessName is null)
        {
            return false;
        }

        bool sameAsLast =
            string.Equals(_lastPrintedProcess, sample.ProcessName, StringComparison.OrdinalIgnoreCase)
            && _lastPrintedState == sample.State;

        if (!sameAsLast)
        {
            return true;
        }

        return sample.Timestamp - _lastPrintedAt >= _options.PeriodicStatusInterval;
    }

    private void PrintBanner()
    {
        if (_options.QuietConsole)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] LyfStack agent started (quiet mode)");
            if (_sessionStore is not null)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Database: {_sessionStore.DatabasePath}");
            }

            return;
        }

        Console.WriteLine("========================================");
        Console.WriteLine(" LyfStack Windows Device Activity Agent");
        Console.WriteLine(" Development Mode - Phase 4 (Tray GUI)");
        Console.WriteLine("========================================");
        Console.WriteLine();
        Console.WriteLine($"Sampling interval: {_options.SamplingInterval.TotalSeconds:0} seconds");
        Console.WriteLine($"Idle threshold: {_options.IdleThreshold.TotalMinutes:0} minutes");
        if (_sessionStore is not null)
        {
            Console.WriteLine($"Database: {_sessionStore.DatabasePath}");
            Console.WriteLine($"Saved sessions: {_sessionStore.Count()}");
        }

        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();
    }

    private static void PrintStatusLine(ActivitySample sample)
    {
        string localTime = sample.Timestamp.ToLocalTime().ToString("HH:mm:ss");
        string process = (sample.ProcessName ?? "Unknown").PadRight(16);
        string state = sample.State.ToString().ToUpperInvariant();
        Console.WriteLine($"[{localTime}] {process} {state}");
    }

    private static void PrintCurrentSessionSummary(UsageSession session)
    {
        Console.WriteLine();
        Console.WriteLine("----------------------------------------");
        Console.WriteLine("Current Session");
        PrintSessionDetails(session);
        Console.WriteLine("----------------------------------------");
        Console.WriteLine();
    }

    private static void PrintSessionDetails(UsageSession session)
    {
        Console.WriteLine($"Application: {session.ApplicationName}");
        Console.WriteLine($"Process:     {session.ProcessName} (PID {session.ProcessId})");
        Console.WriteLine($"Started:     {session.StartedAt.ToLocalTime():HH:mm:ss}");
        if (session.EndedAt is not null)
        {
            Console.WriteLine($"Ended:       {session.EndedAt.Value.ToLocalTime():HH:mm:ss}");
        }

        Console.WriteLine($"Active:      {FormatDuration(session.ActiveDuration)}");
        Console.WriteLine($"Idle:        {FormatDuration(session.IdleDuration)}");
    }

    private static string FormatDuration(TimeSpan value)
    {
        return value.ToString(@"hh\:mm\:ss");
    }
}
