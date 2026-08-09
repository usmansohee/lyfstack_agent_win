using LyfStack.Agent.Windows.Configuration;
using LyfStack.Agent.Windows.Models;
using LyfStack.Agent.Windows.Persistence;

namespace LyfStack.Agent.Windows.Sync;

/// <summary>
/// Coordinates manual Sync Now + automatic periodic sync of aggregated SQLite sessions.
/// Does not upload raw 5-second samples.
/// </summary>
public sealed class SyncService : IAsyncDisposable
{
    private readonly SqliteSessionStore _store;
    private readonly IActivitySyncClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private AgentSettings _settings;

    public SyncService(SqliteSessionStore store, IActivitySyncClient client, AgentSettings settings)
    {
        _store = store;
        _client = client;
        _settings = settings;
        _client.EndpointUrl = settings.SyncEndpointUrl;
    }

    public event Action<SyncResult>? SyncCompleted;

    public AgentSettings Settings => _settings;

    public DateTimeOffset? NextScheduledSyncUtc { get; private set; }

    public void ApplySettings(AgentSettings settings)
    {
        _settings = settings;
        _client.EndpointUrl = settings.SyncEndpointUrl;
        RestartPeriodicSync();
    }

    public void StartPeriodicSync()
    {
        RestartPeriodicSync();
    }

    public async Task<SyncResult> SyncNowAsync(string trigger = "manual", CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Incremental: only new/changed aggregated sessions.
            IReadOnlyList<UsageSession> sessions = _store.GetPendingSync(500);
            if (sessions.Count == 0)
            {
                var empty = new SyncResult
                {
                    Success = true,
                    Message = "Nothing new to sync",
                    SessionCount = 0,
                    SyncedAt = DateTimeOffset.UtcNow,
                    Trigger = trigger
                };

                HttpActivitySyncClient.SaveLastSync(new LastSyncInfo
                {
                    SyncedAt = empty.SyncedAt.Value,
                    SessionCount = 0,
                    Success = true,
                    Message = empty.Message,
                    Trigger = trigger,
                    Endpoint = _client.EndpointUrl
                });

                SyncCompleted?.Invoke(empty);
                return empty;
            }

            SyncResult result = await _client.PushSessionsAsync(sessions, trigger, cancellationToken);

            if (result.Success)
            {
                DateTimeOffset syncedAt = result.SyncedAt ?? DateTimeOffset.UtcNow;
                _store.MarkSynced(sessions.Select(s => s.Id), syncedAt);
                DeviceProfileStore.MarkFirstSyncIfNeeded(syncedAt);
            }

            HttpActivitySyncClient.SaveLastSync(new LastSyncInfo
            {
                SyncedAt = result.SyncedAt ?? DateTimeOffset.UtcNow,
                SessionCount = result.SessionCount,
                Success = result.Success,
                Message = result.Message,
                Trigger = result.Trigger,
                Endpoint = _client.EndpointUrl
            });

            SyncCompleted?.Invoke(result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RestartPeriodicSync()
    {
        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = new CancellationTokenSource();
        CancellationToken token = _loopCts.Token;

        if (!_settings.AutoSyncEnabled)
        {
            NextScheduledSyncUtc = null;
            _loopTask = null;
            return;
        }

        int minutes = Math.Clamp(_settings.AutoSyncIntervalMinutes, 5, 120);
        _loopTask = Task.Run(() => RunPeriodicLoopAsync(TimeSpan.FromMinutes(minutes), token), token);
    }

    private async Task RunPeriodicLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NextScheduledSyncUtc = DateTimeOffset.UtcNow.Add(interval);

            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                SyncResult result = await SyncNowAsync("automatic", cancellationToken);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] AUTO SYNC      {result.Message}");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] AUTO SYNC ERR  {ex.Message}");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync();
            _loopCts.Dispose();
        }

        _gate.Dispose();
    }
}
