namespace LyfStack.Agent.Windows.Sync;

public sealed class SyncResult
{
    public required bool Success { get; init; }
    public required string Message { get; init; }
    public int SessionCount { get; init; }
    public DateTimeOffset? SyncedAt { get; init; }
    public int? HttpStatus { get; init; }
    public string Trigger { get; init; } = "manual";
}

public sealed class LastSyncInfo
{
    public DateTimeOffset SyncedAt { get; set; }
    public int SessionCount { get; set; }
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Trigger { get; set; }
    public string? Endpoint { get; set; }
}
