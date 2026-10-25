using System.Globalization;
using FileTimeTracker.Core.Model;
using Microsoft.Data.Sqlite;

namespace FileTimeTracker.Core.Storage;

/// <summary>
/// SQLite implementation of the §4.2 <c>intervals</c> buffer.
/// </summary>
/// <remarks>
/// WAL journalling with <c>synchronous=NORMAL</c>: a laptop lid-slam or a hard power loss can lose
/// at most the last few seconds of sampling, never the database. Uploaded rows are retained for a
/// grace period rather than deleted immediately so a re-run can prove idempotency (§4.3) and an
/// admin can inspect what a machine actually sent.
/// </remarks>
public sealed class SqliteIntervalStore : IIntervalStore
{
    private const string RoundTrip = "O";

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();

    public SqliteIntervalStore(string databasePath)
    {
        if (!databasePath.Equals(":memory:", StringComparison.Ordinal))
        {
            var dir = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            // Kept open for the process lifetime; an in-memory database would otherwise vanish.
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        };

        _connection = new SqliteConnection(builder.ToString());
        _connection.Open();
        Initialize();
    }

    /// <summary>Creates a throwaway in-memory store for tests.</summary>
    public static SqliteIntervalStore InMemory() => new(":memory:");

    private void Initialize()
    {
        Execute("""
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA busy_timeout=5000;

            CREATE TABLE IF NOT EXISTS intervals (
                id            TEXT PRIMARY KEY,
                computer_no   TEXT    NOT NULL,
                win_user      TEXT    NOT NULL,
                app           TEXT    NOT NULL,
                file          TEXT    NOT NULL,
                file_path     TEXT        NULL,
                window_title  TEXT        NULL,
                start_utc     TEXT    NOT NULL,
                end_utc       TEXT    NOT NULL,
                active_sec    INTEGER NOT NULL,
                idle_sec      INTEGER NOT NULL,
                source        TEXT    NOT NULL,
                local_date    TEXT    NOT NULL,
                uploaded      INTEGER NOT NULL DEFAULT 0,
                uploaded_utc  TEXT        NULL,
                created_utc   TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_intervals_pending
                ON intervals (uploaded, created_utc);

            CREATE INDEX IF NOT EXISTS ix_intervals_uploaded_utc
                ON intervals (uploaded_utc);

            CREATE TABLE IF NOT EXISTS agent_state (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
    }

    public int Add(IEnumerable<IntervalRecord> records)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;

            // OR IGNORE makes a replayed batch harmless: the interval id is the idempotency key
            // end to end, from this table through to the aggregator (§4.3).
            cmd.CommandText = """
                INSERT OR IGNORE INTO intervals
                    (id, computer_no, win_user, app, file, file_path, window_title,
                     start_utc, end_utc, active_sec, idle_sec, source, local_date,
                     uploaded, uploaded_utc, created_utc)
                VALUES
                    ($id, $computer_no, $win_user, $app, $file, $file_path, $window_title,
                     $start_utc, $end_utc, $active_sec, $idle_sec, $source, $local_date,
                     0, NULL, $created_utc);
                """;

            var p = new
            {
                Id = cmd.CreateParameter(),
                ComputerNo = cmd.CreateParameter(),
                WinUser = cmd.CreateParameter(),
                App = cmd.CreateParameter(),
                File = cmd.CreateParameter(),
                FilePath = cmd.CreateParameter(),
                WindowTitle = cmd.CreateParameter(),
                StartUtc = cmd.CreateParameter(),
                EndUtc = cmd.CreateParameter(),
                ActiveSec = cmd.CreateParameter(),
                IdleSec = cmd.CreateParameter(),
                Source = cmd.CreateParameter(),
                LocalDate = cmd.CreateParameter(),
                CreatedUtc = cmd.CreateParameter(),
            };

            p.Id.ParameterName = "$id";
            p.ComputerNo.ParameterName = "$computer_no";
            p.WinUser.ParameterName = "$win_user";
            p.App.ParameterName = "$app";
            p.File.ParameterName = "$file";
            p.FilePath.ParameterName = "$file_path";
            p.WindowTitle.ParameterName = "$window_title";
            p.StartUtc.ParameterName = "$start_utc";
            p.EndUtc.ParameterName = "$end_utc";
            p.ActiveSec.ParameterName = "$active_sec";
            p.IdleSec.ParameterName = "$idle_sec";
            p.Source.ParameterName = "$source";
            p.LocalDate.ParameterName = "$local_date";
            p.CreatedUtc.ParameterName = "$created_utc";

            foreach (var parameter in new[]
                     {
                         p.Id, p.ComputerNo, p.WinUser, p.App, p.File, p.FilePath, p.WindowTitle,
                         p.StartUtc, p.EndUtc, p.ActiveSec, p.IdleSec, p.Source, p.LocalDate, p.CreatedUtc,
                     })
            {
                cmd.Parameters.Add(parameter);
            }

            var now = DateTimeOffset.UtcNow.ToString(RoundTrip, CultureInfo.InvariantCulture);
            var inserted = 0;

            foreach (var record in records)
            {
                p.Id.Value = record.Id;
                p.ComputerNo.Value = record.ComputerNo;
                p.WinUser.Value = record.WinUser;
                p.App.Value = record.App;
                p.File.Value = record.File;
                p.FilePath.Value = (object?)record.FilePath ?? DBNull.Value;
                p.WindowTitle.Value = (object?)record.WindowTitle ?? DBNull.Value;
                p.StartUtc.Value = record.StartUtc.ToUniversalTime().ToString(RoundTrip, CultureInfo.InvariantCulture);
                p.EndUtc.Value = record.EndUtc.ToUniversalTime().ToString(RoundTrip, CultureInfo.InvariantCulture);
                p.ActiveSec.Value = record.ActiveSec;
                p.IdleSec.Value = record.IdleSec;
                p.Source.Value = record.Source.ToString();
                p.LocalDate.Value = record.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                p.CreatedUtc.Value = now;

                inserted += cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return inserted;
        }
    }

    public IReadOnlyList<IntervalRecord> GetPendingUploads(int limit)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, computer_no, win_user, app, file, file_path, window_title,
                       start_utc, end_utc, active_sec, idle_sec, source, local_date
                FROM intervals
                WHERE uploaded = 0
                ORDER BY created_utc, id
                LIMIT $limit;
                """;
            cmd.Parameters.AddWithValue("$limit", limit);

            var results = new List<IntervalRecord>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(Read(reader));
            }

            return results;
        }
    }

    private static IntervalRecord Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        ComputerNo = reader.GetString(1),
        WinUser = reader.GetString(2),
        App = reader.GetString(3),
        File = reader.GetString(4),
        FilePath = reader.IsDBNull(5) ? null : reader.GetString(5),
        WindowTitle = reader.IsDBNull(6) ? null : reader.GetString(6),
        StartUtc = ParseTimestamp(reader.GetString(7)),
        EndUtc = ParseTimestamp(reader.GetString(8)),
        ActiveSec = reader.GetInt32(9),
        IdleSec = reader.GetInt32(10),
        Source = Enum.TryParse<FileSource>(reader.GetString(11), out var source) ? source : FileSource.Unknown,
        LocalDate = DateOnly.ParseExact(reader.GetString(12), "yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(
        value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal);

    public void MarkUploaded(IEnumerable<string> ids)
    {
        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE intervals SET uploaded = 1, uploaded_utc = $now WHERE id = $id;";

            var idParam = cmd.CreateParameter();
            idParam.ParameterName = "$id";
            cmd.Parameters.Add(idParam);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString(RoundTrip, CultureInfo.InvariantCulture));

            foreach (var id in ids)
            {
                idParam.Value = id;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public int PendingCount()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM intervals WHERE uploaded = 0;";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public int PurgeUploaded(DateTimeOffset olderThanUtc)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM intervals WHERE uploaded = 1 AND uploaded_utc IS NOT NULL AND uploaded_utc < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", olderThanUtc.ToUniversalTime().ToString(RoundTrip, CultureInfo.InvariantCulture));
            var deleted = cmd.ExecuteNonQuery();

            if (deleted > 0)
            {
                Execute("PRAGMA incremental_vacuum;");
            }

            return deleted;
        }
    }

    public string? GetState(string key)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM agent_state WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetState(string key, string value)
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO agent_state (key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }
    }

    private void Execute(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }
}
