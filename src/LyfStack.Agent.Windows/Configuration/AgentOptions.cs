namespace LyfStack.Agent.Windows.Configuration;

public sealed class AgentOptions
{
    /// <summary>How often to sample foreground + idle state.</summary>
    public TimeSpan SamplingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>User is idle when last input is at least this old.</summary>
    public TimeSpan IdleThreshold { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often to reprint status when nothing meaningful changed.</summary>
    public TimeSpan PeriodicStatusInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often to flush the open session to SQLite while it continues.</summary>
    public TimeSpan PersistenceInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional override for the SQLite database path.</summary>
    public string? DatabasePath { get; init; }

    /// <summary>Temporary webhook endpoint used by the Sync button.</summary>
    public string SyncWebhookUrl { get; init; } =
        "https://webhook.site/162a6652-aec2-4bcc-ad8d-d3a4acb63181";

    /// <summary>When true, suppress chatty console sample lines (GUI/tray/log modes).</summary>
    public bool QuietConsole { get; init; }
}
