# TASK C-5：校正配置管理

**文档版本**: v1.0
**编写日期**: 2026-03-21
**优先级**: P1
**所属阶段**: Phase 4
**任务流**: Stream C（正交度校正）
**前置依赖**: D-1（SQLite 数据库服务）

---

## 一、基本信息

### 1.1 任务概述

实现正交度校正和通用校准参数的持久化管理，包括 `ICalibrationRepository` 接口定义及其 SQLite 实现，以及 JSON 格式的配置导入/导出功能。本任务为整个校正模块提供数据持久化基础设施，C-3 向导的保存功能、C-2 校正应用的配置加载均依赖本任务。

### 1.2 核心职责

| 职责 | 说明 |
|------|------|
| CRUD 操作 | 正交度配置和通用校准配置的增删改查 |
| 数据库存储 | 通过 SQLite + Dapper 实现持久化 |
| 按条件查询 | 按传感器序列号、传感器类型筛选 |
| 默认配置管理 | 支持设置和获取默认校正配置 |
| JSON 导入/导出 | 将配置序列化为 JSON 文件，或从 JSON 文件导入 |

### 1.3 涉及的现有代码

| 文件 | 作用 |
|------|------|
| `Core/Models/OrthogonalityParams.cs` | 正交度参数模型 |
| `Core/Models/CalibrationParams.cs` | 通用校准参数模型 |
| `Core/Models/SensorType.cs` | 传感器类型枚举 |
| `Infrastructure/Database/DatabaseInitializer.cs` | 数据库初始化，建表逻辑 |

### 1.4 数据库表

本任务依赖的数据库表已在项目规划中定义（见第五节），由 D-1 任务负责建表。若 D-1 尚未完成，本任务的 `DatabaseInitializer` 中需包含建表逻辑。

---

## 二、功能需求

### 2.1 正交度配置 CRUD

| 操作 | 方法 | 说明 |
|------|------|------|
| 保存 | `SaveOrthogonalityProfileAsync` | 插入或更新（按 Id） |
| 查询列表 | `GetOrthogonalityProfilesAsync` | 可按 sensorSerial 筛选 |
| 查询单个 | `GetOrthogonalityProfileAsync` | 按 Id 查询 |
| 删除 | `DeleteOrthogonalityProfileAsync` | 按 Id 删除 |

### 2.2 通用校准配置 CRUD

| 操作 | 方法 | 说明 |
|------|------|------|
| 保存 | `SaveCalibrationProfileAsync` | 插入或更新 |
| 查询列表 | `GetCalibrationProfilesAsync` | 可按 sensorType 筛选 |
| 删除 | `DeleteCalibrationProfileAsync` | 按 Id 删除 |

### 2.3 默认配置管理

通过 `settings` 表存储默认配置 ID：

| Key | Value | 说明 |
|-----|-------|------|
| `default_orthogonality_profile_{sensorSerial}` | profile_id | 特定传感器的默认正交度配置 |
| `default_calibration_profile_{sensorType}` | profile_id | 特定传感器类型的默认校准配置 |

### 2.4 JSON 导入/导出

#### 2.4.1 导出格式

```json
{
    "type": "orthogonality_profile",
    "version": "1.0",
    "exported_at": "2026-03-21T10:30:00",
    "profile": {
        "id": "guid-here",
        "name": "传感器A正交度校正",
        "sensor_serial": "SN-2024-001",
        "created_at": "2026-03-20T14:25:00",
        "offset": [120.5, -45.3, 78.1],
        "compensation_matrix": [1.0002, -0.0021, 0.0015, 0.0019, 0.9998, -0.0030, -0.0009, 0.0026, 1.0001],
        "residual_mean": 2.35,
        "residual_std": 8.72,
        "sample_count": 1500,
        "notes": "实验室环境校正"
    }
}
```

#### 2.4.2 导入逻辑

1. 读取 JSON 文件并反序列化
2. 校验 `type` 和 `version` 字段
3. 生成新 Id（避免冲突），保留原始创建时间
4. 调用 `SaveOrthogonalityProfileAsync` 保存
5. 返回导入结果（成功/失败+原因）

### 2.5 配置管理 UI（可选独立页面或嵌入向导）

#### 2.5.1 UI 元素

