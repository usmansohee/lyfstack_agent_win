using LyfStack.Agent.Windows.Native;

namespace LyfStack.Agent.Windows.Collectors;

/// <summary>
/// Uses GetLastInputInfo to measure time since the last keyboard/mouse input.
/// Does not install hooks or capture key contents.
/// </summary>
public sealed class IdleDetector
{
    public TimeSpan GetIdleDuration()
    {
        var lastInput = new WindowsApi.LastInputInfo
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WindowsApi.LastInputInfo>()
        };

        if (!WindowsApi.GetLastInputInfo(ref lastInput))
        {
            return TimeSpan.Zero;
        }

        // dwTime and TickCount are 32-bit millisecond counters; unsigned subtract handles wraparound.
        uint idleMilliseconds = unchecked((uint)Environment.TickCount - lastInput.dwTime);
        return TimeSpan.FromMilliseconds(idleMilliseconds);
    }

    public bool IsIdle(TimeSpan idleThreshold)
    {
        if (idleThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleThreshold), "Idle threshold must be positive.");
        }

        return GetIdleDuration() >= idleThreshold;
    }
}
