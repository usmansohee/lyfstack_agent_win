using LyfStack.Agent.Windows.Models;
using Microsoft.Data.Sqlite;

namespace LyfStack.Agent.Windows.Persistence;

/// <summary>
/// Local SQLite persistence for usage sessions. No networking.
/// </summary>
public sealed class SqliteSessionStore : IDisposable
{
    private readonly string _connectionString;
    private bool _initialized;

    public SqliteSessionStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string? directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public string DatabasePath { get; }

    public void Initialize()
    {
        using var connection = OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS usage_sessions (
                    id TEXT NOT NULL PRIMARY KEY,
                    application_name TEXT NOT NULL,
                    process_name TEXT NOT NULL,
                    process_id INTEGER NOT NULL,
                    started_at TEXT NOT NULL,
                    ended_at TEXT NULL,
                    active_duration_ms INTEGER NOT NULL,
                    idle_duration_ms INTEGER NOT NULL,
                    last_state TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    synced_at TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_usage_sessions_started_at
                    ON usage_sessions (started_at DESC);
                """;
            command.ExecuteNonQuery();
        }

        EnsureColumn(connection, "synced_at", "TEXT NULL");
        EnsureColumn(connection, "executable_path", "TEXT NULL");
        _initialized = true;
    }

    public void Upsert(UsageSession session)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO usage_sessions (
                id, application_name, process_name, process_id,
                started_at, ended_at, active_duration_ms, idle_duration_ms,
                last_state, updated_at, synced_at, executable_path
            )
            VALUES (
                $id, $application_name, $process_name, $process_id,
                $started_at, $ended_at, $active_duration_ms, $idle_duration_ms,
                $last_state, $updated_at, NULL, $executable_path
            )
            ON CONFLICT(id) DO UPDATE SET
                application_name = excluded.application_name,
                process_name = excluded.process_name,
                process_id = excluded.process_id,
                started_at = excluded.started_at,
                ended_at = excluded.ended_at,
                active_duration_ms = excluded.active_duration_ms,
                idle_duration_ms = excluded.idle_duration_ms,
                last_state = excluded.last_state,
                updated_at = excluded.updated_at,
                executable_path = excluded.executable_path,
                synced_at = CASE
                    WHEN usage_sessions.synced_at IS NULL THEN NULL
                    WHEN excluded.updated_at > usage_sessions.synced_at THEN NULL
                    ELSE usage_sessions.synced_at
                END;
            """;

        BindSession(command, session);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<UsageSession> GetRecent(int limit = 20)
    {
        EnsureInitialized();
        if (limit <= 0)
        {
            return Array.Empty<UsageSession>();
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, application_name, process_name, process_id,
                started_at, ended_at, active_duration_ms, idle_duration_ms, last_state,
                executable_path
            FROM usage_sessions
            ORDER BY started_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var sessions = new List<UsageSession>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    /// <summary>
    /// Sessions that are new or changed since last successful sync.
    /// </summary>
    public IReadOnlyList<UsageSession> GetPendingSync(int limit = 500)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id, application_name, process_name, process_id,
                started_at, ended_at, active_duration_ms, idle_duration_ms, last_state,
                executable_path
            FROM usage_sessions
            WHERE synced_at IS NULL
               OR updated_at > synced_at
            ORDER BY started_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var sessions = new List<UsageSession>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            sessions.Add(ReadSession(reader));
        }

        return sessions;
    }

    public int CountPendingSync()
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM usage_sessions
            WHERE synced_at IS NULL
               OR updated_at > synced_at;
            """;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void MarkSynced(IEnumerable<Guid> sessionIds, DateTimeOffset syncedAt)
    {
        EnsureInitialized();
        List<Guid> ids = sessionIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        using var connection = OpenConnection();
        using var tx = connection.BeginTransaction();
        foreach (Guid id in ids)
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText =
                """
                UPDATE usage_sessions
                SET synced_at = $synced_at
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$synced_at", ToIso(syncedAt));
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public int Count()
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM usage_sessions;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public int CloseOpenSessions(DateTimeOffset endedAt)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE usage_sessions
            SET ended_at = $ended_at,
                updated_at = $updated_at,
                synced_at = NULL
            WHERE ended_at IS NULL;
            """;
        command.Parameters.AddWithValue("$ended_at", ToIso(endedAt));
        command.Parameters.AddWithValue("$updated_at", ToIso(DateTimeOffset.UtcNow));
        return command.ExecuteNonQuery();
    }

    public int CountOpenSessions()
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM usage_sessions WHERE ended_at IS NULL;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public void Dispose()
    {
    }

    private static void EnsureColumn(SqliteConnection connection, string columnName, string columnType)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(usage_sessions);";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE usage_sessions ADD COLUMN {columnName} {columnType};";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            Initialize();
        }
    }

    private static void BindSession(SqliteCommand command, UsageSession session)
    {
        command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
        command.Parameters.AddWithValue("$application_name", session.ApplicationName);
        command.Parameters.AddWithValue("$process_name", session.ProcessName);
        command.Parameters.AddWithValue("$process_id", session.ProcessId);
        command.Parameters.AddWithValue("$started_at", ToIso(session.StartedAt));
        command.Parameters.AddWithValue(
            "$ended_at",
            session.EndedAt is null ? DBNull.Value : ToIso(session.EndedAt.Value));
        command.Parameters.AddWithValue("$active_duration_ms", (long)session.ActiveDuration.TotalMilliseconds);
        command.Parameters.AddWithValue("$idle_duration_ms", (long)session.IdleDuration.TotalMilliseconds);
        command.Parameters.AddWithValue("$last_state", session.LastState.ToString());
        command.Parameters.AddWithValue("$updated_at", ToIso(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue(
            "$executable_path",
            string.IsNullOrWhiteSpace(session.ExecutablePath) ? DBNull.Value : session.ExecutablePath);
    }

    private static UsageSession ReadSession(SqliteDataReader reader)
    {
        string? endedAtRaw = reader.IsDBNull(5) ? null : reader.GetString(5);
        string? executablePath = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetString(9) : null;

        return new UsageSession
        {
            Id = Guid.Parse(reader.GetString(0)),
            ApplicationName = reader.GetString(1),
            ProcessName = reader.GetString(2),
            ProcessId = reader.GetInt32(3),
            StartedAt = DateTimeOffset.Parse(reader.GetString(4)),
            EndedAt = endedAtRaw is null ? null : DateTimeOffset.Parse(endedAtRaw),
            ActiveDuration = TimeSpan.FromMilliseconds(reader.GetInt64(6)),
            IdleDuration = TimeSpan.FromMilliseconds(reader.GetInt64(7)),
            LastState = Enum.Parse<ActivityState>(reader.GetString(8)),
            ExecutablePath = executablePath
        };
    }

    private static string ToIso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
}