| 控件 | 类型 | 说明 |
|------|------|------|
| 配置列表 | ListView/DataGrid | 显示所有正交度配置，列：名称、传感器序列号、创建时间、残差标准差 |
| 加载配置 | Button | 将选中配置设为当前活动配置 |
| 删除配置 | Button | 删除选中配置，需确认对话框 |
| 设为默认 | Button | 将选中配置设为该传感器的默认配置 |
| 导出 | Button | 导出选中配置为 JSON 文件（SaveFileDialog） |
| 导入 | Button | 从 JSON 文件导入配置（OpenFileDialog） |
| 筛选 | TextBox/ComboBox | 按传感器序列号筛选 |

---

## 三、接口契约

### 3.1 新增接口 — `ICalibrationRepository`

```csharp
// 文件: Core/Calibration/ICalibrationRepository.cs
namespace MagnetometerSystem.Core.Calibration;

using MagnetometerSystem.Core.Models;

/// <summary>
/// 校正/校准配置持久化仓库接口
/// </summary>
public interface ICalibrationRepository
{
    // === 正交度配置 ===

    /// <summary>保存正交度配置（插入或更新）</summary>
    Task SaveOrthogonalityProfileAsync(OrthogonalityParams profile);

    /// <summary>查询正交度配置列表，可按传感器序列号筛选</summary>
    Task<IReadOnlyList<OrthogonalityParams>> GetOrthogonalityProfilesAsync(
        string? sensorSerial = null);

    /// <summary>按 Id 查询单个正交度配置</summary>
    Task<OrthogonalityParams?> GetOrthogonalityProfileAsync(string id);

    /// <summary>按 Id 删除正交度配置</summary>
    Task DeleteOrthogonalityProfileAsync(string id);

    // === 通用校准配置 ===

    /// <summary>保存校准配置</summary>
    Task SaveCalibrationProfileAsync(CalibrationParams profile);

    /// <summary>查询校准配置列表，可按传感器类型筛选</summary>
    Task<IReadOnlyList<CalibrationParams>> GetCalibrationProfilesAsync(
        SensorType? sensorType = null);

    /// <summary>按 Id 删除校准配置</summary>
    Task DeleteCalibrationProfileAsync(string id);
}
```

### 3.2 导入/导出辅助接口

```csharp
// 可在 ICalibrationRepository 中扩展，或作为独立服务
public interface ICalibrationExportService
{
    /// <summary>导出正交度配置为 JSON 字符串</summary>
    string ExportOrthogonalityProfile(OrthogonalityParams profile);

    /// <summary>从 JSON 字符串导入正交度配置</summary>
    ImportResult<OrthogonalityParams> ImportOrthogonalityProfile(string json);

    /// <summary>导出校准配置为 JSON 字符串</summary>
    string ExportCalibrationProfile(CalibrationParams profile);

    /// <summary>从 JSON 字符串导入校准配置</summary>
    ImportResult<CalibrationParams> ImportCalibrationProfile(string json);
}

public class ImportResult<T>
{
    public bool Success { get; set; }
    public T? Profile { get; set; }
    public string? ErrorMessage { get; set; }
}
```

---

## 四、文件清单

### 4.1 新建文件

| 文件路径 | 说明 |
|----------|------|
| `src/MagnetometerSystem.Core/Calibration/ICalibrationRepository.cs` | 仓库接口定义 |
| `src/MagnetometerSystem.Core/Calibration/ICalibrationExportService.cs` | 导入/导出服务接口 |
| `src/MagnetometerSystem.Infrastructure/Database/SqliteCalibrationRepository.cs` | SQLite 实现 |
| `src/MagnetometerSystem.Infrastructure/Export/CalibrationExportService.cs` | JSON 导入/导出实现 |

### 4.2 修改文件

| 文件路径 | 修改说明 |
|----------|----------|
| `src/MagnetometerSystem.Infrastructure/Database/DatabaseInitializer.cs` | 确保 `orthogonality_profiles` 和 `calibration_profiles` 表已建（若 D-1 未含） |
| `src/MagnetometerSystem.App/App.xaml.cs` | DI 注册 `ICalibrationRepository` → `SqliteCalibrationRepository`，`ICalibrationExportService` → `CalibrationExportService` |
| `src/MagnetometerSystem.App/Views/OrthogonalityCalibrationView.xaml` | 添加配置管理区域（或新建独立页面） |
| `src/MagnetometerSystem.App/ViewModels/OrthogonalityCalibrationViewModel.cs` | 添加配置列表管理功能 |

