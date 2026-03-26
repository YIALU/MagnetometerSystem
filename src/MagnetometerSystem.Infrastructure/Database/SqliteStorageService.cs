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
/// 使用 Channel&lt;MagnetometerReading&gt; 实现异步批量写入队列。
/// </summary>
public class SqliteStorageService : IDataStorageService, IDisposable
{
    private readonly DatabaseInitializer _dbInit;
    private readonly DataBus _dataBus;
    private readonly Channel<MagnetometerReading> _writeChannel;
    private readonly Task _consumerTask;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    private const int BatchSize = 500;

    public SqliteStorageService(DatabaseInitializer dbInit, DataBus dataBus)
    {
        _dbInit = dbInit;
        _dataBus = dataBus;

        // 无界 Channel，SingleReader 优化性能
        _writeChannel = Channel.CreateUnbounded<MagnetometerReading>(
            new UnboundedChannelOptions { SingleReader = true });

        // 启动后台消费者
        _consumerTask = Task.Run(ConsumeWriteQueueAsync);
    }

    /// <inheritdoc />
    public async Task<string> StartSessionAsync(string name, SensorConfig sensorConfig, ConnectionConfig connectionConfig)
    {
        var sessionId = Guid.NewGuid().ToString();

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
            ChannelNames = JsonSerializer.Serialize(sensorConfig.ChannelNames),
            DeviceInfo = sensorConfig.SerialNumber,
            ConnectionType = connectionConfig.Type.ToString(),
        });

        _dataBus.PublishSessionStarted(sessionId);
        return sessionId;
    }

    /// <inheritdoc />
    public async Task EndSessionAsync(string sessionId)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        // 统计该会话的读数总数
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
        // 入队后立即返回，不阻塞调用方
        foreach (var reading in readings)
        {
            _writeChannel.Writer.TryWrite(reading);
        }
        return Task.CompletedTask;
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

        var sql = """
            SELECT id, session_id, timestamp, ch0, ch1, ch2, ch3, ch4, ch5,
                   extra_channels, total_field, is_calibrated, is_ortho_corrected, original_channel_values
            FROM readings
            WHERE session_id = @SessionId
            """;

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
            readings.Add(MapRowToReading(row));
        }

        return readings;
    }

    /// <inheritdoc />
    public async Task DeleteSessionAsync(string sessionId)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("PRAGMA foreign_keys=ON;");

        // 外键级联删除会自动清除关联的 readings 记录
        await conn.ExecuteAsync(
            "DELETE FROM sessions WHERE id = @Id",
            new { Id = sessionId });
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

    #region 校正数据存储（独立于原始数据）

    /// <inheritdoc />
    public async Task SaveCorrectedReadingsAsync(IEnumerable<CorrectedReading> readings)
    {
        var readingsList = readings.ToList();
        if (readingsList.Count == 0) return;

        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();

        const string sql = """
            INSERT INTO corrected_readings
                (original_reading_id, session_id, timestamp, correction_profile_id,
                 ch0, ch1, ch2, ch3, ch4, ch5, extra_channels,
                 corrected_total_field, is_ortho_corrected, corrected_at)
            VALUES
                (@OriginalReadingId, @SessionId, @Timestamp, @CorrectionProfileId,
                 @Ch0, @Ch1, @Ch2, @Ch3, @Ch4, @Ch5, @ExtraChannels,
                 @CorrectedTotalField, @IsOrthoCorrected, @CorrectedAt)
            """;

        foreach (var reading in readingsList)
        {
            var param = MapCorrectedReadingToParam(reading);
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

        var sql = "SELECT * FROM corrected_readings WHERE session_id = @SessionId";
        if (correctionProfileId != null)
            sql += " AND correction_profile_id = @ProfileId";
        sql += " ORDER BY timestamp ASC";

        var rows = await conn.QueryAsync(sql, new { SessionId = sessionId, ProfileId = correctionProfileId });
        return rows.Select(MapRowToCorrectedReading).ToList();
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
            while (await reader.WaitToReadAsync(_cts.Token))
            {
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
            // 正常取消，写入剩余数据
        }

        // 消费残余数据
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

    private async Task WriteBatchAsync(List<MagnetometerReading> batch)
    {
        try
        {
            using var conn = new SqliteConnection(_dbInit.ConnectionString);
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            const string sql = """
                INSERT INTO readings (session_id, timestamp, ch0, ch1, ch2, ch3, ch4, ch5,
                    extra_channels, total_field, is_calibrated, is_ortho_corrected, original_channel_values)
                VALUES (@SessionId, @Timestamp, @Ch0, @Ch1, @Ch2, @Ch3, @Ch4, @Ch5,
                    @ExtraChannels, @TotalField, @IsCalibrated, @IsOrthoCorrected, @OriginalChannelValues)
                """;

            foreach (var r in batch)
            {
                var param = MapReadingToParam(r);
                await conn.ExecuteAsync(sql, param, tx);
            }

            tx.Commit();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"SqliteStorageService.WriteBatchAsync failed ({batch.Count} readings): {ex.Message}");
        }
    }

    #endregion

    #region 映射方法

    private static object MapReadingToParam(MagnetometerReading r)
    {
        var cv = r.ChannelValues;
        return new
        {
            r.SessionId,
            Timestamp = r.Timestamp.ToString("O"),
            Ch0 = cv.Length > 0 ? cv[0] : (double?)null,
            Ch1 = cv.Length > 1 ? cv[1] : (double?)null,
            Ch2 = cv.Length > 2 ? cv[2] : (double?)null,
            Ch3 = cv.Length > 3 ? cv[3] : (double?)null,
            Ch4 = cv.Length > 4 ? cv[4] : (double?)null,
            Ch5 = cv.Length > 5 ? cv[5] : (double?)null,
            ExtraChannels = cv.Length > 6
                ? JsonSerializer.Serialize(cv[6..])
                : null,
            r.TotalField,
            IsCalibrated = r.IsCalibrated ? 1 : 0,
            IsOrthoCorrected = r.IsOrthogonalityCorrected ? 1 : 0,
            OriginalChannelValues = r.OriginalChannelValues != null
                ? JsonSerializer.Serialize(r.OriginalChannelValues)
                : null,
        };
    }

    private static MagnetometerReading MapRowToReading(dynamic row)
    {
        var values = new List<double>();
        if (row.ch0 != null) values.Add((double)row.ch0);
        if (row.ch1 != null) values.Add((double)row.ch1);
        if (row.ch2 != null) values.Add((double)row.ch2);
        if (row.ch3 != null) values.Add((double)row.ch3);
        if (row.ch4 != null) values.Add((double)row.ch4);
        if (row.ch5 != null) values.Add((double)row.ch5);

        if (row.extra_channels is string json && !string.IsNullOrEmpty(json))
        {
            var extra = JsonSerializer.Deserialize<double[]>(json);
            if (extra != null) values.AddRange(extra);
        }

        double[]? originalValues = null;
        if (row.original_channel_values is string origJson && !string.IsNullOrEmpty(origJson))
        {
            originalValues = JsonSerializer.Deserialize<double[]>(origJson);
        }

        return new MagnetometerReading
        {
            Id = (long)row.id,
            SessionId = (string)row.session_id,
            Timestamp = DateTime.Parse((string)row.timestamp, null, DateTimeStyles.RoundtripKind),
            ChannelValues = values.ToArray(),
            OriginalChannelValues = originalValues,
            TotalField = row.total_field as double?,
            IsCalibrated = (long)row.is_calibrated == 1,
            IsOrthogonalityCorrected = (long)row.is_ortho_corrected == 1,
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
            StartedAt = DateTime.Parse((string)row.started_at, null, DateTimeStyles.RoundtripKind),
            EndedAt = row.ended_at is string endedAt && !string.IsNullOrEmpty(endedAt)
                ? DateTime.Parse(endedAt, null, DateTimeStyles.RoundtripKind)
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

    private static object MapCorrectedReadingToParam(CorrectedReading reading)
    {
        var values = reading.CorrectedValues;
        return new
        {
            reading.OriginalReadingId,
            reading.SessionId,
            Timestamp = reading.Timestamp.ToString("O"),
            reading.CorrectionProfileId,
            Ch0 = values.Length > 0 ? values[0] : (double?)null,
            Ch1 = values.Length > 1 ? values[1] : (double?)null,
            Ch2 = values.Length > 2 ? values[2] : (double?)null,
            Ch3 = values.Length > 3 ? values[3] : (double?)null,
            Ch4 = values.Length > 4 ? values[4] : (double?)null,
            Ch5 = values.Length > 5 ? values[5] : (double?)null,
            ExtraChannels = values.Length > 6 ? JsonSerializer.Serialize(values[6..]) : null,
            reading.CorrectedTotalField,
            IsOrthoCorrected = reading.IsOrthogonalityCorrected ? 1 : 0,
            CorrectedAt = reading.CorrectedAt.ToString("O")
        };
    }

    private static CorrectedReading MapRowToCorrectedReading(dynamic row)
    {
        var values = new List<double>();
        if (row.ch0 != null) values.Add((double)row.ch0);
        if (row.ch1 != null) values.Add((double)row.ch1);
        if (row.ch2 != null) values.Add((double)row.ch2);
        if (row.ch3 != null) values.Add((double)row.ch3);
        if (row.ch4 != null) values.Add((double)row.ch4);
        if (row.ch5 != null) values.Add((double)row.ch5);

        string? extraJson = row.extra_channels as string;
        if (!string.IsNullOrEmpty(extraJson))
        {
            var extras = JsonSerializer.Deserialize<double[]>(extraJson);
            if (extras != null) values.AddRange(extras);
        }

        return new CorrectedReading
        {
            Id = (long)row.id,
            OriginalReadingId = (long)row.original_reading_id,
            SessionId = (string)row.session_id,
            Timestamp = DateTime.Parse((string)row.timestamp, null, DateTimeStyles.RoundtripKind),
            CorrectionProfileId = (string)row.correction_profile_id,
            CorrectedValues = values.ToArray(),
            CorrectedTotalField = row.corrected_total_field as double?,
            IsOrthogonalityCorrected = ((long)row.is_ortho_corrected) == 1,
            CorrectedAt = DateTime.Parse((string)row.corrected_at, null, DateTimeStyles.RoundtripKind)
        };
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 通知消费者不再有新数据
        _writeChannel.Writer.Complete();

        // 取消等待并等待消费者完成（最多等待 5 秒）
        _cts.Cancel();
        _consumerTask.Wait(TimeSpan.FromSeconds(5));

        _cts.Dispose();
    }

    #endregion
}
