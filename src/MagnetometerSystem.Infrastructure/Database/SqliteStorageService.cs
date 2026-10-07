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
/// 新读数以有序数组写入 JSON，保留重名通道和辅助通道；兼容读取旧版按名称存储的字典。
/// 入队与提交分别计数，失败批次保留在内存并公开错误，等待明确重试。
/// </summary>
public class SqliteStorageService : IDataStorageService, IDisposable
{
    private readonly DatabaseInitializer _dbInit;
    private readonly DataBus _dataBus;
    private readonly Channel<PendingBatch> _writeChannel;
    private readonly Task _consumerTask;
    private bool _disposed;
    private readonly object _writeLock = new();
    private readonly List<PendingBatch> _failedBatches = [];
    private long _pendingReadings;
    private long _savedReadings;
    private int _queuedBatches;
    private Exception? _writeError;
    private sealed record PendingBatch(MagnetometerReading[] Readings)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public event Action<StorageWriteStatus>? WriteStatusChanged;
    public StorageWriteStatus WriteStatus
    {
        get { lock (_writeLock) return new(_savedReadings, _pendingReadings, _writeError?.Message); }
    }

    // 仅用于等待失败队列转入保留列表；落库确认依据待保存读数计数。
    private volatile bool _writerIdle;

    // 缓存 session_id -> 通道名（一个会话不会变）
    private readonly Dictionary<string, string[]> _channelNamesCache = new();
    private readonly object _channelNamesLock = new();

    private const int BatchSize = 500;

    public SqliteStorageService(DatabaseInitializer dbInit, DataBus dataBus)
    {
        _dbInit = dbInit;
        _dataBus = dataBus;

        _writeChannel = Channel.CreateUnbounded<PendingBatch>(
            new UnboundedChannelOptions { SingleReader = true });

        _consumerTask = Task.Run(ConsumeWriteQueueAsync);
    }

