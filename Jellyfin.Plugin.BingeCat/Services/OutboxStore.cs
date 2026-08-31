using System.Text.Json;
using Jellyfin.Plugin.BingeCat.Api;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.BingeCat.Services;

/// <summary>Small durable SQLite outbox; playback callbacks never wait for the network.</summary>
public sealed class OutboxStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    public OutboxStore()
    {
        var dataPath = Plugin.Instance?.DataFolderPath
            ?? throw new InvalidOperationException("BingeCat plugin data directory is unavailable.");
        Directory.CreateDirectory(dataPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataPath, "outbox.sqlite3"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS event_outbox (
                sequence_no INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                user_id TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                attempt_count INTEGER NOT NULL DEFAULT 0,
                next_attempt_at TEXT NULL,
                last_error TEXT NULL,
                created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_event_outbox_due
                ON event_outbox(user_id, next_attempt_at, sequence_no);
            CREATE TABLE IF NOT EXISTS event_dead_letter (
                sequence_no INTEGER NOT NULL,
                event_id TEXT NOT NULL PRIMARY KEY,
                user_id TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                error_code TEXT NOT NULL,
                attempt_count INTEGER NOT NULL,
                failed_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnqueueAsync(Guid userId, HistoryEvent historyEvent, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO event_outbox(event_id, user_id, payload_json, created_at)
            VALUES ($event_id, $user_id, '{}', $created_at);
            SELECT sequence_no FROM event_outbox WHERE event_id = $event_id;
            """;
        insert.Parameters.AddWithValue("$event_id", historyEvent.EventId);
        insert.Parameters.AddWithValue("$user_id", userId.ToString("D"));
        insert.Parameters.AddWithValue("$created_at", DateTime.UtcNow.ToString("O"));
        var sequence = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        historyEvent.SequenceNo = sequence;
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE event_outbox SET payload_json = $payload WHERE event_id = $event_id;";
        update.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(historyEvent, JsonOptions));
        update.Parameters.AddWithValue("$event_id", historyEvent.EventId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxEntry>> ReadBatchAsync(Guid userId, int limit, CancellationToken cancellationToken)
    {
        var entries = new List<OutboxEntry>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence_no, event_id, payload_json, attempt_count
            FROM event_outbox
            WHERE user_id = $user_id
              AND (next_attempt_at IS NULL OR next_attempt_at <= $now)
            ORDER BY sequence_no
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$user_id", userId.ToString("D"));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var payload = JsonSerializer.Deserialize<HistoryEvent>(reader.GetString(2), JsonOptions);
            if (payload is not null)
            {
                entries.Add(new OutboxEntry(reader.GetInt64(0), reader.GetString(1), payload, reader.GetInt32(3)));
            }
        }

        return entries;
    }

    public async Task DeleteAsync(IEnumerable<string> eventIds, CancellationToken cancellationToken)
    {
        var ids = eventIds.Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var eventId in ids)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM event_outbox WHERE event_id = $event_id;";
            command.Parameters.AddWithValue("$event_id", eventId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeadLetterAsync(
        Guid userId,
        IReadOnlyDictionary<string, string> rejected,
        IEnumerable<OutboxEntry> entries,
        CancellationToken cancellationToken)
    {
        var rejectedEntries = entries
            .Where(entry => rejected.ContainsKey(entry.EventId))
            .ToArray();
        if (rejectedEntries.Length == 0)
        {
            return;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in rejectedEntries)
        {
            var error = rejected[entry.EventId];
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR REPLACE INTO event_dead_letter(
                    sequence_no, event_id, user_id, payload_json, error_code, attempt_count, failed_at
                ) VALUES ($sequence_no, $event_id, $user_id, $payload, $error, $attempts, $failed_at);
                """;
            insert.Parameters.AddWithValue("$sequence_no", entry.SequenceNo);
            insert.Parameters.AddWithValue("$event_id", entry.EventId);
            insert.Parameters.AddWithValue("$user_id", userId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entry.Event, JsonOptions));
            insert.Parameters.AddWithValue("$error", error[..Math.Min(error.Length, 160)]);
            insert.Parameters.AddWithValue("$attempts", entry.AttemptCount);
            insert.Parameters.AddWithValue("$failed_at", DateTime.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM event_outbox WHERE event_id = $event_id;";
            delete.Parameters.AddWithValue("$event_id", entry.EventId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeferAsync(IEnumerable<OutboxEntry> entries, string error, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in entries)
        {
            var attempts = entry.AttemptCount + 1;
            var delay = TimeSpan.FromSeconds(Math.Min(900, 5 * Math.Pow(2, Math.Min(attempts, 7))));
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE event_outbox
                SET attempt_count = $attempts, next_attempt_at = $next, last_error = $error
                WHERE event_id = $event_id;
                """;
            command.Parameters.AddWithValue("$attempts", attempts);
            command.Parameters.AddWithValue("$next", DateTime.UtcNow.Add(delay).ToString("O"));
            command.Parameters.AddWithValue("$error", error[..Math.Min(error.Length, 500)]);
            command.Parameters.AddWithValue("$event_id", entry.EventId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}

public sealed record OutboxEntry(long SequenceNo, string EventId, HistoryEvent Event, int AttemptCount);