### 4.3 DI 注册

```csharp
services.AddSingleton<ICalibrationRepository, SqliteCalibrationRepository>();
services.AddSingleton<ICalibrationExportService, CalibrationExportService>();
```

---

## 五、数据库变更

### 5.1 依赖表结构

以下表结构由 D-1 任务建表。若 D-1 尚未完成，本任务需在 `DatabaseInitializer` 中自行创建。

```sql
-- 正交度校正配置表
CREATE TABLE IF NOT EXISTS orthogonality_profiles (
    id              TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    sensor_serial   TEXT,
    created_at      TEXT NOT NULL,
    offset_x        REAL NOT NULL,
    offset_y        REAL NOT NULL,
    offset_z        REAL NOT NULL,
    m00 REAL NOT NULL, m01 REAL NOT NULL, m02 REAL NOT NULL,
    m10 REAL NOT NULL, m11 REAL NOT NULL, m12 REAL NOT NULL,
    m20 REAL NOT NULL, m21 REAL NOT NULL, m22 REAL NOT NULL,
    residual_mean   REAL,
    residual_std    REAL,
    sample_count    INTEGER,
    notes           TEXT
);

-- 通用传感器校准配置表
CREATE TABLE IF NOT EXISTS calibration_profiles (
    id              TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    sensor_type     TEXT NOT NULL,
    sensor_serial   TEXT,
    created_at      TEXT NOT NULL,
    offset_values   TEXT NOT NULL,   -- JSON 数组
    gain_values     TEXT NOT NULL,   -- JSON 数组
    notes           TEXT
);
```

### 5.2 数据映射

#### 5.2.1 `OrthogonalityParams` ↔ `orthogonality_profiles`

| 模型属性 | 数据库列 | 映射说明 |
|----------|----------|----------|
| `Id` | `id` | 直接映射 |
| `Name` | `name` | 直接映射 |
| `SensorSerial` | `sensor_serial` | 直接映射，可为 null |
| `CreatedAt` | `created_at` | DateTime → ISO 8601 字符串 |
| `Offset[0]` | `offset_x` | 数组第 0 个元素 |
| `Offset[1]` | `offset_y` | 数组第 1 个元素 |
| `Offset[2]` | `offset_z` | 数组第 2 个元素 |
| `CompensationMatrix[0..8]` | `m00..m22` | 行优先展开为 9 列 |
| `ResidualMean` | `residual_mean` | 直接映射 |
| `ResidualStd` | `residual_std` | 直接映射 |
| `SampleCount` | `sample_count` | 直接映射 |
| `Notes` | `notes` | 直接映射 |

#### 5.2.2 `CalibrationParams` ↔ `calibration_profiles`

| 模型属性 | 数据库列 | 映射说明 |
|----------|----------|----------|
| `Id` | `id` | 直接映射 |
| `Name` | `name` | 直接映射 |
| `SensorType` | `sensor_type` | 枚举名称字符串 |
| `SensorSerial` | `sensor_serial` | 直接映射 |
| `CreatedAt` | `created_at` | DateTime → ISO 8601 字符串 |
| `OffsetValues` | `offset_values` | `double[]` → JSON 数组字符串 |
| `GainValues` | `gain_values` | `double[]` → JSON 数组字符串 |
| `Notes` | `notes` | 直接映射 |

---

## 六、实现指南

### 6.1 `SqliteCalibrationRepository` 类结构

