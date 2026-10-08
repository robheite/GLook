using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GLook.Models;
using Microsoft.Data.Sqlite;

namespace GLook.Services;

public sealed class LocalMailStore
{
    private static readonly byte[] CacheEntropy = Encoding.UTF8.GetBytes("GLook.MailCache.v2");
    private readonly string connectionString;
    private string? accountScope;

    public LocalMailStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    }

    public bool HasAccountScope => accountScope is not null;

    public void SetAccountScope(string? accountEmail)
    {
        accountScope = string.IsNullOrWhiteSpace(accountEmail)
            ? null
            : accountEmail.Trim().ToLowerInvariant();
    }

    public async Task InitializeAsync()
    {
        await using var connection = await OpenConnectionAsync();
        var legacyTablesFound = await HasLegacyTablesAsync(connection);

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS secure_mail_threads (
                account_hash TEXT NOT NULL,
                thread_hash TEXT NOT NULL,
                payload BLOB NOT NULL,
                PRIMARY KEY (account_hash, thread_hash)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS secure_mail_sync_state (
                account_hash TEXT NOT NULL PRIMARY KEY,
                payload BLOB NOT NULL
            ) WITHOUT ROWID;
            DROP TABLE IF EXISTS account_mail_threads;
            DROP TABLE IF EXISTS mail_threads;
            DROP TABLE IF EXISTS sync_state;
            """;
        await command.ExecuteNonQueryAsync();

        // Truncate the WAL on every start so an interrupted migration cannot leave
        // recoverable legacy pages behind. VACUUM is only needed when plaintext
        // tables were present in this run.
        await ExecuteNonQueryAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        if (legacyTablesFound)
        {
            await ExecuteNonQueryAsync(connection, "VACUUM;");
            await ExecuteNonQueryAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
    }

    public async Task SaveThreadsAsync(IEnumerable<MailThreadSummary> threads)
    {
        var accountHash = CurrentAccountHash();
        var snapshot = MaterializeThreads(threads);
        await using var connection = await OpenConnectionAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        foreach (var thread in snapshot)
        {
            await UpsertThreadAsync(connection, transaction, accountHash, thread);
        }

        await transaction.CommitAsync();
    }

    public async Task<ulong?> GetHistoryCursorAsync()
    {
        if (accountScope is null)
        {
            return null;
        }

        var accountHash = HashAccountId(accountScope);
        await using var connection = await OpenConnectionAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM secure_mail_sync_state WHERE account_hash = $accountHash;";
        command.Parameters.AddWithValue("$accountHash", accountHash);
        var payload = await command.ExecuteScalarAsync() as byte[];
        return payload is null ? null : UnprotectSyncState(payload)?.HistoryId;
    }

    /// <summary>
    /// Applies a Gmail history result and advances its cursor in one SQLite
    /// transaction. A failed apply therefore replays the same history range on
    /// the next run instead of skipping mailbox changes.
    /// </summary>
    public async Task ApplyHistorySyncAsync(
        GmailHistorySyncResult result,
        bool replaceAccountSnapshot = false)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.RequiresBootstrap)
        {
            throw new ArgumentException("A rebuild marker cannot be persisted as a completed history sync.", nameof(result));
        }

        var accountHash = CurrentAccountHash();
        var upserts = MaterializeThreads(result.UpsertedThreads);
        var removals = result.RemovedThreadIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await using var connection = await OpenConnectionAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        if (replaceAccountSnapshot)
        {
            var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM secure_mail_threads WHERE account_hash = $accountHash;";
            clear.Parameters.AddWithValue("$accountHash", accountHash);
            await clear.ExecuteNonQueryAsync();
        }

        foreach (var threadId in removals)
        {
            await DeleteThreadHashAsync(
                connection,
                transaction,
                accountHash,
                HashThreadId(accountHash, threadId));
        }

        foreach (var thread in upserts)
        {
            await UpsertThreadAsync(connection, transaction, accountHash, thread);
        }

        var syncState = new CachedSyncState(result.LatestHistoryId, DateTimeOffset.UtcNow);
        var stateCommand = connection.CreateCommand();
        stateCommand.Transaction = transaction;
        stateCommand.CommandText = """
            INSERT INTO secure_mail_sync_state (account_hash, payload)
            VALUES ($accountHash, $payload)
            ON CONFLICT(account_hash) DO UPDATE SET payload = excluded.payload;
            """;
        stateCommand.Parameters.AddWithValue("$accountHash", accountHash);
        stateCommand.Parameters.Add("$payload", SqliteType.Blob).Value = ProtectSyncState(syncState);
        await stateCommand.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }

    public async Task SaveFolderSnapshotAsync(
        string labelId,
        IEnumerable<MailThreadSummary> threads,
        bool reconcileMissing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(labelId);
        var accountHash = CurrentAccountHash();
        var snapshot = MaterializeThreads(threads);
        var snapshotHashes = snapshot
            .Select(thread => HashThreadId(accountHash, thread.Id))
            .ToHashSet(StringComparer.Ordinal);

        await using var connection = await OpenConnectionAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        var existing = reconcileMissing
            ? await ReadAccountRowsAsync(connection, transaction, accountHash)
            : [];

        foreach (var thread in snapshot)
        {
            await UpsertThreadAsync(connection, transaction, accountHash, thread);
        }

        if (reconcileMissing)
        {
            foreach (var row in existing)
            {
                if (snapshotHashes.Contains(row.ThreadHash)
                    || !row.Thread.LabelIds.Contains(labelId, StringComparer.Ordinal))
                {
                    continue;
                }

                var remainingLabels = row.Thread.LabelIds
                    .Where(existingLabel => !string.Equals(existingLabel, labelId, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (remainingLabels.Length == 0)
                {
                    await DeleteThreadHashAsync(connection, transaction, accountHash, row.ThreadHash);
                }
                else
                {
                    await UpsertThreadAsync(
                        connection,
                        transaction,
                        accountHash,
                        row.Thread with { LabelIds = remainingLabels });
                }
            }
        }

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<MailThreadSummary>> LoadThreadsAsync(string? labelId, int limit = 50)
    {
        if (accountScope is null || limit <= 0)
        {
            return [];
        }

        var accountHash = HashAccountId(accountScope);
        await using var connection = await OpenConnectionAsync();
        var rows = await ReadAccountRowsAsync(connection, transaction: null, accountHash);
        return rows
            .Select(row => row.Thread)
            .Where(thread => string.IsNullOrWhiteSpace(labelId)
                || thread.LabelIds.Contains(labelId, StringComparer.Ordinal))
            .OrderByDescending(thread => thread.ReceivedAt)
            .Take(limit)
            .ToList();
    }

    public async Task<MailThreadSummary?> LoadThreadAsync(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        if (accountScope is null)
        {
            return null;
        }

        var accountHash = HashAccountId(accountScope);
        await using var connection = await OpenConnectionAsync();
        var rows = await ReadAccountRowsAsync(connection, transaction: null, accountHash);
        return rows.Select(row => row.Thread).FirstOrDefault(thread => thread.Id == threadId);
    }

    public async Task RemoveThreadAsync(string threadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        var accountHash = CurrentAccountHash();
        await using var connection = await OpenConnectionAsync();
        await DeleteThreadHashAsync(
            connection,
            transaction: null,
            accountHash,
            HashThreadId(accountHash, threadId));
    }

    public async Task ClearCurrentAccountAsync()
    {
        if (accountScope is null)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM secure_mail_threads WHERE account_hash = $accountHash;
            DELETE FROM secure_mail_sync_state WHERE account_hash = $accountHash;
            """;
        command.Parameters.AddWithValue("$accountHash", HashAccountId(accountScope));
        await command.ExecuteNonQueryAsync();
        await ExecuteNonQueryAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    private static IReadOnlyList<MailThreadSummary> MaterializeThreads(IEnumerable<MailThreadSummary> threads)
    {
        ArgumentNullException.ThrowIfNull(threads);
        var unique = new Dictionary<string, MailThreadSummary>(StringComparer.Ordinal);
        foreach (var thread in threads)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(thread.Id);
            unique[thread.Id] = thread;
        }

        return unique.Values.ToList();
    }

    private async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            await ExecuteNonQueryAsync(connection, "PRAGMA secure_delete = ON;");
            await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout = 5000;");
            await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode = WAL;");
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task<bool> HasLegacyTablesAsync(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM sqlite_master
                WHERE type = 'table'
                  AND name IN ('account_mail_threads', 'mail_threads', 'sync_state')
            );
            """;
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    private static async Task<IReadOnlyList<CachedRow>> ReadAccountRowsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string accountHash)
    {
        var rows = new List<CachedRow>();
        var invalidHashes = new List<string>();
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT thread_hash, payload
            FROM secure_mail_threads
            WHERE account_hash = $accountHash;
            """;
        command.Parameters.AddWithValue("$accountHash", accountHash);

        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var threadHash = reader.GetString(0);
                var thread = UnprotectThread(reader.GetFieldValue<byte[]>(1));
                if (thread is null)
                {
                    invalidHashes.Add(threadHash);
                }
                else
                {
                    rows.Add(new CachedRow(threadHash, thread));
                }
            }
        }

        foreach (var invalidHash in invalidHashes)
        {
            await DeleteThreadHashAsync(connection, transaction, accountHash, invalidHash);
        }

        return rows;
    }

    private static async Task UpsertThreadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string accountHash,
        MailThreadSummary thread)
    {
        var protectedPayload = ProtectThread(thread);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO secure_mail_threads (account_hash, thread_hash, payload)
            VALUES ($accountHash, $threadHash, $payload)
            ON CONFLICT(account_hash, thread_hash) DO UPDATE SET
                payload = excluded.payload;
            """;
        command.Parameters.AddWithValue("$accountHash", accountHash);
        command.Parameters.AddWithValue("$threadHash", HashThreadId(accountHash, thread.Id));
        command.Parameters.Add("$payload", SqliteType.Blob).Value = protectedPayload;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DeleteThreadHashAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string accountHash,
        string threadHash)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM secure_mail_threads
            WHERE account_hash = $accountHash AND thread_hash = $threadHash;
            """;
        command.Parameters.AddWithValue("$accountHash", accountHash);
        command.Parameters.AddWithValue("$threadHash", threadHash);
        await command.ExecuteNonQueryAsync();
    }

    private static byte[] ProtectThread(MailThreadSummary thread)
    {
        var payload = new CachedMailThread(
            thread.Id,
            thread.Sender,
            thread.Subject,
            thread.Snippet,
            thread.ReceivedAt,
            thread.IsUnread,
            thread.IsStarred,
            thread.LabelIds.ToArray());
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload);
        try
        {
            return ProtectedData.Protect(plaintext, CacheEntropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static MailThreadSummary? UnprotectThread(byte[] protectedPayload)
    {
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(
                protectedPayload,
                CacheEntropy,
                DataProtectionScope.CurrentUser);
            var payload = JsonSerializer.Deserialize<CachedMailThread>(plaintext);
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.Id)
                || payload.LabelIds is null)
            {
                return null;
            }

            return new MailThreadSummary(
                payload.Id,
                payload.Sender ?? string.Empty,
                payload.Subject ?? string.Empty,
                payload.Snippet ?? string.Empty,
                payload.ReceivedAt,
                payload.IsUnread,
                payload.IsStarred,
                payload.LabelIds);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private static byte[] ProtectSyncState(CachedSyncState state)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state);
        try
        {
            return ProtectedData.Protect(plaintext, CacheEntropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static CachedSyncState? UnprotectSyncState(byte[] protectedPayload)
    {
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(
                protectedPayload,
                CacheEntropy,
                DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<CachedSyncState>(plaintext);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    private string CurrentAccountHash() => HashAccountId(RequireAccountScope());

    private static string HashAccountId(string accountId) =>
        HashIdentifier($"account\n{accountId}");

    private static string HashThreadId(string accountHash, string threadId) =>
        HashIdentifier($"thread\n{accountHash}\n{threadId}");

    private static string HashIdentifier(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static Task<int> ExecuteNonQueryAsync(SqliteConnection connection, string commandText)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        return command.ExecuteNonQueryAsync();
    }

    private string RequireAccountScope() =>
        accountScope ?? throw new InvalidOperationException("Connect a Gmail account before using the local mail cache.");

    private sealed record CachedRow(string ThreadHash, MailThreadSummary Thread);

    private sealed record CachedMailThread(
        string Id,
        string Sender,
        string Subject,
        string Snippet,
        DateTimeOffset ReceivedAt,
        bool IsUnread,
        bool IsStarred,
        string[] LabelIds);

    private sealed record CachedSyncState(ulong HistoryId, DateTimeOffset UpdatedAt);
}
