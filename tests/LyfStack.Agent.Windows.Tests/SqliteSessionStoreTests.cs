using LyfStack.Agent.Windows.Models;
using LyfStack.Agent.Windows.Persistence;

namespace LyfStack.Agent.Windows.Tests;

public class SqliteSessionStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteSessionStore _store;

    public SqliteSessionStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lyfstack-test-{Guid.NewGuid():N}.db");
        _store = new SqliteSessionStore(_dbPath);
        _store.Initialize();
    }

    [Fact]
    public void Upsert_InsertsAndUpdatesSession()
    {
        var session = new UsageSession
        {
            Id = Guid.NewGuid(),
            ApplicationName = "Cursor",
            ProcessName = "Cursor.exe",
            ProcessId = 123,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ActiveDuration = TimeSpan.FromMinutes(3),
            IdleDuration = TimeSpan.FromMinutes(1),
            LastState = ActivityState.Active
        };

        _store.Upsert(session);
        Assert.Equal(1, _store.Count());

        session.EndedAt = DateTimeOffset.UtcNow;
        session.ActiveDuration = TimeSpan.FromMinutes(4);
        _store.Upsert(session);

        IReadOnlyList<UsageSession> recent = _store.GetRecent(10);
        Assert.Single(recent);
        Assert.Equal(session.Id, recent[0].Id);
        Assert.Equal(TimeSpan.FromMinutes(4), recent[0].ActiveDuration);
        Assert.NotNull(recent[0].EndedAt);
        Assert.Equal(ActivityState.Active, recent[0].LastState);
    }

    [Fact]
    public void GetRecent_ReturnsNewestFirst()
    {
        var older = CreateSession("Chrome.exe", DateTimeOffset.UtcNow.AddHours(-2));
        var newer = CreateSession("Code.exe", DateTimeOffset.UtcNow.AddHours(-1));

        _store.Upsert(older);
        _store.Upsert(newer);

        IReadOnlyList<UsageSession> recent = _store.GetRecent(10);
        Assert.Equal(2, recent.Count);
        Assert.Equal("Code.exe", recent[0].ProcessName);
        Assert.Equal("Chrome.exe", recent[1].ProcessName);
    }

    [Fact]
    public void CloseOpenSessions_ClosesLeftoverRows()
    {
        var open = new UsageSession
        {
            Id = Guid.NewGuid(),
            ApplicationName = "Chrome",
            ProcessName = "chrome.exe",
            ProcessId = 42,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            ActiveDuration = TimeSpan.FromMinutes(2),
            IdleDuration = TimeSpan.Zero,
            LastState = ActivityState.Active
        };

        _store.Upsert(open);
        Assert.Equal(1, _store.CountOpenSessions());

        DateTimeOffset endedAt = DateTimeOffset.UtcNow;
        int closed = _store.CloseOpenSessions(endedAt);

        Assert.Equal(1, closed);
        Assert.Equal(0, _store.CountOpenSessions());
        Assert.NotNull(_store.GetRecent(1)[0].EndedAt);
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }

    private static UsageSession CreateSession(string processName, DateTimeOffset startedAt)
    {
        return new UsageSession
        {
            Id = Guid.NewGuid(),
            ApplicationName = processName,
            ProcessName = processName,
            ProcessId = Random.Shared.Next(1000, 9999),
            StartedAt = startedAt,
            EndedAt = startedAt.AddMinutes(10),
            ActiveDuration = TimeSpan.FromMinutes(8),
            IdleDuration = TimeSpan.FromMinutes(2),
            LastState = ActivityState.Idle
        };
    }
}
