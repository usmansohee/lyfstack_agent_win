using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LyfStack.Agent.Windows.Configuration;
using LyfStack.Agent.Windows.Models;

namespace LyfStack.Agent.Windows.Sync;

public sealed class HttpActivitySyncClient : IActivitySyncClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;

    public HttpActivitySyncClient(string endpointUrl, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
        EndpointUrl = endpointUrl;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string EndpointUrl { get; set; }

    public async Task<SyncResult> PushSessionsAsync(
        IReadOnlyList<UsageSession> sessions,
        string trigger,
        CancellationToken cancellationToken = default)
    {
        object payload = SyncPayloadFactory.Create(sessions);

        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                EndpointUrl,
                payload,
                JsonOptions,
                cancellationToken);

            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            bool success = response.IsSuccessStatusCode;
            string message = success
                ? $"Synced {sessions.Count} sessions via {trigger} ({(int)response.StatusCode})"
                : $"Sync failed ({(int)response.StatusCode}): {Trim(body, 180)}";

            return new SyncResult
            {
                Success = success,
                Message = message,
                SessionCount = sessions.Count,
                SyncedAt = DateTimeOffset.UtcNow,
                HttpStatus = (int)response.StatusCode,
                Trigger = trigger
            };
        }
        catch (Exception ex)
        {
            return new SyncResult
            {
                Success = false,
                Message = $"Sync error: {ex.Message}",
                SessionCount = sessions.Count,
                SyncedAt = DateTimeOffset.UtcNow,
                Trigger = trigger
            };
        }
    }

    public static LastSyncInfo? LoadLastSync()
    {
        try
        {
            if (!File.Exists(AgentPaths.LastSyncPath))
            {
                return null;
            }

            string json = File.ReadAllText(AgentPaths.LastSyncPath);
            return JsonSerializer.Deserialize<LastSyncInfo>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveLastSync(LastSyncInfo info)
    {
        try
        {
            AgentPaths.EnsureDataDirectory();
            string json = JsonSerializer.Serialize(info, JsonOptions);
            File.WriteAllText(AgentPaths.LastSyncPath, json);
        }
        catch
        {
            // Best-effort local cache.
        }
    }

    private static string Trim(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= max ? compact : compact[..max] + "...";
    }
}
