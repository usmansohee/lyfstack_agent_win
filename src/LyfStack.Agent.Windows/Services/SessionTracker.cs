using LyfStack.Agent.Windows.Models;

namespace LyfStack.Agent.Windows.Services;

public sealed class SessionUpdateResult
{
    public required UsageSession CurrentSession { get; init; }
    public UsageSession? ClosedSession { get; init; }
    public bool SessionStarted { get; init; }
    public bool ApplicationChanged { get; init; }
    public bool StateChanged { get; init; }
}

/// <summary>
/// Pure session aggregation logic — unit-testable without Windows APIs.
/// </summary>
public sealed class SessionTracker
{
    private UsageSession? _current;
    private DateTimeOffset? _lastSampleAt;
    private readonly List<UsageSession> _closedSessions = new();

    public UsageSession? CurrentSession => _current;
    public IReadOnlyList<UsageSession> ClosedSessions => _closedSessions;

    public SessionUpdateResult? Apply(ActivitySample sample)
    {
        if (sample.State == ActivityState.Unknown
            || string.IsNullOrWhiteSpace(sample.ProcessName)
            || sample.ProcessId is null)
        {
            // Unknown/unavailable foreground: freeze timing until we have a usable sample.
            _lastSampleAt = sample.Timestamp;
            return null;
        }

        TimeSpan elapsed = TimeSpan.Zero;
        if (_lastSampleAt is not null)
        {
            elapsed = sample.Timestamp - _lastSampleAt.Value;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }
        }

        _lastSampleAt = sample.Timestamp;

        bool applicationChanged = _current is not null
            && !string.Equals(_current.ProcessName, sample.ProcessName, StringComparison.OrdinalIgnoreCase);

        UsageSession? closed = null;
        bool sessionStarted = false;
        bool stateChanged = false;

        if (_current is null)
        {
            _current = StartSession(sample);
            sessionStarted = true;
            stateChanged = true;
        }
        else if (applicationChanged)
        {
            closed = CloseCurrent(sample.Timestamp);
            _current = StartSession(sample);
            sessionStarted = true;
            stateChanged = true;
        }
        else
        {
            // Same application: attribute elapsed time to previous state, then apply new state.
            if (elapsed > TimeSpan.Zero)
            {
                AttributeElapsed(_current, elapsed);
            }

            if (_current.LastState != sample.State)
            {
                _current.LastState = sample.State;
                stateChanged = true;
            }
        }

        return new SessionUpdateResult
        {
            CurrentSession = _current,
            ClosedSession = closed,
            SessionStarted = sessionStarted,
            ApplicationChanged = applicationChanged,
            StateChanged = stateChanged
        };
    }

    public UsageSession? EndCurrentSession(DateTimeOffset endedAt)
    {
        if (_current is null)
        {
            return null;
        }

        if (_lastSampleAt is not null)
        {
            TimeSpan elapsed = endedAt - _lastSampleAt.Value;
            if (elapsed > TimeSpan.Zero)
            {
                AttributeElapsed(_current, elapsed);
            }
        }

        return CloseCurrent(endedAt);
    }

    private static UsageSession StartSession(ActivitySample sample)
    {
        return new UsageSession
        {
            ApplicationName = sample.ApplicationName ?? sample.ProcessName!,
            ProcessName = sample.ProcessName!,
            ProcessId = sample.ProcessId!.Value,
            ExecutablePath = sample.ExecutablePath,
            StartedAt = sample.Timestamp,
            LastState = sample.State,
            ActiveDuration = TimeSpan.Zero,
            IdleDuration = TimeSpan.Zero
        };
    }

    private UsageSession CloseCurrent(DateTimeOffset endedAt)
    {
        if (_current is null)
        {
            throw new InvalidOperationException("No current session to close.");
        }

        _current.EndedAt = endedAt;
        UsageSession closed = _current;
        _closedSessions.Add(closed);
        _current = null;
        return closed;
    }

    private static void AttributeElapsed(UsageSession session, TimeSpan elapsed)
    {
        switch (session.LastState)
        {
            case ActivityState.Active:
                session.ActiveDuration += elapsed;
                break;
            case ActivityState.Idle:
                session.IdleDuration += elapsed;
                break;
            case ActivityState.Unknown:
                break;
        }
    }
}
