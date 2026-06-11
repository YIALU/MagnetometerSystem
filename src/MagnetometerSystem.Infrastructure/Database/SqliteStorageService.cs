using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;

namespace MagnetometerSystem.Infrastructure.Database;

/// <summary>
/// 基于 SQLite + Dapper 的数据存储服务实现。
/// readings / corrected_readings 的通道数据以 JSON 形式存入 data 列：
///   { "values": {"X":1.0,...}, "original": {...}, "isCalibrated": 0|1, "isOrthoCorrected": 0|1 }
/// 通道名来自会话的 channel_names，读取时按名字顺序还原为 double[]。
/// </summary>
public class SqliteStorageService : IDataStorageService, IDisposable
{
    private readonly DatabaseInitializer _dbInit;
    private readonly DataBus _dataBus;
    private readonly Channel<MagnetometerReading> _writeChannel;
    private readonly Task _consumerTask;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    // 消费循环是否处于空闲（队列空、阻塞等待中）。配合队列计数判断"已全部落库"。
    private volatile bool _writerIdle;

    // 缓存 session_id -> 通道名（一个会话不会变）
    private readonly Dictionary<string, string[]> _channelNamesCache = new();
    private readonly object _channelNamesLock = new();

    private const int BatchSize = 500;

    public SqliteStorageService(DatabaseInitializer dbInit, DataBus dataBus)
    {
        _dbInit = dbInit;
        _dataBus = dataBus;

        _writeChannel = Channel.CreateUnbounded<MagnetometerReading>(
            new UnboundedChannelOptions { SingleReader = true });

        _consumerTask = Task.Run(ConsumeWriteQueueAsync);
    }

    /// <inheritdoc />
    public async Task<string> StartSessionAsync(string name, SensorConfig sensorConfig, ConnectionConfig connectionConfig)
    {
        var sessionId = Guid.NewGuid().ToString();
        var channelNames = sensorConfig.ChannelNames;

        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        const string sql = """
            INSERT INTO sessions (id, name, started_at, sensor_type, sample_rate,
                channel_count, channel_names, device_info, connection_type)
            VALUES (@Id, @Name, @StartedAt, @SensorType, @SampleRate,
                @ChannelCount, @ChannelNames, @DeviceInfo, @ConnectionType)
            """;

        await conn.ExecuteAsync(sql, new
        {
            Id = sessionId,
            Name = name,
            StartedAt = DateTime.UtcNow.ToString("O"),
            SensorType = sensorConfig.Type.ToString(),
            SampleRate = sensorConfig.SampleRate,
            ChannelCount = sensorConfig.ChannelCount,
            ChannelNames = JsonSerializer.Serialize(channelNames),
            DeviceInfo = sensorConfig.SerialNumber,
            ConnectionType = connectionConfig.Type.ToString(),
        });

        lock (_channelNamesLock)
        {
            _channelNamesCache[sessionId] = channelNames;
        }

        _dataBus.PublishSessionStarted(sessionId);
        return sessionId;
    }

