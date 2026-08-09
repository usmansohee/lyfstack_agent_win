using Microsoft.Win32;

namespace LyfStack.Agent.Windows.Hosting;

/// <summary>
/// Registers the agent in the current-user Windows Run key (starts at login).
/// Not a Windows Service — foreground APIs require an interactive user session.
/// </summary>
public static class StartupRegistration
{
    public const string ValueName = "LyfStackWindowsAgent";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsInstalled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string;
    }

    public static string? GetInstalledCommand()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public static void Install(string hiddenLauncherPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hiddenLauncherPath);
        if (!File.Exists(hiddenLauncherPath))
        {
            throw new FileNotFoundException("Hidden launcher script not found.", hiddenLauncherPath);
        }

        string command = $"wscript.exe //B //Nologo \"{hiddenLauncherPath}\"";
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, command);
    }

    public static void Uninstall()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
