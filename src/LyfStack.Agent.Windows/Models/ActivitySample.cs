namespace LyfStack.Agent.Windows.Models;

/// <summary>
/// One sampling tick of foreground + idle metadata (no content surveillance).
/// </summary>
public sealed class ActivitySample
{
    public required DateTimeOffset Timestamp { get; init; }
    public required ActivityState State { get; init; }
    public string? ApplicationName { get; init; }
    public string? ProcessName { get; init; }
    public int? ProcessId { get; init; }
    public string? ExecutablePath { get; init; }
    public TimeSpan IdleDuration { get; init; }

    /// <summary>
    /// When true (paused tracking or ignored process), close current session and do not attribute time.
    /// </summary>
    public bool SkipAttribution { get; init; }
}
