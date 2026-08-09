using System.Runtime.InteropServices;

namespace LyfStack.Agent.Windows.Native;

/// <summary>
/// Thin P/Invoke wrappers for Windows user32 APIs used by activity collection.
/// Business logic must not call these directly — use collectors instead.
/// </summary>
internal static class WindowsApi
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [StructLayout(LayoutKind.Sequential)]
    public struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }
}
