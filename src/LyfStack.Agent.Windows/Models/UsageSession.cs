namespace LyfStack.Agent.Windows.Models;

public sealed class UsageSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string ApplicationName { get; init; }
    public required string ProcessName { get; init; }
    public int ProcessId { get; init; }
    public string? ExecutablePath { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; set; }
    public TimeSpan ActiveDuration { get; set; }
    public TimeSpan IdleDuration { get; set; }
    public ActivityState LastState { get; set; } = ActivityState.Unknown;

    public bool IsOpen => EndedAt is null;

    public TimeSpan TotalDuration => ActiveDuration + IdleDuration;
}
