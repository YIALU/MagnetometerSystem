using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MagnetometerSystem.Core.Feedback;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.Feedback.Server;

public sealed class FeedbackStore
{
    private readonly string _connection;
    public FeedbackStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 10 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS feedback (
                id TEXT PRIMARY KEY, payload TEXT NOT NULL, hash TEXT NOT NULL,
                state TEXT NOT NULL DEFAULT 'pending', issue_url TEXT,
                created_utc TEXT NOT NULL, next_utc TEXT NOT NULL, error_code TEXT);
            UPDATE feedback SET state='unknown' WHERE state='syncing';
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open() { var connection = new SqliteConnection(_connection); connection.Open(); return connection; }

    public async Task<FeedbackReceipt> AcceptAsync(FeedbackSubmission request, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(request);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO feedback(id,payload,hash,created_utc,next_utc) VALUES($id,$payload,$hash,$now,$now);";
        command.Parameters.AddWithValue("$id", request.FeedbackId.ToString());
        command.Parameters.AddWithValue("$payload", json); command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
        command.CommandText = "SELECT hash,state,issue_url FROM feedback WHERE id=$id";
        await using var reader = await command.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
        if (reader.GetString(0) != hash) throw new FeedbackConflictException();
        return new(request.FeedbackId, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task<FeedbackReceipt?> ReceiptAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = Open(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state,issue_url FROM feedback WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(id, reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)) : null;
    }

    public async Task<(FeedbackSubmission Request, string State)?> NextAsync(CancellationToken ct)
    {
        await using var connection = Open(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload,state FROM feedback WHERE state IN ('pending','unknown') AND next_utc <= $now ORDER BY created_utc LIMIT 1";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (JsonSerializer.Deserialize<FeedbackSubmission>(reader.GetString(0))!, reader.GetString(1)) : null;
    }

    public async Task UpdateAsync(Guid id, string state, string? url = null, string? error = null, CancellationToken ct = default)
    {
        await using var connection = Open(); await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE feedback SET state=$state,issue_url=COALESCE($url,issue_url),error_code=$error,next_utc=$next WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString()); command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value); command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$next", DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
    }
}

public sealed class FeedbackConflictException : Exception;