    /// <inheritdoc />
    public async Task<string> StartSessionAsync(string name, SensorConfig sensorConfig, ConnectionConfig connectionConfig)
    {
        var sessionId = Guid.NewGuid().ToString();
        var channelNames = sensorConfig.ChannelNames.ToArray();
        var channelUnits = sensorConfig.ChannelUnits.ToArray();
        if (!double.IsFinite(sensorConfig.SampleRate) || sensorConfig.SampleRate <= 0)
            throw new ArgumentException("采样率必须是正有限数值。", nameof(sensorConfig));
        if (channelNames.Length == 0 || channelNames.Length != sensorConfig.ChannelCount)
            throw new ArgumentException("会话通道名称数量必须与通道数一致。", nameof(sensorConfig));
        if (channelUnits.Length != channelNames.Length)
            throw new ArgumentException("会话通道单位数量必须与通道数一致。", nameof(sensorConfig));

        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        const string sql = """
            INSERT INTO sessions (id, name, started_at, sensor_type, sample_rate,
                channel_count, channel_names, channel_units, device_info, connection_type)
            VALUES (@Id, @Name, @StartedAt, @SensorType, @SampleRate,
                @ChannelCount, @ChannelNames, @ChannelUnits, @DeviceInfo, @ConnectionType)
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
            ChannelUnits = JsonSerializer.Serialize(channelUnits),
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
        await WaitForPendingWritesAsync();
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
        ArgumentNullException.ThrowIfNull(readings);
        var snapshot = readings.Select(r => r.DeepClone()).ToArray();
        if (snapshot.Length == 0) return Task.CompletedTask;
        var tasks = new List<Task>();
        lock (_writeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var chunk in snapshot.Chunk(BatchSize))
            {
                var batch = new PendingBatch(chunk);
                _pendingReadings += chunk.Length;
                if (_writeError != null)
                {
                    _failedBatches.Add(batch);
                    batch.Completion.TrySetException(_writeError);
                }
                else if (!_writeChannel.Writer.TryWrite(batch))
                {
                    throw new InvalidOperationException("存储写入队列已关闭。");
                }
                else Interlocked.Increment(ref _queuedBatches);
                tasks.Add(batch.Completion.Task);
            }
        }
        PublishWriteStatus();
        return Task.WhenAll(tasks);
    }

    /// <inheritdoc />
    public async Task WaitForPendingWritesAsync(int timeoutMs = 5000)
    {
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            lock (_writeLock)
            {
                if (_writeError != null)
                    throw new IOException("读数尚未全部保存，请修复存储问题后重试。", _writeError);
                if (_pendingReadings == 0) return;
            }
            if (elapsed.ElapsedMilliseconds >= timeoutMs)
                throw new TimeoutException($"等待落库超过 {timeoutMs} ms；仍有 {WriteStatus.PendingReadings} 条待保存。");
            await Task.Delay(15).ConfigureAwait(false);
        }
    }

    public async Task RetryPendingWritesAsync()
    {
        // 失败后新批次直接留在内存，等待消费者把先前队列转入保留列表。
        while (Volatile.Read(ref _queuedBatches) > 0 || !_writerIdle)
            await Task.Delay(15).ConfigureAwait(false);
        Task[] tasks;
        lock (_writeLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            tasks = _failedBatches.Select(old => new PendingBatch(old.Readings)).Select(batch =>
            {
                _writeChannel.Writer.TryWrite(batch);
                Interlocked.Increment(ref _queuedBatches);
                return batch.Completion.Task;
            }).ToArray();
            _failedBatches.Clear();
            _writeError = null;
        }
        PublishWriteStatus();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        await WaitForPendingWritesAsync().ConfigureAwait(false);
    }

    private void PublishWriteStatus()
    {
        var status = WriteStatus;
        if (WriteStatusChanged is not { } handlers) return;
        foreach (Action<StorageWriteStatus> handler in handlers.GetInvocationList())
        {
            try { handler(status); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"存储状态订阅者异常: {ex}"); }
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionInfo>> GetSessionsAsync()
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        const string sql = """
            SELECT id, name, started_at, ended_at, sensor_type, sample_rate,
                   channel_count, channel_names, channel_units, device_info, connection_type,
                   notes, total_readings, legacy_data_table
            FROM sessions
            ORDER BY started_at DESC
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

        var sensorTypeName = await conn.ExecuteScalarAsync<string?>(
            "SELECT sensor_type FROM sessions WHERE id = @Id", new { Id = sessionId });
        if (sensorTypeName == null) return [];
        var legacyTable = await conn.ExecuteScalarAsync<string?>(
            "SELECT legacy_data_table FROM sessions WHERE id = @Id", new { Id = sessionId });
        if (legacyTable != null)
            throw new NotSupportedException($"旧版原始数据保留在 {legacyTable}，需迁移后读取。");
        var channelNames = await GetChannelNamesAsync(conn, sessionId);
        Enum.TryParse<SensorType>(sensorTypeName, out var sensorType);

        var sql = "SELECT id, session_id, timestamp, data FROM readings WHERE session_id = @SessionId";

        var parameters = new DynamicParameters();
        parameters.Add("SessionId", sessionId);

        if (startTime.HasValue)
        {
            sql += " AND julianday(timestamp) >= julianday(@StartTime)";
            parameters.Add("StartTime", startTime.Value.ToUniversalTime().ToString("O"));
        }

        if (endTime.HasValue)
        {
            sql += " AND julianday(timestamp) <= julianday(@EndTime)";
            parameters.Add("EndTime", endTime.Value.ToUniversalTime().ToString("O"));
        }

        sql += " ORDER BY julianday(timestamp), id";

        var rows = await conn.QueryAsync(sql, parameters);
        var readings = new List<MagnetometerReading>();

        foreach (var row in rows)
        {
            readings.Add(MapRowToReading(row, channelNames, sensorType));
        }

        return readings;
    }

