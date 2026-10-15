# D-1: SQLite 数据库存储服务

## 基本信息

| 属性     | 值                                |
| -------- | --------------------------------- |
| 任务编号 | D-1                               |
| 优先级   | P0                                |
| 阶段     | Phase 3 - 数据持久化              |
| 流       | Stream A (Agent 1)                |
| 前置依赖 | 无                                |
| 预估工时 | 6-8 小时                          |

## 功能需求

### 概述
实现基于 SQLite 的数据持久化服务，作为 `IDataStorageService` 接口的具体实现。该服务需支持高频写入（500 Hz x 6 通道）且不丢数据，同时保证读取性能满足 UI 交互需求。

### 详细需求

1. **数据库初始化**
   - 首次运行时自动创建数据库文件及所有表结构
   - 启用 WAL (Write-Ahead Logging) 模式以提升并发读写性能
   - 支持版本化迁移机制，便于后续 schema 升级

2. **会话管理**
   - `StartSessionAsync`: 创建新会话记录，生成 GUID 作为 SessionId，返回该 ID
   - `EndSessionAsync`: 更新会话的 `ended_at` 字段，统计并更新 `total_readings`
   - `GetSessionsAsync`: 返回所有会话列表，按 `started_at` 降序排列
   - `DeleteSessionAsync`: 级联删除会话及其关联的所有读数记录

3. **高频数据写入**
   - `SaveReadingsAsync` 使用 `System.Threading.Channels.Channel<MagnetometerReading>` 实现异步队列
   - 调用方将数据入队后立即返回，不阻塞采集线程
   - 后台消费者任务从 Channel 中批量读取（每批最多 500 条），包装在单个事务中写入
   - 写入失败时记录日志，不抛出异常到调用方

4. **数据查询**
   - `GetReadingsAsync`: 支持按 SessionId + 可选时间范围查询
   - 查询 100,000 条读数的响应时间 < 100ms

5. **DataBus 扩展**
   - 在 `DataBus` 中新增 `SessionStarted` 和 `SessionEnded` 事件
   - 在 `SqliteStorageService` 启动/结束会话时通过 DataBus 发布通知

6. **DI 注册**
   - 在 `App.xaml.cs` 中注册 `DatabaseInitializer` 和 `IDataStorageService -> SqliteStorageService`

## 接口契约

### 已定义接口（无需修改）

```csharp
// 文件: src/MagnetometerSystem.Core/Storage/IDataStorageService.cs
public interface IDataStorageService
{
    Task<string> StartSessionAsync(string name, SensorConfig sensorConfig, ConnectionConfig connectionConfig);
    Task EndSessionAsync(string sessionId);
    Task SaveReadingsAsync(IEnumerable<MagnetometerReading> readings);
    Task<IReadOnlyList<SessionInfo>> GetSessionsAsync();
    Task<IReadOnlyList<MagnetometerReading>> GetReadingsAsync(
        string sessionId, DateTime? startTime = null, DateTime? endTime = null);
    Task DeleteSessionAsync(string sessionId);
}
```

### DataBus 扩展（需修改）

```csharp
// 文件: src/MagnetometerSystem.Core/Services/DataBus.cs
// 新增以下事件和方法:

/// <summary>会话开始时触发，参数为 sessionId</summary>
public event Action<string>? SessionStarted;

/// <summary>会话结束时触发，参数为 sessionId</summary>
public event Action<string>? SessionEnded;

public void PublishSessionStarted(string sessionId)
{
    SessionStarted?.Invoke(sessionId);
}

public void PublishSessionEnded(string sessionId)
{
    SessionEnded?.Invoke(sessionId);
}
```

### DatabaseInitializer 公共 API

```csharp
// 文件: src/MagnetometerSystem.Infrastructure/Database/DatabaseInitializer.cs
namespace MagnetometerSystem.Infrastructure.Database;

public class DatabaseInitializer
{
    /// <summary>
    /// 初始化数据库：创建目录、创建表、启用 WAL 模式。
    /// 应在应用启动时调用一次。
    /// </summary>
    public Task InitializeAsync();

    /// <summary>获取数据库连接字符串</summary>
    public string ConnectionString { get; }
}
```

## 文件清单

### 新建文件