    /// <inheritdoc />
    public async Task EndSessionAsync(string sessionId)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        var count = await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM readings WHERE session_id = @SessionId",
            new { SessionId = sessionId });

        const string sql = """
            UPDATE sessions
            SET ended_at = @EndedAt, total_readings = @TotalReadings
            WHERE id = @Id
            """;

        await conn.ExecuteAsync(sql, new
        {
            Id = sessionId,
            EndedAt = DateTime.UtcNow.ToString("O"),
            TotalReadings = count,
        });

        _dataBus.PublishSessionEnded(sessionId);
    }

    /// <inheritdoc />
    public Task SaveReadingsAsync(IEnumerable<MagnetometerReading> readings)
    {
        foreach (var reading in readings)
        {
            _writeChannel.Writer.TryWrite(reading);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task WaitForPendingWritesAsync(int timeoutMs = 5000)
    {
        // 当队列计数为 0 且消费循环处于空闲（阻塞等待）时，说明此前入队的读数已全部落库。
        // 调用方应先把缓冲 flush（入队）再调用本方法，再统计会话总数。
        int waited = 0;
        const int step = 15;
        while (waited < timeoutMs)
        {
            if (_writeChannel.Reader.Count == 0 && _writerIdle)
                return;
            await Task.Delay(step);
            waited += step;
        }
        System.Diagnostics.Trace.TraceWarning(
            $"WaitForPendingWritesAsync 超时（{timeoutMs}ms），会话总数计数可能偏少");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionInfo>> GetSessionsAsync()
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        const string sql = """
            SELECT id, name, started_at, ended_at, sensor_type, sample_rate,
                   channel_count, channel_names, device_info, connection_type,
                   notes, total_readings
            FROM sessions
            ORDER BY started_at DESC
            LIMIT 100
            """;

        var rows = await conn.QueryAsync(sql);
        var sessions = new List<SessionInfo>();

        foreach (var row in rows)
        {
            sessions.Add(MapRowToSession(row));
        }

        return sessions;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MagnetometerReading>> GetReadingsAsync(
        string sessionId, DateTime? startTime = null, DateTime? endTime = null)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        var channelNames = await GetChannelNamesAsync(conn, sessionId);

        var sql = "SELECT id, session_id, timestamp, data FROM readings WHERE session_id = @SessionId";

        var parameters = new DynamicParameters();
        parameters.Add("SessionId", sessionId);

        if (startTime.HasValue)
        {
            sql += " AND timestamp >= @StartTime";
            parameters.Add("StartTime", startTime.Value.ToString("O"));
        }

        if (endTime.HasValue)
        {
            sql += " AND timestamp <= @EndTime";
            parameters.Add("EndTime", endTime.Value.ToString("O"));
        }

        sql += " ORDER BY timestamp ASC";

        var rows = await conn.QueryAsync(sql, parameters);
        var readings = new List<MagnetometerReading>();

        foreach (var row in rows)
        {
            readings.Add(MapRowToReading(row, channelNames));
        }

        return readings;
    }

    /// <inheritdoc />
    public async Task DeleteSessionAsync(string sessionId)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("PRAGMA foreign_keys=ON;");

        await conn.ExecuteAsync(
            "DELETE FROM sessions WHERE id = @Id",
            new { Id = sessionId });

        lock (_channelNamesLock)
        {
            _channelNamesCache.Remove(sessionId);
        }
    }

    /// <inheritdoc />
    public async Task UpdateSessionAsync(string sessionId, string name, string? notes)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        const string sql = """
            UPDATE sessions SET name = @Name, notes = @Notes WHERE id = @Id
            """;

        await conn.ExecuteAsync(sql, new { Id = sessionId, Name = name, Notes = notes });
    }

    #region 校正数据存储

    /// <inheritdoc />
    public async Task SaveCorrectedReadingsAsync(IEnumerable<CorrectedReading> readings)
    {
        var readingsList = readings.ToList();
        if (readingsList.Count == 0) return;

        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        var sessionId = readingsList[0].SessionId;
        var channelNames = await GetChannelNamesAsync(conn, sessionId);

        using var tx = conn.BeginTransaction();

        const string sql = """
            INSERT INTO corrected_readings
                (original_reading_id, session_id, timestamp, correction_profile_id,
                 data, corrected_at)
            VALUES
                (@OriginalReadingId, @SessionId, @Timestamp, @CorrectionProfileId,
                 @Data, @CorrectedAt)
            """;

        foreach (var reading in readingsList)
        {
            var param = MapCorrectedReadingToParam(reading, channelNames);
            await conn.ExecuteAsync(sql, param, tx);
        }

        tx.Commit();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CorrectedReading>> GetCorrectedReadingsAsync(
        string sessionId, string? correctionProfileId = null)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        var channelNames = await GetChannelNamesAsync(conn, sessionId);

        var sql = """
            SELECT id, original_reading_id, session_id, timestamp, correction_profile_id,
                   data, corrected_at
            FROM corrected_readings WHERE session_id = @SessionId
            """;
        if (correctionProfileId != null)
            sql += " AND correction_profile_id = @ProfileId";
        sql += " ORDER BY timestamp ASC";

        var rows = await conn.QueryAsync(sql, new { SessionId = sessionId, ProfileId = correctionProfileId });
        var result = new List<CorrectedReading>();
        foreach (var row in rows)
        {
            result.Add(MapRowToCorrectedReading(row, channelNames));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task DeleteCorrectedReadingsAsync(string sessionId, string? correctionProfileId = null)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        var sql = "DELETE FROM corrected_readings WHERE session_id = @SessionId";
        if (correctionProfileId != null)
            sql += " AND correction_profile_id = @ProfileId";

        await conn.ExecuteAsync(sql, new { SessionId = sessionId, ProfileId = correctionProfileId });
    }

    /// <inheritdoc />
    public async Task<bool> HasCorrectedReadingsAsync(string sessionId)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM corrected_readings WHERE session_id = @SessionId LIMIT 1",
            new { SessionId = sessionId });
        return count > 0;
    }

    #endregion

    #region 后台写入队列

    private async Task ConsumeWriteQueueAsync()
    {
        var batch = new List<MagnetometerReading>(BatchSize);
        var reader = _writeChannel.Reader;

        try
        {
            while (true)
            {
                // 队列空、即将阻塞等待 → 标记空闲。WaitForPendingWritesAsync 据此判断已排空。
                _writerIdle = true;
                bool hasMore;
                try { hasMore = await reader.WaitToReadAsync(_cts.Token); }
                finally { _writerIdle = false; }
                if (!hasMore) break;

                batch.Clear();
                while (batch.Count < BatchSize && reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                if (batch.Count > 0)
                {
                    await WriteBatchAsync(batch);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        batch.Clear();
        while (reader.TryRead(out var remaining))
        {
            batch.Add(remaining);
            if (batch.Count >= BatchSize)
            {
                await WriteBatchAsync(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await WriteBatchAsync(batch);
        }
    }

    // SQLite 瞬时错误码：忙 / 被锁。多进程或长事务下可能短暂出现，退避重试通常即可成功。
    private const int SqliteBusy = 5;    // SQLITE_BUSY
    private const int SqliteLocked = 6;  // SQLITE_LOCKED

    private async Task WriteBatchAsync(List<MagnetometerReading> batch)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await WriteBatchOnceAsync(batch);
                return; // 成功落库
            }
            catch (SqliteException ex)
                when ((ex.SqliteErrorCode == SqliteBusy || ex.SqliteErrorCode == SqliteLocked)
                      && attempt < maxAttempts)
            {
                int delayMs = attempt switch { 1 => 50, 2 => 150, _ => 450 };
                System.Diagnostics.Trace.TraceWarning(
                    $"SqliteStorageService.WriteBatchAsync 第 {attempt}/{maxAttempts} 次 busy/locked，{delayMs}ms 后重试: {ex.Message}");
                await Task.Delay(delayMs);
            }
            catch (Exception ex)
            {
                // 非瞬时错误，或重试耗尽：记录并放弃该批（已确认不做落盘）
                System.Diagnostics.Trace.TraceError(
                    $"SqliteStorageService.WriteBatchAsync failed ({batch.Count} readings, attempt {attempt}): {ex.Message}");
                return;
            }
        }
    }

    private async Task WriteBatchOnceAsync(List<MagnetometerReading> batch)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        var sessionToNames = new Dictionary<string, string[]>();
        foreach (var r in batch)
        {
            if (!sessionToNames.ContainsKey(r.SessionId))
            {
                sessionToNames[r.SessionId] = await GetChannelNamesAsync(conn, r.SessionId);
            }
        }

        using var tx = conn.BeginTransaction();

        const string sql = """
            INSERT INTO readings (session_id, timestamp, data)
            VALUES (@SessionId, @Timestamp, @Data)
            """;

        foreach (var r in batch)
        {
            var param = MapReadingToParam(r, sessionToNames[r.SessionId]);
            await conn.ExecuteAsync(sql, param, tx);
        }

        tx.Commit();
    }

    #endregion

    #region 映射方法

    private async Task<string[]> GetChannelNamesAsync(SqliteConnection conn, string sessionId)
    {
        lock (_channelNamesLock)
        {
            if (_channelNamesCache.TryGetValue(sessionId, out var cached))
                return cached;
        }

        var json = await conn.ExecuteScalarAsync<string?>(
            "SELECT channel_names FROM sessions WHERE id = @Id",
            new { Id = sessionId });

        string[] names;
        if (!string.IsNullOrEmpty(json))
        {
            names = JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        else
        {
            names = [];
        }

        lock (_channelNamesLock)
        {
            _channelNamesCache[sessionId] = names;
        }
        return names;
    }

    private static string[] EffectiveNames(string[] names, int count)
    {
        if (names.Length >= count) return names;
        var result = new string[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = i < names.Length ? names[i] : $"CH{i}";
        }
        return result;
    }

    private static string BuildDataJson(double[] values, double[]? original, string[] channelNames, bool isCalibrated, bool isOrthoCorrected)
    {
        var names = EffectiveNames(channelNames, values.Length);

        var valuesDict = new Dictionary<string, double>(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            valuesDict[names[i]] = values[i];
        }

        Dictionary<string, double>? originalDict = null;
        if (original != null && original.Length > 0)
        {
            var origNames = EffectiveNames(channelNames, original.Length);
            originalDict = new Dictionary<string, double>(original.Length);
            for (int i = 0; i < original.Length; i++)
            {
                originalDict[origNames[i]] = original[i];
            }
        }

        var payload = new Dictionary<string, object?>
        {
            ["values"] = valuesDict,
            ["original"] = originalDict,
            ["isCalibrated"] = isCalibrated ? 1 : 0,
            ["isOrthoCorrected"] = isOrthoCorrected ? 1 : 0,
        };

        return JsonSerializer.Serialize(payload);
    }

    private sealed class ReadingDataDto
    {
        public Dictionary<string, double>? values { get; set; }
        public Dictionary<string, double>? original { get; set; }
        public int isCalibrated { get; set; }
        public int isOrthoCorrected { get; set; }
    }

    private static (double[] values, double[]? original, bool isCalibrated, bool isOrthoCorrected) ParseDataJson(string json, string[] channelNames)
    {
        var dto = JsonSerializer.Deserialize<ReadingDataDto>(json) ?? new ReadingDataDto();
        var values = ExtractOrdered(dto.values, channelNames);
        var original = dto.original != null && dto.original.Count > 0
            ? ExtractOrdered(dto.original, channelNames)
            : null;
        return (values, original, dto.isCalibrated == 1, dto.isOrthoCorrected == 1);
    }

    private static double[] ExtractOrdered(Dictionary<string, double>? dict, string[] channelNames)
    {
        if (dict == null || dict.Count == 0) return [];

        if (channelNames.Length > 0)
        {
            var result = new List<double>(channelNames.Length);
            foreach (var name in channelNames)
            {
                if (dict.TryGetValue(name, out var v))
                    result.Add(v);
            }
            // 若协议定义的通道名在 JSON 中完全没命中，退回到原字典顺序
            if (result.Count > 0) return result.ToArray();
        }

        // Fallback: 按 CH0/CH1/... 或字典本身顺序
        return dict.Values.ToArray();
    }

    private static object MapReadingToParam(MagnetometerReading r, string[] channelNames)
    {
        return new
        {
            r.SessionId,
            Timestamp = r.Timestamp.ToString("O"),
            Data = BuildDataJson(r.ChannelValues, r.OriginalChannelValues, channelNames, r.IsCalibrated, r.IsOrthogonalityCorrected),
        };
    }

    private static MagnetometerReading MapRowToReading(dynamic row, string[] channelNames)
    {
        var dataJson = (string)row.data;
        var (values, original, isCal, isOrtho) = ParseDataJson(dataJson, channelNames);

        return new MagnetometerReading
        {
            Id = (long)row.id,
            SessionId = (string)row.session_id,
            Timestamp = ParseUtcAsLocal((string)row.timestamp),
            ChannelValues = values,
            OriginalChannelValues = original,
            IsCalibrated = isCal,
            IsOrthogonalityCorrected = isOrtho,
        };
    }

    private static SessionInfo MapRowToSession(dynamic row)
    {
        string[] channelNames = [];
        if (row.channel_names is string namesJson && !string.IsNullOrEmpty(namesJson))
        {
            channelNames = JsonSerializer.Deserialize<string[]>(namesJson) ?? [];
        }

        _ = Enum.TryParse<SensorType>((string)row.sensor_type, out var sensorType);
        _ = Enum.TryParse<ConnectionType>((string?)row.connection_type ?? "", out var connectionType);

        return new SessionInfo
        {
            Id = (string)row.id,
            Name = (string)row.name,
            StartedAt = ParseUtcAsLocal((string)row.started_at),
            EndedAt = row.ended_at is string endedAt && !string.IsNullOrEmpty(endedAt)
                ? ParseUtcAsLocal(endedAt)
                : null,
            SensorType = sensorType,
            SampleRate = (double)row.sample_rate,
            ChannelCount = (int)(long)row.channel_count,
            ChannelNames = channelNames,
            DeviceInfo = row.device_info as string,
            ConnectionType = connectionType,
            Notes = row.notes as string,
            TotalReadings = (long)row.total_readings,
        };
    }

    private static object MapCorrectedReadingToParam(CorrectedReading reading, string[] channelNames)
    {
        return new
        {
            reading.OriginalReadingId,
            reading.SessionId,
            Timestamp = reading.Timestamp.ToString("O"),
            reading.CorrectionProfileId,
            Data = BuildDataJson(reading.CorrectedValues, null, channelNames, false, reading.IsOrthogonalityCorrected),
            CorrectedAt = reading.CorrectedAt.ToString("O"),
        };
    }

    private static CorrectedReading MapRowToCorrectedReading(dynamic row, string[] channelNames)
    {
        var dataJson = (string)row.data;
        var (values, _, _, isOrtho) = ParseDataJson(dataJson, channelNames);

        double? totalField = null;
        if (values.Length >= 3)
        {
            totalField = Math.Sqrt(values[0] * values[0] + values[1] * values[1] + values[2] * values[2]);
        }

        return new CorrectedReading
        {
            Id = (long)row.id,
            OriginalReadingId = (long)row.original_reading_id,
            SessionId = (string)row.session_id,
            Timestamp = ParseUtcAsLocal((string)row.timestamp),
            CorrectionProfileId = (string)row.correction_profile_id,
            CorrectedValues = values,
            CorrectedTotalField = totalField,
            IsOrthogonalityCorrected = isOrtho,
            CorrectedAt = ParseUtcAsLocal((string)row.corrected_at)
        };
    }

    /// <summary>
    /// 将 DB 里存储的 UTC ISO-8601 时间字符串解析为本地时间（Kind=Local）。
    /// DB 仍统一保持 UTC 存储；仅显示/读取时转本地，避免时区歧义。
    /// </summary>
    private static DateTime ParseUtcAsLocal(string iso)
    {
        var dt = DateTime.Parse(iso, null, DateTimeStyles.RoundtripKind);
        if (dt.Kind == DateTimeKind.Unspecified)
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        return dt.ToLocalTime();
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 标记不再写入。消费循环会把队列内剩余读数全部落库后自然退出。
        _writeChannel.Writer.Complete();

        // 正常退出应等待队列排空，不能用固定短超时直接丢尾部数据。
        // 不在此处 Cancel —— Cancel 会中断排空；仅当排空异常缓慢（30s 未完成）才兜底取消。
        if (!_consumerTask.Wait(TimeSpan.FromSeconds(30)))
        {
            System.Diagnostics.Trace.TraceWarning(
                "SqliteStorageService.Dispose: 写入队列 30s 内未排空，强制取消，可能丢失尾部数据");
            _cts.Cancel();
            try { _consumerTask.Wait(TimeSpan.FromSeconds(5)); } catch { /* 退出阶段忽略 */ }
        }

        _cts.Dispose();
    }

    #endregion
}