    /// <inheritdoc />
    public async Task<ReadingPage> GetReadingsPageAsync(
        string sessionId, DateTime startTime, DateTime endTime, ReadingPageCursor? after, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        var sensorTypeName = await conn.ExecuteScalarAsync<string?>(
            "SELECT sensor_type FROM sessions WHERE id = @Id", new { Id = sessionId });
        if (sensorTypeName == null) return new ReadingPage([], [], null);
        var legacyTable = await conn.ExecuteScalarAsync<string?>(
            "SELECT legacy_data_table FROM sessions WHERE id = @Id", new { Id = sessionId });
        if (legacyTable != null)
            throw new NotSupportedException($"旧版原始数据保留在 {legacyTable}，需迁移后读取。");
        var channelNames = await GetChannelNamesAsync(conn, sessionId);
        Enum.TryParse<SensorType>(sensorTypeName, out var sensorType);

        // 键集分页：沿 (session_id, timestamp) 索引及隐含的 id 前进，每页只取 limit 行，不重复、不遗漏。
        var sql = "SELECT id, session_id, timestamp, data FROM readings WHERE session_id = @SessionId"
            + " AND julianday(timestamp) >= julianday(@StartTime) AND julianday(timestamp) <= julianday(@EndTime)";
        var parameters = new DynamicParameters();
        parameters.Add("SessionId", sessionId);
        parameters.Add("StartTime", startTime.ToUniversalTime().ToString("O"));
        parameters.Add("EndTime", endTime.ToUniversalTime().ToString("O"));
        if (after != null)
        {
            sql += " AND (timestamp, id) > (@AfterTimestamp, @AfterId)";
            parameters.Add("AfterTimestamp", after.Timestamp);
            parameters.Add("AfterId", after.Id);
        }
        sql += " ORDER BY timestamp, id LIMIT @Limit";
        parameters.Add("Limit", limit);

        var readings = new List<MagnetometerReading>();
        var utcTimestamps = new List<DateTime>();
        ReadingPageCursor? last = null;
        foreach (var row in await conn.QueryAsync(sql, parameters))
        {
            readings.Add(MapRowToReading(row, channelNames, sensorType));
            utcTimestamps.Add(ParseUtc((string)row.timestamp));
            last = new ReadingPageCursor((string)row.timestamp, (long)row.id);
        }
        return new ReadingPage(readings, utcTimestamps, readings.Count == limit ? last : null);
    }

