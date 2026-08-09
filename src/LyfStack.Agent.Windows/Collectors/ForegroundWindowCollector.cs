using System.Diagnostics;
using LyfStack.Agent.Windows.Native;

namespace LyfStack.Agent.Windows.Collectors;

public sealed class ForegroundAppInfo
{
    public required IntPtr WindowHandle { get; init; }
    public required int ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required string ApplicationName { get; init; }
    public string? ExecutablePath { get; init; }
}

/// <summary>
/// Resolves the current foreground window to process metadata.
/// </summary>
public sealed class ForegroundWindowCollector
{
    public bool TryGetForegroundApp(out ForegroundAppInfo? info)
    {
        info = null;

        try
        {
            IntPtr hwnd = WindowsApi.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            WindowsApi.GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0)
            {
                return false;
            }

            using Process process = Process.GetProcessById((int)processId);
            string processName = SafeGetProcessName(process);
            string applicationName = SafeGetApplicationName(process, processName);
            string? executablePath = SafeGetExecutablePath(process);

            info = new ForegroundAppInfo
            {
                WindowHandle = hwnd,
                ProcessId = (int)processId,
                ProcessName = processName,
                ApplicationName = applicationName,
                ExecutablePath = executablePath
            };

            return true;
        }
        catch (ArgumentException)
        {
            // Process disappeared between PID lookup and Process.GetProcessById.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Protected / elevated / system process access denied.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string SafeGetProcessName(Process process)
    {
        try
        {
            string name = process.ProcessName;
            return string.IsNullOrWhiteSpace(name) ? "Unknown" : $"{name}.exe";
        }
        catch
        {
            return "Unknown";
        }
    }

    private static string SafeGetApplicationName(Process process, string fallbackProcessName)
    {
        try
        {
            // MainModule can throw for protected processes; never required for Phase 1.
            string? description = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }
        }
        catch
        {
            // Ignore access failures and fall back to process name.
        }

        return fallbackProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fallbackProcessName[..^4]
            : fallbackProcessName;
    }

    private static string? SafeGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
