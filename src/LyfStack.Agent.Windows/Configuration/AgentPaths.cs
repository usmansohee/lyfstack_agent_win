namespace LyfStack.Agent.Windows.Configuration;

public static class AgentPaths
{
    public static string DataDirectory
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "LyfStack", "WindowsAgent");
        }
    }

    public static string AppDirectory => Path.Combine(DataDirectory, "app");

    public static string DatabasePath => Path.Combine(DataDirectory, "activity.db");

    public static string LogPath => Path.Combine(DataDirectory, "agent.log");

    public static string LastSyncPath => Path.Combine(DataDirectory, "last-sync.json");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public static string InstalledExePath => Path.Combine(AppDirectory, "LyfStack.Agent.Windows.exe");

    public static void EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
    }
}