```csharp
// 文件: Infrastructure/Database/SqliteCalibrationRepository.cs
namespace MagnetometerSystem.Infrastructure.Database;

using System.Data;
using System.Text.Json;
using Dapper;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;

public class SqliteCalibrationRepository : ICalibrationRepository
{
    private readonly IDbConnection _connection; // 或连接工厂

    public SqliteCalibrationRepository(IDbConnection connection)
    {
        _connection = connection;
    }

    // === 正交度配置 ===

    public async Task SaveOrthogonalityProfileAsync(OrthogonalityParams profile)
    {
        const string sql = @"
            INSERT OR REPLACE INTO orthogonality_profiles
            (id, name, sensor_serial, created_at,
             offset_x, offset_y, offset_z,
             m00, m01, m02, m10, m11, m12, m20, m21, m22,
             residual_mean, residual_std, sample_count, notes)
            VALUES
            (@Id, @Name, @SensorSerial, @CreatedAt,
             @OffsetX, @OffsetY, @OffsetZ,
             @M00, @M01, @M02, @M10, @M11, @M12, @M20, @M21, @M22,
             @ResidualMean, @ResidualStd, @SampleCount, @Notes)";

        await _connection.ExecuteAsync(sql, new
        {
            profile.Id,
            profile.Name,
            profile.SensorSerial,
            CreatedAt = profile.CreatedAt.ToString("O"),
            OffsetX = profile.Offset[0],
            OffsetY = profile.Offset[1],
            OffsetZ = profile.Offset[2],
            M00 = profile.CompensationMatrix[0],
            M01 = profile.CompensationMatrix[1],
            M02 = profile.CompensationMatrix[2],
            M10 = profile.CompensationMatrix[3],
            M11 = profile.CompensationMatrix[4],
            M12 = profile.CompensationMatrix[5],
            M20 = profile.CompensationMatrix[6],
            M21 = profile.CompensationMatrix[7],
            M22 = profile.CompensationMatrix[8],
            profile.ResidualMean,
            profile.ResidualStd,
            profile.SampleCount,
            profile.Notes
        });
    }

    public async Task<IReadOnlyList<OrthogonalityParams>> GetOrthogonalityProfilesAsync(
        string? sensorSerial = null)
    {
        string sql = "SELECT * FROM orthogonality_profiles";
        if (sensorSerial != null)
            sql += " WHERE sensor_serial = @SensorSerial";
        sql += " ORDER BY created_at DESC";

        var rows = await _connection.QueryAsync(sql, new { SensorSerial = sensorSerial });
        return rows.Select(MapToOrthogonalityParams).ToList().AsReadOnly();
    }

    public async Task<OrthogonalityParams?> GetOrthogonalityProfileAsync(string id)
    {
        const string sql = "SELECT * FROM orthogonality_profiles WHERE id = @Id";
        var row = await _connection.QueryFirstOrDefaultAsync(sql, new { Id = id });
        return row != null ? MapToOrthogonalityParams(row) : null;
    }

    public async Task DeleteOrthogonalityProfileAsync(string id)
    {
        const string sql = "DELETE FROM orthogonality_profiles WHERE id = @Id";
        await _connection.ExecuteAsync(sql, new { Id = id });
    }

    // === 通用校准配置 ===

    public async Task SaveCalibrationProfileAsync(CalibrationParams profile)
    {
        const string sql = @"
            INSERT OR REPLACE INTO calibration_profiles
            (id, name, sensor_type, sensor_serial, created_at,
             offset_values, gain_values, notes)
            VALUES
            (@Id, @Name, @SensorType, @SensorSerial, @CreatedAt,
             @OffsetValues, @GainValues, @Notes)";

        await _connection.ExecuteAsync(sql, new
        {
            profile.Id,
            profile.Name,
            SensorType = profile.SensorType.ToString(),
            profile.SensorSerial,
            CreatedAt = profile.CreatedAt.ToString("O"),
            OffsetValues = JsonSerializer.Serialize(profile.OffsetValues),
            GainValues = JsonSerializer.Serialize(profile.GainValues),
            profile.Notes
        });
    }

    public async Task<IReadOnlyList<CalibrationParams>> GetCalibrationProfilesAsync(
        SensorType? sensorType = null)
    {
        string sql = "SELECT * FROM calibration_profiles";
        if (sensorType != null)
            sql += " WHERE sensor_type = @SensorType";
        sql += " ORDER BY created_at DESC";

        var rows = await _connection.QueryAsync(sql,
            new { SensorType = sensorType?.ToString() });
        return rows.Select(MapToCalibrationParams).ToList().AsReadOnly();
    }

    public async Task DeleteCalibrationProfileAsync(string id)
    {
        const string sql = "DELETE FROM calibration_profiles WHERE id = @Id";
        await _connection.ExecuteAsync(sql, new { Id = id });
    }

    // === 私有映射方法 ===

    private static OrthogonalityParams MapToOrthogonalityParams(dynamic row)
    {
        return new OrthogonalityParams
        {
            Id = row.id,
            Name = row.name,
            SensorSerial = row.sensor_serial,
            CreatedAt = DateTime.Parse(row.created_at),
            Offset = [(double)row.offset_x, (double)row.offset_y, (double)row.offset_z],
            CompensationMatrix = [
                (double)row.m00, (double)row.m01, (double)row.m02,
                (double)row.m10, (double)row.m11, (double)row.m12,
                (double)row.m20, (double)row.m21, (double)row.m22
            ],
            ResidualMean = row.residual_mean,
            ResidualStd = row.residual_std,
            SampleCount = (int?)row.sample_count,
            Notes = row.notes
        };
    }

    private static CalibrationParams MapToCalibrationParams(dynamic row)
    {
        return new CalibrationParams
        {
            Id = row.id,
            Name = row.name,
            SensorType = Enum.Parse<SensorType>(row.sensor_type),
            SensorSerial = row.sensor_serial,
            CreatedAt = DateTime.Parse(row.created_at),
            OffsetValues = JsonSerializer.Deserialize<double[]>(row.offset_values) ?? [],
            GainValues = JsonSerializer.Deserialize<double[]>(row.gain_values) ?? [],
            Notes = row.notes
        };
    }
}
```