| 文件路径 | 说明 |
| -------- | ---- |
| `src/MagnetometerSystem.Infrastructure/Database/DatabaseInitializer.cs` | 数据库初始化器：创建 DB 文件、执行 DDL、启用 WAL |
| `src/MagnetometerSystem.Infrastructure/Database/SqliteStorageService.cs` | `IDataStorageService` 的 SQLite + Dapper 实现 |
| `src/MagnetometerSystem.Infrastructure/Database/Migrations/V1_InitialSchema.sql` | 初始 DDL 脚本 |

### 修改文件

| 文件路径 | 修改内容 |
| -------- | -------- |
| `src/MagnetometerSystem.Core/Services/DataBus.cs` | 新增 `SessionStarted`/`SessionEnded` 事件及发布方法 |
| `src/MagnetometerSystem.App/App.xaml.cs` | 在 DI 容器中注册数据库相关服务 |
| `src/MagnetometerSystem.Infrastructure/MagnetometerSystem.Infrastructure.csproj` | 添加 NuGet 包引用 |

### NuGet 包

在 `MagnetometerSystem.Infrastructure` 项目中安装：

| 包名 | 用途 |
| ---- | ---- |
| `Microsoft.Data.Sqlite` | SQLite 数据库驱动 |
| `Dapper` | 轻量级 ORM，简化 SQL 映射 |

## 数据库变更

### V1 初始 Schema

```sql
-- 文件: Infrastructure/Database/Migrations/V1_InitialSchema.sql

CREATE TABLE IF NOT EXISTS sessions (
    id              TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    started_at      TEXT NOT NULL,
    ended_at        TEXT,
    sensor_type     TEXT NOT NULL,
    sample_rate     REAL NOT NULL,
    channel_count   INTEGER NOT NULL,
    channel_names   TEXT,           -- JSON 数组: ["X","Y","Z"]
    device_info     TEXT,           -- 设备信息 (序列号等)
    connection_type TEXT,           -- "Serial" | "Tcp"
    notes           TEXT,
    total_readings  INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS readings (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id          TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
    timestamp           TEXT NOT NULL,   -- ISO 8601 格式
    ch0                 REAL,
    ch1                 REAL,
    ch2                 REAL,
    ch3                 REAL,
    ch4                 REAL,
    ch5                 REAL,
    extra_channels      TEXT,            -- 当通道数 > 6 时, 存储为 JSON 数组
    total_field         REAL,
    is_calibrated       INTEGER DEFAULT 0,
    is_ortho_corrected  INTEGER DEFAULT 0
);

CREATE INDEX IF NOT EXISTS idx_readings_session_time
    ON readings(session_id, timestamp);

CREATE TABLE IF NOT EXISTS settings (
    key         TEXT PRIMARY KEY,
    value       TEXT NOT NULL,
    updated_at  TEXT NOT NULL
);
```

### 数据库文件路径

```
%LOCALAPPDATA%/MagnetometerSystem/magnetometer.db
```

对应代码：
```csharp
Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "MagnetometerSystem",
    "magnetometer.db");
```

## 实现指南

### 1. DatabaseInitializer

```csharp
public class DatabaseInitializer
{
    private readonly string _dbPath;

    public string ConnectionString => $"Data Source={_dbPath}";

    public DatabaseInitializer()
    {
        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MagnetometerSystem",
            "magnetometer.db");
    }

    public async Task InitializeAsync()
    {
        // 1. 确保目录存在
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);

        // 2. 打开连接，启用 WAL
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("PRAGMA journal_mode=WAL;");
        await connection.ExecuteAsync("PRAGMA foreign_keys=ON;");

        // 3. 读取并执行 V1 迁移脚本
        //    脚本作为嵌入资源或从文件系统加载
        var sql = LoadMigrationSql("V1_InitialSchema.sql");
        await connection.ExecuteAsync(sql);
    }
}
```

### 2. SqliteStorageService - 异步批量写入队列

核心设计：使用 `Channel<MagnetometerReading>` 实现生产者-消费者模式。

