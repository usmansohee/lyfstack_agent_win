using System.Diagnostics;
using System.Runtime.InteropServices;
using LyfStack.Agent.Windows.Configuration;
using LyfStack.Agent.Windows.Hosting;
using LyfStack.Agent.Windows.Models;
using LyfStack.Agent.Windows.Persistence;
using LyfStack.Agent.Windows.Services;
using LyfStack.Agent.Windows.Sync;
using LyfStack.Agent.Windows.UI;

namespace LyfStack.Agent.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        return MainAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> MainAsync(string[] args)
    {
        bool headless = HasFlag(args, "--headless");
        bool tray = HasFlag(args, "--tray") || (!headless && !IsManagementCommand(args));
        bool isManagementCommand = IsManagementCommand(args);

        if (isManagementCommand || (!tray && !headless))
        {
            EnsureConsole();
        }

        IDisposable? logRedirect = null;
        if ((headless || tray) && !isManagementCommand)
        {
            AgentPaths.EnsureDataDirectory();
            logRedirect = ConsoleBootstrap.RedirectToLogFile(AgentPaths.LogPath);
        }

        try
        {
            if (HasFlag(args, "--help") || HasFlag(args, "-h"))
            {
                PrintHelp();
                return 0;
            }

            if (HasFlag(args, "--status"))
            {
                return ShowStatus();
            }

            if (HasFlag(args, "--stop"))
            {
                return StopRunningAgent();
            }

            if (HasFlag(args, "--install-startup"))
            {
                return InstallStartup();
            }

            if (HasFlag(args, "--uninstall-startup"))
            {
                return UninstallStartup();
            }

            if (HasFlag(args, "--list") || HasFlag(args, "-l"))
            {
                return ListRecentSessions(args);
            }

            if (tray)
            {
                return RunTrayUi(args, startHidden: HasFlag(args, "--tray"));
            }

            return await RunAgentAsync(args, headless);
        }
        finally
        {
            logRedirect?.Dispose();
        }
    }

    private static bool IsManagementCommand(string[] args) =>
        HasFlag(args, "--list") || HasFlag(args, "-l")
        || HasFlag(args, "--install-startup")
        || HasFlag(args, "--uninstall-startup")
        || HasFlag(args, "--status")
        || HasFlag(args, "--stop")
        || HasFlag(args, "--help") || HasFlag(args, "-h");

    private static int RunTrayUi(string[] args, bool startHidden)
    {
        if (!AgentProcessControl.TryAcquire(out AgentProcessControl? processControl) || processControl is null)
        {
            EnsureConsole();
            Console.WriteLine("LyfStack agent is already running.");
            Console.WriteLine("Open it from the tray icon, or run --stop first.");
            return 1;
        }

        if (startHidden)
        {
            TrayAppHost.TryDetachConsole();
        }

        using (processControl)
        {
            AgentOptions parsed = ParseOptions(args);
            var options = new AgentOptions
            {
                SamplingInterval = parsed.SamplingInterval,
                IdleThreshold = parsed.IdleThreshold,
                PeriodicStatusInterval = parsed.PeriodicStatusInterval,
                PersistenceInterval = parsed.PersistenceInterval,
                DatabasePath = parsed.DatabasePath,
                SyncWebhookUrl = parsed.SyncWebhookUrl,
                QuietConsole = true
            };

            string databasePath = options.DatabasePath ?? AgentPaths.DatabasePath;
            AgentPaths.EnsureDataDirectory();

            using var store = new SqliteSessionStore(databasePath);
            store.Initialize();
            store.CloseOpenSessions(DateTimeOffset.UtcNow);
            _ = DeviceProfileStore.LoadOrCreate();

            using var cts = new CancellationTokenSource();
            var tracking = new ActivityTrackingService(options, store);
            tracking.SettingsProvider = () => AgentSettingsStore.Load();

            AgentSettings settings = AgentSettingsStore.Load();
            if (!string.IsNullOrWhiteSpace(parsed.SyncWebhookUrl)
                && !string.Equals(parsed.SyncWebhookUrl, settings.SyncEndpointUrl, StringComparison.OrdinalIgnoreCase))
            {
                // CLI --webhook overrides persisted endpoint for this run.
                settings.SyncEndpointUrl = parsed.SyncWebhookUrl;
            }

            var syncClient = new HttpActivitySyncClient(settings.SyncEndpointUrl);
            var sync = new SyncService(store, syncClient, settings);
            var deviceConnection = new DeviceConnectionService(settings);

            using var host = new TrayAppHost(
                options,
                store,
                tracking,
                sync,
                deviceConnection,
                cts,
                processControl.StopHandle,
                startHidden);

            return host.Run();
        }
    }

    private static async Task<int> RunAgentAsync(string[] args, bool headless)
    {
        if (!AgentProcessControl.TryAcquire(out AgentProcessControl? processControl) || processControl is null)
        {
            Console.WriteLine("LyfStack agent is already running.");
            Console.WriteLine("Use: dotnet run --project src/LyfStack.Agent.Windows -- --stop");
            return 1;
        }

        using (processControl)
        {
            AgentOptions parsed = ParseOptions(args);
            var options = new AgentOptions
            {
                SamplingInterval = parsed.SamplingInterval,
                IdleThreshold = parsed.IdleThreshold,
                PeriodicStatusInterval = parsed.PeriodicStatusInterval,
                PersistenceInterval = parsed.PersistenceInterval,
                DatabasePath = parsed.DatabasePath,
                SyncWebhookUrl = parsed.SyncWebhookUrl,
                QuietConsole = headless
            };

            string databasePath = options.DatabasePath ?? AgentPaths.DatabasePath;

            AgentPaths.EnsureDataDirectory();
            using var store = new SqliteSessionStore(databasePath);
            store.Initialize();

            int closedOrphans = store.CloseOpenSessions(DateTimeOffset.UtcNow);
            if (closedOrphans > 0 && !headless)
            {
                Console.WriteLine($"Closed {closedOrphans} leftover open session(s) from a previous run.");
                Console.WriteLine();
            }

            using var cts = new CancellationTokenSource();
            if (!headless)
            {
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };
            }

            var service = new ActivityTrackingService(options, store);

            try
            {
                await service.RunAsync(cts.Token, processControl.StopHandle);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Fatal error: {ex.Message}");
                return 1;
            }
        }
    }

    private static int InstallStartup()
    {
        try
        {
            AgentPaths.EnsureDataDirectory();
            AgentDeployer.DeployCurrentBuild();
            StartupRegistration.Install(AgentDeployer.HiddenLauncherPath);

            Console.WriteLine("Startup installed.");
            Console.WriteLine($"App folder: {AgentPaths.AppDirectory}");
            Console.WriteLine($"Command:    {StartupRegistration.GetInstalledCommand()}");
            Console.WriteLine();
            Console.WriteLine("The agent will start in the tray when you sign in to Windows.");
            Console.WriteLine("Starting it now...");

            Process.Start(new ProcessStartInfo
            {
                FileName = "wscript.exe",
                Arguments = $"//B //Nologo \"{AgentDeployer.HiddenLauncherPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AgentPaths.AppDirectory
            });

            Console.WriteLine("Tray agent started.");
            Console.WriteLine($"Log file: {AgentPaths.LogPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Install failed: {ex.Message}");
            return 1;
        }
    }

    private static int UninstallStartup()
    {
        StartupRegistration.Uninstall();
        bool stopped = AgentProcessControl.RequestStop();

        Console.WriteLine("Startup registration removed.");
        Console.WriteLine(stopped
            ? "Stop signal sent to the running agent."
            : "No running agent was found to stop.");
        Console.WriteLine("Database and logs were kept.");
        return 0;
    }

    private static int StopRunningAgent()
    {
        if (AgentProcessControl.RequestStop())
        {
            Console.WriteLine("Stop signal sent. The agent should exit shortly.");
            return 0;
        }

        Console.WriteLine("No running LyfStack agent was found.");
        return 1;
    }

    private static int ShowStatus()
    {
        Console.WriteLine("========================================");
        Console.WriteLine(" LyfStack Windows Agent - Status");
        Console.WriteLine("========================================");
        Console.WriteLine($"Database:     {AgentPaths.DatabasePath}");
        Console.WriteLine($"Log file:     {AgentPaths.LogPath}");
        Console.WriteLine($"App folder:   {AgentPaths.AppDirectory}");
        AgentSettings settings = AgentSettingsStore.Load();
        Console.WriteLine($"Endpoint:     {settings.SyncEndpointUrl}");
        Console.WriteLine($"Auto-sync:    {(settings.AutoSyncEnabled ? $"On every {settings.AutoSyncIntervalMinutes} min" : "Off")}");
        Console.WriteLine($"Startup:      {(StartupRegistration.IsInstalled() ? "Installed" : "Not installed")}");
        if (StartupRegistration.IsInstalled())
        {
            Console.WriteLine($"Startup cmd:  {StartupRegistration.GetInstalledCommand()}");
        }

        bool running = !AgentProcessControl.TryAcquire(out AgentProcessControl? control);
        if (control is not null)
        {
            control.Dispose();
        }

        Console.WriteLine($"Running now:  {(running ? "Yes" : "No")}");

        LastSyncInfo? last = HttpActivitySyncClient.LoadLastSync();
        Console.WriteLine(last is null
            ? "Last sync:    (never)"
            : $"Last sync:    {last.SyncedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({(last.Success ? "ok" : "failed")}, {last.Trigger})");

        if (File.Exists(AgentPaths.DatabasePath))
        {
            using var store = new SqliteSessionStore(AgentPaths.DatabasePath);
            store.Initialize();
            Console.WriteLine($"Sessions:     {store.Count()} total, {store.CountOpenSessions()} open");
        }
        else
        {
            Console.WriteLine("Sessions:     (no database yet)");
        }

        return 0;
    }

    private static int ListRecentSessions(string[] args)
    {
        AgentOptions options = ParseOptions(args);
        string databasePath = options.DatabasePath ?? AgentPaths.DatabasePath;

        if (!File.Exists(databasePath))
        {
            Console.WriteLine("No database yet. Run the agent first, then use --list.");
            Console.WriteLine($"Expected path: {databasePath}");
            return 0;
        }

        using var store = new SqliteSessionStore(databasePath);
        store.Initialize();

        IReadOnlyList<UsageSession> sessions = store.GetRecent(20);
        Console.WriteLine("========================================");
        Console.WriteLine(" Recent saved sessions");
        Console.WriteLine("========================================");
        Console.WriteLine($"Database: {databasePath}");
        Console.WriteLine($"Total saved: {store.Count()}");
        Console.WriteLine();

        if (sessions.Count == 0)
        {
            Console.WriteLine("No sessions saved yet.");
            return 0;
        }

        foreach (UsageSession session in sessions)
        {
            string ended = session.EndedAt?.ToLocalTime().ToString("HH:mm:ss") ?? "(open)";
            Console.WriteLine(
                $"{session.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}  " +
                $"{session.ProcessName,-16}  " +
                $"active {FormatDuration(session.ActiveDuration)}  " +
                $"idle {FormatDuration(session.IdleDuration)}  " +
                $"ended {ended}");
        }

        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("LyfStack Windows Device Activity Agent");
        Console.WriteLine();
        Console.WriteLine("Run GUI + tray:");
        Console.WriteLine("  dotnet run --project src/LyfStack.Agent.Windows");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  --tray                 Start in tray (window hidden)");
        Console.WriteLine("  --list                 Show recent saved sessions");
        Console.WriteLine("  --status               Show install/run status");
        Console.WriteLine("  --headless             Run without GUI (log file only)");
        Console.WriteLine("  --install-startup      Start automatically with Windows");
        Console.WriteLine("  --uninstall-startup    Remove automatic startup");
        Console.WriteLine("  --stop                 Stop a running agent");
        Console.WriteLine("  --interval <seconds>   Sampling interval (default 5)");
        Console.WriteLine("  --idle-minutes <n>     Idle threshold (default 5)");
    }

    private static AgentOptions ParseOptions(string[] args)
    {
        TimeSpan sampling = TimeSpan.FromSeconds(5);
        TimeSpan idle = TimeSpan.FromMinutes(5);
        string? databasePath = null;
        string webhook = AgentSettingsStore.Load().SyncEndpointUrl;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--interval" or "-i" && i + 1 < args.Length
                && int.TryParse(args[i + 1], out int seconds) && seconds > 0)
            {
                sampling = TimeSpan.FromSeconds(seconds);
                i++;
            }
            else if (args[i] is "--idle-minutes" or "-t" && i + 1 < args.Length
                     && int.TryParse(args[i + 1], out int minutes) && minutes > 0)
            {
                idle = TimeSpan.FromMinutes(minutes);
                i++;
            }
            else if (args[i] is "--db" && i + 1 < args.Length)
            {
                databasePath = args[i + 1];
                i++;
            }
            else if (args[i] is "--webhook" && i + 1 < args.Length)
            {
                webhook = args[i + 1];
                i++;
            }
        }

        return new AgentOptions
        {
            SamplingInterval = sampling,
            IdleThreshold = idle,
            PeriodicStatusInterval = sampling,
            DatabasePath = databasePath,
            SyncWebhookUrl = webhook
        };
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string FormatDuration(TimeSpan value) => value.ToString(@"hh\:mm\:ss");

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    private const uint AttachParentProcess = 0xFFFFFFFF;

    private static void EnsureConsole()
    {
        if (!AttachConsole(AttachParentProcess))
        {
            AllocConsole();
        }

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }
}
