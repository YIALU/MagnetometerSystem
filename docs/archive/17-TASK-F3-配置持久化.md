# TASK-F3: 配置持久化（AppConfigService）

**文档版本**: v1.0
**更新日期**: 2026-03-21
**优先级**: P1
**阶段**: Phase 5
**流**: Stream E
**依赖**: D-1（使用同一 SQLite 数据库）

---

## 一、基本信息

### 背景

当前系统所有配置参数（连接参数、图表刷新率等）均为硬编码默认值，用户每次启动应用都需要重新设置。需要将用户偏好持久化存储，实现"关闭后重开，配置还在"的体验。

### 目标

基于 SQLite 的 `settings` 表实现键值对配置存储服务，支持泛型读写、整体加载/保存，并在 ViewModel 启动时自动加载用户上次的配置。

---

## 二、功能需求

1. **键值对存储** — 通过 `key` 存取任意类型的配置值，值以 JSON 序列化存储。
2. **整体设置模型** — `AppSettings` 类封装常用配置项，支持一次性加载和保存。
3. **启动加载** — 应用启动时自动从数据库加载配置，填充到相关 ViewModel。
4. **变更保存** — 配置变更后自动或手动保存到数据库。
5. **默认值回退** — 数据库中无对应键时返回 `AppSettings` 中定义的默认值。
6. **与 D-1 共享数据库** — 复用 D-1 任务创建的 SQLite 数据库文件，仅新增 `settings` 表。

---

## 三、接口契约

### IAppConfigService 接口

```csharp
namespace MagnetometerSystem.Infrastructure.Configuration;

/// <summary>
/// 应用配置持久化服务接口。
/// </summary>
public interface IAppConfigService
{
    /// <summary>
    /// 获取指定键的配置值。键不存在时返回 default(T)。
    /// </summary>
    Task<T?> GetAsync<T>(string key);

    /// <summary>
    /// 设置指定键的配置值。键存在则更新，不存在则插入。
    /// </summary>
    Task SetAsync<T>(string key, T value);

    /// <summary>
    /// 加载完整的应用设置。各字段从对应键读取，缺失时使用默认值。
    /// </summary>
    Task<AppSettings> LoadSettingsAsync();

    /// <summary>
    /// 保存完整的应用设置。各字段写入对应键。
    /// </summary>
    Task SaveSettingsAsync(AppSettings settings);
}
```

### AppSettings 数据类

```csharp
namespace MagnetometerSystem.Infrastructure.Configuration;

/// <summary>
/// 应用全局设置数据模型。
/// </summary>
public class AppSettings
{
    public string? DefaultPortName { get; set; }
    public int DefaultBaudRate { get; set; } = 115200;
    public string? DefaultIpAddress { get; set; }
    public int DefaultPort { get; set; } = 5000;
    public string DataStoragePath { get; set; } = "";
    public bool AutoSaveEnabled { get; set; } = true;
    public int ChartRefreshRate { get; set; } = 30;
    public string ThemeName { get; set; } = "Default";
}
```

### 数据库表结构

```sql
CREATE TABLE IF NOT EXISTS settings (
    key   TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL  -- JSON 序列化的值
);
```

### 键名约定

| 键名 | 对应属性 | 值示例 |
|------|---------|--------|
| `connection.defaultPortName` | DefaultPortName | `"COM3"` |
| `connection.defaultBaudRate` | DefaultBaudRate | `115200` |
| `connection.defaultIpAddress` | DefaultIpAddress | `"192.168.1.100"` |
| `connection.defaultPort` | DefaultPort | `5000` |
| `storage.dataPath` | DataStoragePath | `"D:\\Data"` |
| `storage.autoSave` | AutoSaveEnabled | `true` |
| `chart.refreshRate` | ChartRefreshRate | `30` |
| `ui.theme` | ThemeName | `"Default"` |

---

## 四、文件清单

| 操作 | 文件路径 | 说明 |
|------|---------|------|
| 新建 | `Infrastructure/Configuration/IAppConfigService.cs` | 配置服务接口 |
| 新建 | `Infrastructure/Configuration/AppConfigService.cs` | SQLite 实现 |
| 新建 | `Infrastructure/Configuration/AppSettings.cs` | 设置数据模型 |
| 修改 | `App/App.xaml.cs` | 注册 IAppConfigService 到 DI，启动时加载配置 |
| 修改 | `App/ViewModels/ConnectionViewModel.cs` | 从设置加载默认连接参数 |
| 修改 | `App/ViewModels/RealtimeChartViewModel.cs` | 从设置加载默认刷新率 |

---

## 五、实现指南

### 5.1 AppConfigService 实现

