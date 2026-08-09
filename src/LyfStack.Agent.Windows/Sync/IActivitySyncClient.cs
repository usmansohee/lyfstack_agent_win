using LyfStack.Agent.Windows.Models;
using LyfStack.Agent.Windows.Persistence;
using LyfStack.Agent.Windows.Configuration;

namespace LyfStack.Agent.Windows.Sync;

/// <summary>
/// Transport for posting aggregated session payloads to LyfStack
/// <c>POST /api/v1/device-activity/sync</c>.
/// </summary>
public interface IActivitySyncClient
{
    string EndpointUrl { get; set; }

    Task<SyncResult> PushSessionsAsync(
        IReadOnlyList<UsageSession> sessions,
        string trigger,
        SyncRangeQuery? range = null,
        CancellationToken cancellationToken = default);
}

public static class SyncPayloadFactory
{
    /// <summary>
    /// Builds an aggregated payload from usage sessions — never raw 5-second samples.
    /// </summary>
    public static object Create(
        IReadOnlyList<UsageSession> sessions,
        SyncRangeQuery? range = null,
        Guid? deviceId = null)
    {
        ResolvedSyncWindow window = (range ?? SyncRangeQuery.SinceLast).Resolve();
        DeviceProfile profile = DeviceProfileStore.LoadOrCreate();

        return new
        {
            source = "LyfStack.Agent.Windows",
            deviceId = (deviceId ?? profile.DeviceId).ToString("D"),
            device = Environment.MachineName,
            platform = "windows",
            exportedAt = DateTimeOffset.UtcNow,
            aggregation = "usage_sessions",
            sync = new
            {
                range = SyncRangeQuery.ToRangeParam(window.Range),
                from = window.From,
                to = window.To,
                pendingOnly = window.PendingOnly
            },
            note = window.PendingOnly
                ? "Payload contains aggregated sessions that are new or changed since last sync."
                : "Payload contains aggregated sessions for the requested range.",
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

    public static string BuildRequestUrl(string endpointUrl, SyncRangeQuery range)
    {
        string baseUrl = endpointUrl.Trim();
        string query = range.ToQueryString();

        // Strip existing range/from/to so caller can re-apply.
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
        {
            var kept = new List<string>();
            string q = uri.Query.TrimStart('?');
            if (!string.IsNullOrEmpty(q))
            {
                foreach (string part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    string key = part.Split('=')[0];
                    if (key is "range" or "from" or "to")
                    {
                        continue;
                    }

                    kept.Add(part);
                }
            }

            string path = uri.GetLeftPart(UriPartial.Path);
            var all = new List<string>(kept) { query };
            return path + "?" + string.Join("&", all.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        int qIndex = baseUrl.IndexOf('?', StringComparison.Ordinal);
        string root = qIndex >= 0 ? baseUrl[..qIndex] : baseUrl;
        return root + "?" + query;
    }
}