    /// <inheritdoc />
    public async Task DeleteSessionAsync(string sessionId)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("PRAGMA foreign_keys=ON;");
        var legacyTable = await conn.ExecuteScalarAsync<string?>(
            "SELECT legacy_data_table FROM sessions WHERE id = @Id", new { Id = sessionId });
        if (legacyTable != null)
            throw new NotSupportedException("旧版数据尚未迁移，不能删除保留的原始会话。");

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
        ArgumentNullException.ThrowIfNull(readings);
        var readingsList = readings.Select(r => new CorrectedReading
        {
            Id = r.Id, OriginalReadingId = r.OriginalReadingId, SessionId = r.SessionId,
            Timestamp = r.Timestamp, CorrectionProfileId = r.CorrectionProfileId,
            CorrectedValues = (double[])r.CorrectedValues.Clone(),
            CorrectedTotalField = r.CorrectedTotalField,
            IsOrthogonalityCorrected = r.IsOrthogonalityCorrected, CorrectedAt = r.CorrectedAt
        }).ToList();
        if (readingsList.Count == 0) return;

        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        var sessionNames = new Dictionary<string, string[]>();
        foreach (var sessionId in readingsList.Select(r => r.SessionId).Distinct())
            sessionNames[sessionId] = await GetChannelNamesAsync(conn, sessionId);
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
            var channelNames = sessionNames[reading.SessionId];
            var original = await conn.ExecuteScalarAsync<string?>(
                "SELECT data FROM readings WHERE id = @Id AND session_id = @SessionId",
                new { Id = reading.OriginalReadingId, reading.SessionId }, tx);
            if (original == null)
                throw new ArgumentException("改正结果必须引用同一会话中已保存的原始读数。");
            var originalValues = ParseDataJson(original, channelNames).values;
            if (reading.CorrectedValues.Length != originalValues.Length)
                throw new ArgumentException("改正结果必须保留原始数据的全部通道。");
            var param = MapCorrectedReadingToParam(reading, channelNames);
            await conn.ExecuteAsync(
                "DELETE FROM corrected_readings WHERE original_reading_id=@OriginalReadingId AND session_id=@SessionId AND correction_profile_id=@CorrectionProfileId",
                new { reading.OriginalReadingId, reading.SessionId, reading.CorrectionProfileId }, tx);
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
        var sensorTypeName = await conn.ExecuteScalarAsync<string?>(
            "SELECT sensor_type FROM sessions WHERE id = @Id", new { Id = sessionId });
        Enum.TryParse<SensorType>(sensorTypeName, out var sensorType);

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
            result.Add(MapRowToCorrectedReading(row, channelNames, sensorType));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetCorrectionVersionIdsAsync(string sessionId)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();
        var ids = await conn.QueryAsync<string>(
            "SELECT DISTINCT correction_profile_id FROM corrected_readings WHERE session_id = @SessionId ORDER BY correction_profile_id",
            new { SessionId = sessionId });
        return ids.ToArray();
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
        var reader = _writeChannel.Reader;
        while (true)
        {
            _writerIdle = true;
            if (!await reader.WaitToReadAsync().ConfigureAwait(false)) break;
            _writerIdle = false;
            while (reader.TryRead(out var batch))
            {
                try
                {
                    lock (_writeLock)
                    {
                        if (_writeError != null)
                            throw new IOException("存储已暂停，待写批次保留到重试。", _writeError);
                    }
                    await WriteBatchAsync(batch.Readings).ConfigureAwait(false);
                    lock (_writeLock)
                    {
                        _pendingReadings -= batch.Readings.Length;
                        _savedReadings += batch.Readings.Length;
                    }
                    batch.Completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    bool firstFailure;
                    lock (_writeLock)
                    {
                        firstFailure = _writeError == null;
                        _writeError ??= ex;
                        _failedBatches.Add(batch);
                    }
                    // Stop producers synchronously before scheduling any UI notification.
                    // Accepted batches remain available for the explicit retry operation.
                    if (firstFailure) _dataBus.PublishAcquisitionFault(ex);
                    batch.Completion.TrySetException(ex);
                    System.Diagnostics.Trace.TraceError($"保存失败，保留 {batch.Readings.Length} 条待重试: {ex}");
                }
                PublishWriteStatus();
                Interlocked.Decrement(ref _queuedBatches);
            }
        }
        _writerIdle = true;
    }

    // SQLite 瞬时错误码：忙 / 被锁。多进程或长事务下可能短暂出现，退避重试通常即可成功。
    private const int SqliteBusy = 5;    // SQLITE_BUSY
    private const int SqliteLocked = 6;  // SQLITE_LOCKED

    private async Task WriteBatchAsync(IReadOnlyList<MagnetometerReading> batch)
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
        }
    }

    private async Task WriteBatchOnceAsync(IReadOnlyList<MagnetometerReading> batch)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        var sessionToNames = new Dictionary<string, string[]>();
        foreach (var r in batch)
        {
            if (string.IsNullOrWhiteSpace(r.SessionId))
                throw new ArgumentException("读数缺少采集会话 ID。");
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
            if (r.ChannelValues.Length != sessionToNames[r.SessionId].Length)
                throw new ArgumentException("读数通道数与会话不一致，数据已保留待处理。");
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

        if (json == null)
            throw new ArgumentException($"会话不存在或缺少通道定义: {sessionId}");
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

    private static string BuildDataJson(double[] values, double[]? original, string[] channelNames, bool isCalibrated, bool isOrthoCorrected)
    {
        if (values.Any(v => !double.IsFinite(v)) || original?.Any(v => !double.IsFinite(v)) == true)
            throw new ArgumentException("不能保存包含 NaN 或无穷大的读数。");

        var payload = new Dictionary<string, object?>
        {
            ["values"] = values,
            ["original"] = original,
            ["isCalibrated"] = isCalibrated ? 1 : 0,
            ["isOrthoCorrected"] = isOrthoCorrected ? 1 : 0,
        };

        return JsonSerializer.Serialize(payload);
    }

    private static (double[] values, double[]? original, bool isCalibrated, bool isOrthoCorrected) ParseDataJson(string json, string[] channelNames)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        double[] ReadValues(JsonElement element) => element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().Select(v => v.GetDouble()).ToArray()
            : ExtractOrdered(element.Deserialize<Dictionary<string, double>>(), channelNames);
        var values = ReadValues(root.GetProperty("values"));
        var original = root.TryGetProperty("original", out var raw) && raw.ValueKind != JsonValueKind.Null
            ? ReadValues(raw) : null;
        return (values, original,
            root.TryGetProperty("isCalibrated", out var cal) && cal.GetInt32() == 1,
            root.TryGetProperty("isOrthoCorrected", out var ortho) && ortho.GetInt32() == 1);
    }

    private static double[] ExtractOrdered(Dictionary<string, double>? dict, string[] channelNames)
    {
        if (dict == null || dict.Count == 0) return [];

        if (channelNames.Length > 0)
        {
            var result = new List<double>(channelNames.Length);
            foreach (var name in channelNames)
            {
                result.Add(dict.TryGetValue(name, out var v) ? v : double.NaN);
            }
            if (channelNames.Any(dict.ContainsKey))
            {
                result.AddRange(dict.Where(pair => !channelNames.Contains(pair.Key)).Select(pair => pair.Value));
                return result.ToArray();
            }
        }

        // Fallback: 按 CH0/CH1/... 或字典本身顺序
        return dict.Values.ToArray();
    }

    private static object MapReadingToParam(MagnetometerReading r, string[] channelNames)
    {
        return new
        {
            r.SessionId,
            Timestamp = r.Timestamp.ToUniversalTime().ToString("O"),
            Data = BuildDataJson(r.ChannelValues, r.OriginalChannelValues, channelNames, r.IsCalibrated, r.IsOrthogonalityCorrected),
        };
    }

    private static MagnetometerReading MapRowToReading(dynamic row, string[] channelNames, SensorType sensorType)
    {
        var dataJson = (string)row.data;
        var (values, original, isCal, isOrtho) = ParseDataJson(dataJson, channelNames);

        return new MagnetometerReading
        {
            Id = (long)row.id,
            SessionId = (string)row.session_id,
            Timestamp = ParseUtcAsLocal((string)row.timestamp),
            ChannelValues = values,
            SensorType = sensorType,
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

        int channelCount = (int)(long)row.channel_count;
        string[] savedUnits = row.channel_units is string unitsJson && !string.IsNullOrWhiteSpace(unitsJson)
            ? JsonSerializer.Deserialize<string[]>(unitsJson) ?? [] : [];
        // Missing historical units are unknown, not implicitly nT or dimensionless.
        string[] channelUnits = Enumerable.Range(0, channelCount)
            .Select(i => i < savedUnits.Length ? savedUnits[i] ?? "未知单位" : "未知单位").ToArray();
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
            StartedAtUtc = ParseUtc((string)row.started_at),
            EndedAtUtc = row.ended_at is string endedUtc && !string.IsNullOrEmpty(endedUtc)
                ? ParseUtc(endedUtc)
                : null,
            SensorType = sensorType,
            SampleRate = (double)row.sample_rate,
            ChannelCount = (int)(long)row.channel_count,
            ChannelNames = channelNames,
            ChannelUnits = channelUnits,
            LegacyDataTable = row.legacy_data_table as string,
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
            Timestamp = reading.Timestamp.ToUniversalTime().ToString("O"),
            reading.CorrectionProfileId,
            Data = BuildDataJson(reading.CorrectedValues, null, channelNames, false, reading.IsOrthogonalityCorrected),
            CorrectedAt = reading.CorrectedAt.ToUniversalTime().ToString("O"),
        };
    }

    private static CorrectedReading MapRowToCorrectedReading(dynamic row, string[] channelNames, SensorType sensorType)
    {
        var dataJson = (string)row.data;
        var (values, _, _, isOrtho) = ParseDataJson(dataJson, channelNames);

        double? totalField = null;
        if ((sensorType == SensorType.TriaxialFluxgate && values.Length == 3) ||
            (sensorType == SensorType.DualTriaxialFluxgate && values.Length == 6))
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
    private static DateTime ParseUtcAsLocal(string iso) => ParseUtc(iso).ToLocalTime();

    /// <summary>存储的时间戳按 UTC 解析（无时区标记时视为 UTC），不经过本地时间。</summary>
    private static DateTime ParseUtc(string iso)
    {
        var dt = DateTime.Parse(iso, null, DateTimeStyles.RoundtripKind);
        return dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        // 未保存数据仍可在修复后重试，不因关闭失败而释放保留队列。
        WaitForPendingWritesAsync(30000).GetAwaiter().GetResult();
        lock (_writeLock)
        {
            _disposed = true;
            _writeChannel.Writer.Complete();
        }
        _consumerTask.GetAwaiter().GetResult();
    }

    #endregion
}