```csharp
public class SqliteStorageService : IDataStorageService, IDisposable
{
    private readonly DatabaseInitializer _dbInit;
    private readonly DataBus _dataBus;
    private readonly Channel<MagnetometerReading> _writeChannel;
    private readonly Task _consumerTask;
    private readonly CancellationTokenSource _cts = new();

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

    public Task SaveReadingsAsync(IEnumerable<MagnetometerReading> readings)
    {
        // 入队后立即返回，不阻塞调用方
        foreach (var reading in readings)
        {
            _writeChannel.Writer.TryWrite(reading);
        }
        return Task.CompletedTask;
    }

    private async Task ConsumeWriteQueueAsync()
    {
        var batch = new List<MagnetometerReading>(500);
        var reader = _writeChannel.Reader;

        while (await reader.WaitToReadAsync(_cts.Token))
        {
            batch.Clear();
            while (batch.Count < 500 && reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            if (batch.Count > 0)
            {
                await WriteBatchAsync(batch);
            }
        }
    }

    private async Task WriteBatchAsync(List<MagnetometerReading> batch)
    {
        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();
        using var tx = await conn.BeginTransactionAsync();

        try
        {
            const string sql = """
                INSERT INTO readings (session_id, timestamp, ch0, ch1, ch2, ch3, ch4, ch5,
                    extra_channels, total_field, is_calibrated, is_ortho_corrected)
                VALUES (@SessionId, @Timestamp, @Ch0, @Ch1, @Ch2, @Ch3, @Ch4, @Ch5,
                    @ExtraChannels, @TotalField, @IsCalibrated, @IsOrthoCorrected)
                """;

            foreach (var r in batch)
            {
                var param = MapReadingToParam(r);
                await conn.ExecuteAsync(sql, param, (System.Data.IDbTransaction)tx);
            }

            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            // TODO: 记录日志，不向上抛出
        }
    }
}
```

### 3. 通道值映射策略

`MagnetometerReading.ChannelValues` (double[]) 与 `readings` 表 ch0-ch5 列的映射：

```csharp
private static object MapReadingToParam(MagnetometerReading r)
{
    var cv = r.ChannelValues;
    return new
    {
        r.SessionId,
        Timestamp = r.Timestamp.ToString("O"),  // ISO 8601
        Ch0 = cv.Length > 0 ? cv[0] : (double?)null,
        Ch1 = cv.Length > 1 ? cv[1] : (double?)null,
        Ch2 = cv.Length > 2 ? cv[2] : (double?)null,
        Ch3 = cv.Length > 3 ? cv[3] : (double?)null,
        Ch4 = cv.Length > 4 ? cv[4] : (double?)null,
        Ch5 = cv.Length > 5 ? cv[5] : (double?)null,
        ExtraChannels = cv.Length > 6
            ? System.Text.Json.JsonSerializer.Serialize(cv[6..])
            : null,
        r.TotalField,
        IsCalibrated = r.IsCalibrated ? 1 : 0,
        IsOrthoCorrected = r.IsOrthogonalityCorrected ? 1 : 0,
    };
}
```

反向映射（查询时从 ch0-ch5 还原为 double[]）：

```csharp
private static MagnetometerReading MapRowToReading(dynamic row, int channelCount)
{
    var values = new List<double>();
    if (row.ch0 != null) values.Add((double)row.ch0);
    if (row.ch1 != null) values.Add((double)row.ch1);
    if (row.ch2 != null) values.Add((double)row.ch2);
    if (row.ch3 != null) values.Add((double)row.ch3);
    if (row.ch4 != null) values.Add((double)row.ch4);
    if (row.ch5 != null) values.Add((double)row.ch5);

    if (row.extra_channels is string json)
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
```

### 4. StartSessionAsync 实现要点

```csharp
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
```

### 5. DI 注册

```csharp
// 文件: src/MagnetometerSystem.App/App.xaml.cs
// 在 OnStartup 方法的服务注册区域添加:

// #region Stream A - 数据存储
services.AddSingleton<DatabaseInitializer>();
services.AddSingleton<IDataStorageService, SqliteStorageService>();
// #endregion

// 在 BuildServiceProvider 之后、mainWindow.Show() 之前:
var dbInit = Services.GetRequiredService<DatabaseInitializer>();
await dbInit.InitializeAsync();
```

> 注意：`OnStartup` 需改为 `async void` 或使用 `.GetAwaiter().GetResult()` 包装初始化调用。推荐后者以避免死锁风险。