### 6.2 JSON 导入/导出实现

```csharp
// 文件: Infrastructure/Export/CalibrationExportService.cs
namespace MagnetometerSystem.Infrastructure.Export;

using System.Text.Json;
using System.Text.Json.Serialization;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;

public class CalibrationExportService : ICalibrationExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ExportOrthogonalityProfile(OrthogonalityParams profile)
    {
        var envelope = new
        {
            Type = "orthogonality_profile",
            Version = "1.0",
            ExportedAt = DateTime.Now,
            Profile = profile
        };
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    public ImportResult<OrthogonalityParams> ImportOrthogonalityProfile(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // 验证类型和版本
            if (root.GetProperty("type").GetString() != "orthogonality_profile")
                return new ImportResult<OrthogonalityParams>
                    { Success = false, ErrorMessage = "文件类型不匹配" };

            var profileElement = root.GetProperty("profile");
            var profile = JsonSerializer.Deserialize<OrthogonalityParams>(
                profileElement.GetRawText(), JsonOptions);

            if (profile == null)
                return new ImportResult<OrthogonalityParams>
                    { Success = false, ErrorMessage = "配置数据解析失败" };

            // 生成新 Id 避免冲突
            profile.Id = Guid.NewGuid().ToString();

            return new ImportResult<OrthogonalityParams>
                { Success = true, Profile = profile };
        }
        catch (Exception ex)
        {
            return new ImportResult<OrthogonalityParams>
                { Success = false, ErrorMessage = $"JSON 解析失败: {ex.Message}" };
        }
    }

    // CalibrationParams 的导入导出同理
    public string ExportCalibrationProfile(CalibrationParams profile) { ... }
    public ImportResult<CalibrationParams> ImportCalibrationProfile(string json) { ... }
}
```

### 6.3 Dapper 使用注意事项

- 使用 `INSERT OR REPLACE` 实现 upsert 语义
- DateTime 存储为 ISO 8601 格式字符串
- `double[]` 数组通过 `System.Text.Json` 序列化为 JSON 字符串存储
- 枚举通过 `.ToString()` 存储为名称字符串，读取时用 `Enum.Parse<T>()` 还原
- 连接管理：复用 D-1 提供的连接工厂或单例连接

---

## 七、验收标准

### 7.1 功能验收

| 编号 | 验收项 | 通过条件 |
|------|--------|----------|
| AC-1 | 保存正交度配置 | 调用 `SaveOrthogonalityProfileAsync` 后数据库中可查到 |
| AC-2 | 查询正交度配置列表 | 无筛选时返回全部，按序列号筛选时只返回匹配项 |
| AC-3 | 查询单个正交度配置 | 按 Id 查询返回正确的完整对象 |
| AC-4 | 删除正交度配置 | 删除后查询返回 null |
| AC-5 | 保存校准配置 | 数组字段正确序列化为 JSON 字符串存储 |
| AC-6 | 查询校准配置 | 反序列化正确还原 `OffsetValues` 和 `GainValues` 数组 |
| AC-7 | 删除校准配置 | 删除后列表中不再包含该项 |
| AC-8 | 更新操作 | 同 Id 再次保存覆盖旧数据 |
| AC-9 | JSON 导出 | 导出的 JSON 包含完整配置数据和元数据 |
| AC-10 | JSON 导入 | 从合法 JSON 导入后可在列表中查到（新 Id） |
| AC-11 | 导入错误处理 | 非法 JSON 或类型不匹配时返回失败结果 |
| AC-12 | 补偿矩阵精度 | 保存再读取后 9 个矩阵元素无精度丢失（double 完整存储） |
| AC-13 | 偏移向量精度 | 保存再读取后 3 个偏移分量无精度丢失 |