```csharp
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.Infrastructure.Configuration;

public class AppConfigService : IAppConfigService
{
    private readonly string _connectionString;

    public AppConfigService(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        InitializeTable();
    }

    private void InitializeTable()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY NOT NULL,
                value TEXT NOT NULL
            );";
        cmd.ExecuteNonQuery();
    }

    public async Task<T?> GetAsync<T>(string key)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = @key";
        cmd.Parameters.AddWithValue("@key", key);

        var result = await cmd.ExecuteScalarAsync();
        if (result is string json)
            return JsonSerializer.Deserialize<T>(json);
        return default;
    }

    public async Task SetAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO settings (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = @value";
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", json);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<AppSettings> LoadSettingsAsync()
    {
        var settings = new AppSettings();
        settings.DefaultPortName = await GetAsync<string>("connection.defaultPortName");
        settings.DefaultBaudRate = await GetAsync<int>("connection.defaultBaudRate") is int br and > 0 ? br : 115200;
        settings.DefaultIpAddress = await GetAsync<string>("connection.defaultIpAddress");
        settings.DefaultPort = await GetAsync<int>("connection.defaultPort") is int p and > 0 ? p : 5000;
        settings.DataStoragePath = await GetAsync<string>("storage.dataPath") ?? "";
        settings.AutoSaveEnabled = await GetAsync<bool?>("storage.autoSave") ?? true;
        settings.ChartRefreshRate = await GetAsync<int>("chart.refreshRate") is int r and > 0 ? r : 30;
        settings.ThemeName = await GetAsync<string>("ui.theme") ?? "Default";
        return settings;
    }

    public async Task SaveSettingsAsync(AppSettings settings)
    {
        await SetAsync("connection.defaultPortName", settings.DefaultPortName);
        await SetAsync("connection.defaultBaudRate", settings.DefaultBaudRate);
        await SetAsync("connection.defaultIpAddress", settings.DefaultIpAddress);
        await SetAsync("connection.defaultPort", settings.DefaultPort);
        await SetAsync("storage.dataPath", settings.DataStoragePath);
        await SetAsync("storage.autoSave", settings.AutoSaveEnabled);
        await SetAsync("chart.refreshRate", settings.ChartRefreshRate);
        await SetAsync("ui.theme", settings.ThemeName);
    }
}
```

### 5.2 DI 注册（App.xaml.cs）

```csharp
// 在 OnStartup 的 DI 注册区域添加
var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "magnetometer.db");
var configService = new AppConfigService(dbPath);
services.AddSingleton<IAppConfigService>(configService);
```

### 5.3 ConnectionViewModel 集成

```csharp
// 构造函数或初始化方法中
public async Task LoadDefaultsAsync(IAppConfigService configService)
{
    var settings = await configService.LoadSettingsAsync();
    if (!string.IsNullOrEmpty(settings.DefaultPortName))
        SelectedPort = settings.DefaultPortName;
    BaudRate = settings.DefaultBaudRate;
    if (!string.IsNullOrEmpty(settings.DefaultIpAddress))
        IpAddress = settings.DefaultIpAddress;
    Port = settings.DefaultPort;
}

// 连接成功后自动保存当前参数
private async Task SaveCurrentConnectionParams()
{
    await _configService.SetAsync("connection.defaultPortName", SelectedPort);
    await _configService.SetAsync("connection.defaultBaudRate", BaudRate);
    // ...
}
```

### 5.4 RealtimeChartViewModel 集成

```csharp
// 初始化时
var refreshRate = await _configService.GetAsync<int>("chart.refreshRate");
if (refreshRate > 0)
    ChartRefreshRate = refreshRate;
```

---

## 六、验收标准

1. 首次启动时 `settings` 表自动创建，`LoadSettingsAsync` 返回全默认值的 `AppSettings`。
2. 调用 `SetAsync("key", value)` 后，`GetAsync<T>("key")` 返回相同值。
3. `SaveSettingsAsync` 后关闭应用，重新启动后 `LoadSettingsAsync` 返回之前保存的值。
4. `ConnectionViewModel` 启动时自动填充上次使用的连接参数。
5. `RealtimeChartViewModel` 启动时自动应用上次的刷新率设置。
6. 数据库文件与 D-1 任务使用同一个 `magnetometer.db`，不冲突。
7. 键不存在时不抛异常，返回类型默认值。

---

## 七、单元测试要求

测试文件：`tests/MagnetometerSystem.Core.Tests/Configuration/AppConfigServiceTests.cs`

| 测试方法 | 说明 |
|---------|------|
| `GetAsync_NonExistentKey_ReturnsDefault` | 键不存在时返回 default |
| `SetAsync_ThenGetAsync_ReturnsValue` | 存后取，值一致 |
| `SetAsync_SameKeyTwice_UpdatesValue` | 同一键写入两次，取到最新值 |
| `SetAsync_ComplexType_RoundTrips` | 存取复杂对象（如 AppSettings）正确序列化/反序列化 |
| `LoadSettingsAsync_EmptyDb_ReturnsDefaults` | 空库返回默认值 |
| `SaveThenLoad_RoundTrips` | SaveSettingsAsync 后 LoadSettingsAsync 值一致 |
| `InitializeTable_CalledTwice_NoException` | 重复初始化不报错（IF NOT EXISTS） |