### 6. Dispose 模式

`SqliteStorageService` 实现 `IDisposable`，在应用退出时：
1. 调用 `_writeChannel.Writer.Complete()` 通知消费者不再有新数据
2. 等待 `_consumerTask` 完成（设置合理超时），确保队列中残余数据全部写入
3. 释放 `_cts`

## 验收标准

| # | 验收项 | 通过条件 |
| - | ------ | -------- |
| 1 | 数据库自动创建 | 首次运行时在 `%LOCALAPPDATA%/MagnetometerSystem/` 下自动创建 `magnetometer.db`，包含 3 张表 |
| 2 | WAL 模式 | 数据库创建后执行 `PRAGMA journal_mode;` 返回 `wal` |
| 3 | 高频写入无丢失 | 以 500 Hz x 6 通道速率连续写入 60 秒（180,000 条），数据库中记录数 = 发送数 |
| 4 | 写入不阻塞 | `SaveReadingsAsync` 调用耗时 < 1ms（仅入队操作） |
| 5 | 批量事务 | 通过日志或调试确认写入以事务批量提交，每批最多 500 条 |
| 6 | 查询性能 | 查询某会话 100,000 条读数耗时 < 100ms |
| 7 | 会话生命周期 | `StartSessionAsync` 返回有效 GUID，`EndSessionAsync` 正确更新 `ended_at` 和 `total_readings` |
| 8 | 级联删除 | `DeleteSessionAsync` 删除会话后，关联读数记录全部清除 |
| 9 | DataBus 事件 | `SessionStarted`/`SessionEnded` 事件在会话创建/结束时正确触发 |
| 10 | DI 注入 | 通过构造函数注入 `IDataStorageService` 可正常获取 `SqliteStorageService` 实例 |

## 单元测试要求

测试项目: `tests/MagnetometerSystem.Infrastructure.Tests/`

### 测试类: `SqliteStorageServiceTests`

使用内存数据库 (`Data Source=:memory:`) 或临时文件进行隔离测试。

| 测试方法 | 验证内容 |
| -------- | -------- |
| `StartSession_ReturnsGuid_InsertsRow` | 创建会话后返回非空 GUID，数据库中有对应记录 |
| `EndSession_UpdatesTimestampAndCount` | 写入若干读数后结束会话，`ended_at` 非空且 `total_readings` 正确 |
| `SaveReadings_BatchWriteCorrectly` | 写入 1000 条读数，等待消费者完成，数据库中记录数 = 1000 |
| `SaveReadings_ChannelMapping_1Ch` | 单通道读数: ch0 有值，ch1-ch5 为 NULL |
| `SaveReadings_ChannelMapping_3Ch` | 三通道读数: ch0/ch1/ch2 有值，ch3-ch5 为 NULL |
| `SaveReadings_ChannelMapping_6Ch` | 六通道读数: ch0-ch5 均有值，extra_channels 为 NULL |
| `SaveReadings_ChannelMapping_MoreThan6Ch` | 八通道读数: ch0-ch5 有值，extra_channels 包含剩余 2 通道的 JSON |
| `GetReadings_FilterByTimeRange` | 指定时间范围查询，仅返回范围内的数据 |
| `GetReadings_ReturnsAllWhenNoFilter` | 不指定时间范围时返回会话全部读数 |
| `DeleteSession_CascadeDeleteReadings` | 删除会话后，关联读数记录数为 0 |
| `GetSessions_OrderByStartedAtDesc` | 创建多个会话，返回列表按开始时间降序 |
| `ConcurrentWrite_NoDataLoss` | 多线程同时调用 `SaveReadingsAsync`，所有数据最终写入成功 |

### 测试辅助

```csharp
// 创建临时数据库的 Helper
private static async Task<(SqliteStorageService service, string dbPath)> CreateTestServiceAsync()
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.db");
    var initializer = new DatabaseInitializer(dbPath); // 需支持自定义路径的构造函数重载
    await initializer.InitializeAsync();
    var dataBus = new DataBus();
    var service = new SqliteStorageService(initializer, dataBus);
    return (service, dbPath);
}
```

> 注意：`DatabaseInitializer` 应提供接受自定义路径的构造函数重载，以便测试时使用临时文件而非生产路径。