### 7.2 非功能验收

| 编号 | 验收项 | 通过条件 |
|------|--------|----------|
| NF-1 | 并发安全 | 多个异步操作不导致数据库锁死 |
| NF-2 | 大量配置 | 100 个配置的列表查询 < 100ms |
| NF-3 | 接口合规 | 实现 `ICalibrationRepository` 全部方法 |

---

## 八、单元测试要求

测试文件：`tests/MagnetometerSystem.Core.Tests/Infrastructure/SqliteCalibrationRepositoryTests.cs`

### 8.1 测试用例清单

使用内存 SQLite 数据库（`:memory:`）进行测试，每个测试方法独立建表。

| 编号 | 测试名称 | 测试内容 |
|------|----------|----------|
| T-01 | `SaveAndGet_OrthogonalityProfile_RoundTrip` | 保存后按 Id 查询，所有字段一致 |
| T-02 | `SaveAndGet_CompensationMatrix_NoPrecisionLoss` | 补偿矩阵 9 个元素保存后读取无精度丢失 |
| T-03 | `SaveAndGet_OffsetVector_NoPrecisionLoss` | 偏移向量 3 个分量保存后读取无精度丢失 |
| T-04 | `GetProfiles_FilterBySensorSerial` | 按序列号筛选返回正确子集 |
| T-05 | `GetProfiles_NoFilter_ReturnsAll` | 无筛选返回全部配置 |
| T-06 | `GetProfiles_OrderByCreatedAtDesc` | 结果按创建时间降序排列 |
| T-07 | `DeleteProfile_RemovesFromDB` | 删除后查询返回 null |
| T-08 | `SaveProfile_SameId_Updates` | 同 Id 二次保存覆盖旧值 |
| T-09 | `SaveAndGet_CalibrationProfile_RoundTrip` | 校准配置完整读写循环 |
| T-10 | `CalibrationProfile_ArraysSerialized` | OffsetValues/GainValues 正确序列化反序列化 |
| T-11 | `GetCalibrationProfiles_FilterBySensorType` | 按传感器类型筛选 |
| T-12 | `Export_OrthogonalityProfile_ValidJson` | 导出的 JSON 可被解析 |
| T-13 | `Import_OrthogonalityProfile_Success` | 合法 JSON 成功导入 |
| T-14 | `Import_WrongType_Fails` | 类型字段不匹配时导入失败 |
| T-15 | `Import_InvalidJson_Fails` | 非法 JSON 字符串导入失败，不抛异常 |
| T-16 | `Import_GeneratesNewId` | 导入后的配置 Id 与原文件中不同 |

### 8.2 测试基础设施

```csharp
// 测试基类或辅助方法
private static IDbConnection CreateInMemoryDatabase()
{
    var connection = new SqliteConnection("Data Source=:memory:");
    connection.Open();

    // 建表
    connection.Execute(@"
        CREATE TABLE orthogonality_profiles ( ... );
        CREATE TABLE calibration_profiles ( ... );
        CREATE TABLE settings ( ... );
    ");

    return connection;
}

private static OrthogonalityParams CreateTestProfile()
{
    return new OrthogonalityParams
    {
        Id = Guid.NewGuid().ToString(),
        Name = "测试配置",
        SensorSerial = "SN-TEST-001",
        CreatedAt = DateTime.Now,
        Offset = [100.5, -200.3, 50.7],
        CompensationMatrix = [1.0002, -0.0021, 0.0015,
                              0.0019, 0.9998, -0.0030,
                             -0.0009, 0.0026, 1.0001],
        ResidualMean = 2.35,
        ResidualStd = 8.72,
        SampleCount = 1500,
        Notes = "单元测试配置"
    };
}
```
