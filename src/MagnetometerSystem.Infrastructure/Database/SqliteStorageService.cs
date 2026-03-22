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
                   extra_channels, total_field, is_calibrated, is_ortho_corrected
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
                    extra_channels, total_field, is_calibrated, is_ortho_corrected)
                VALUES (@SessionId, @Timestamp, @Ch0, @Ch1, @Ch2, @Ch3, @Ch4, @Ch5,
                    @ExtraChannels, @TotalField, @IsCalibrated, @IsOrthoCorrected)
                """;

            foreach (var r in batch)
            {
                var param = MapReadingToParam(r);
                await conn.ExecuteAsync(sql, param, tx);
            }

            tx.Commit();
        }
        catch (Exception)
        {
            // 写入失败时记录日志，不抛出异常到调用方
            // TODO: 集成 Serilog 后替换为正式日志
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

        return new MagnetometerReading
        {
            Id = (long)row.id,
            SessionId = (string)row.session_id,
            Timestamp = DateTime.Parse((string)row.timestamp, null, DateTimeStyles.RoundtripKind),
            ChannelValues = values.ToArray(),
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
