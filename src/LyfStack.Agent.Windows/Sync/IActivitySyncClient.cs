using LyfStack.Agent.Windows.Models;
using LyfStack.Agent.Windows.Persistence;

namespace LyfStack.Agent.Windows.Sync;

/// <summary>
/// Transport for posting aggregated session payloads.
/// Endpoint stays configurable until LyfStack /device-activity/sync exists.
/// </summary>
public interface IActivitySyncClient
{
    string EndpointUrl { get; set; }

    Task<SyncResult> PushSessionsAsync(
        IReadOnlyList<UsageSession> sessions,
        string trigger,
        CancellationToken cancellationToken = default);
}

public static class SyncPayloadFactory
{
    /// <summary>
    /// Builds an aggregated payload from usage sessions — never raw 5-second samples.
    /// </summary>
    public static object Create(IReadOnlyList<UsageSession> sessions)
    {
        return new
        {
            source = "LyfStack.Agent.Windows",
            device = Environment.MachineName,
            platform = "windows",
            exportedAt = DateTimeOffset.UtcNow,
            aggregation = "usage_sessions",
            note = "Payload contains aggregated sessions that are new or changed since last sync.",
            sessionCount = sessions.Count,
            sessions = sessions.Select(s => new
            {
                id = s.Id,
                applicationName = s.ApplicationName,
                processName = s.ProcessName,
                processId = s.ProcessId,
                startedAt = s.StartedAt,
                endedAt = s.EndedAt,
                activeDurationSeconds = (int)s.ActiveDuration.TotalSeconds,
                idleDurationSeconds = (int)s.IdleDuration.TotalSeconds,
                lastState = s.LastState.ToString(),
                isOpen = s.IsOpen
            })
        };
    }
}
