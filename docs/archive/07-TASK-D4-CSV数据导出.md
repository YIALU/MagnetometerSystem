# D-4: CSV 数据导出

## 基本信息

| 属性     | 值                                |
| -------- | --------------------------------- |
| 任务编号 | D-4                               |
| 优先级   | P1                                |
| 阶段     | Phase 3 - 数据持久化              |
| 流       | Stream A (Agent 1)                |
| 前置依赖 | D-1 (SQLite 数据库存储服务)       |
| 预估工时 | 3-4 小时                          |

## 功能需求

### 概述
实现将采集会话数据导出为 CSV 文件的功能。定义通用的 `IDataExporter` 接口以支持未来扩展更多导出格式，当前实现 CSV 导出器。导出操作从 `SessionListView` 的右键菜单触发，支持进度报告和取消操作。

### 详细需求

1. **CSV 格式规范**
   - 编码: UTF-8 with BOM (确保 Excel 正确识别中文)
   - 换行符: CRLF (`\r\n`)
   - 头行: `Timestamp,CH0,CH1,...,CHn,TotalField`（通道名称根据会话配置动态生成）
   - 时间戳格式: ISO 8601 (`yyyy-MM-ddTHH:mm:ss.fffffffZ`)
   - 数值精度: 保留全精度（不做舍入）
   - 空值: 输出为空字段 (两个逗号之间无内容)

2. **导出选项**
   - 可选择导出的通道索引（默认全部通道）
   - 可指定时间范围（默认全部数据）
   - 可选是否包含已校准数据标记列
   - 可选是否包含头行

3. **进度报告**
   - 通过 `IProgress<double>` 报告导出进度 (0.0 ~ 1.0)
   - 进度粒度: 每写入 1000 行报告一次
   - 用于 UI 显示进度条

4. **取消支持**
   - 通过 `CancellationToken` 支持取消导出
   - 取消后删除已写入的不完整文件

5. **文件选择**
   - 通过 `SaveFileDialog` 选择导出路径
   - 默认文件名: `{会话名称}_{日期}.csv`
   - 过滤器: `CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*`

6. **大数据量处理**
   - 使用流式写入，不将全部数据加载到内存
   - 分批从数据库读取（每批 10,000 条），边读边写
   - 100 万条数据导出时间 < 10 秒

## 接口契约

### IDataExporter

```csharp
// 文件: src/MagnetometerSystem.Infrastructure/Export/IDataExporter.cs
namespace MagnetometerSystem.Infrastructure.Export;

/// <summary>
/// 数据导出器接口
/// </summary>
public interface IDataExporter
{
    /// <summary>导出格式标识 (如 "CSV", "JSON", "MATLAB")</summary>
    string Format { get; }

    /// <summary>
    /// 将指定会话的数据导出到文件
    /// </summary>
    /// <param name="sessionId">会话 ID</param>
    /// <param name="filePath">输出文件路径</param>
    /// <param name="options">导出选项</param>
    /// <param name="progress">进度报告 (0.0 ~ 1.0)</param>
    /// <param name="ct">取消令牌</param>
    Task ExportAsync(
        string sessionId,
        string filePath,
        ExportOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// 导出选项
/// </summary>
public class ExportOptions
{
    /// <summary>
    /// 要导出的通道索引。null 或空数组表示导出全部通道。
    /// 例如: [0, 2] 表示仅导出第 1 和第 3 通道
    /// </summary>
    public int[]? ChannelIndices { get; set; }

    /// <summary>起始时间（仅导出此时间之后的数据）</summary>
    public DateTime? StartTime { get; set; }

    /// <summary>结束时间（仅导出此时间之前的数据）</summary>
    public DateTime? EndTime { get; set; }

    /// <summary>是否包含校准/正交度状态列</summary>
    public bool IncludeCalibratedData { get; set; }

    /// <summary>是否包含 CSV 头行</summary>
    public bool IncludeHeader { get; set; } = true;
}
```

### CsvExporter

```csharp
// 文件: src/MagnetometerSystem.Infrastructure/Export/CsvExporter.cs
namespace MagnetometerSystem.Infrastructure.Export;

public class CsvExporter : IDataExporter
{
    public string Format => "CSV";

    public CsvExporter(IDataStorageService storageService);

    public Task ExportAsync(
        string sessionId,
        string filePath,
        ExportOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
```

## 文件清单

### 新建文件

| 文件路径 | 说明 |
| -------- | ---- |
| `src/MagnetometerSystem.Infrastructure/Export/IDataExporter.cs` | 导出器接口定义和 `ExportOptions` 类 |
| `src/MagnetometerSystem.Infrastructure/Export/CsvExporter.cs` | CSV 导出器实现 |

### 修改文件

| 文件路径 | 修改内容 |
| -------- | -------- |
| `src/MagnetometerSystem.App/ViewModels/SessionListViewModel.cs` | 实现 `ExportSession` 命令，调用 CsvExporter |
| `src/MagnetometerSystem.App/App.xaml.cs` | 注册 `IDataExporter -> CsvExporter` 到 DI |

## 实现指南

### 1. CsvExporter 核心实现

```csharp
public class CsvExporter : IDataExporter
{
    private readonly IDataStorageService _storageService;
    private const int BatchSize = 10_000;  // 每批从数据库读取的行数

    public string Format => "CSV";

    public CsvExporter(IDataStorageService storageService)
    {
        _storageService = storageService;
    }

    public async Task ExportAsync(
        string sessionId,
        string filePath,
        ExportOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        // 1. 获取会话信息以确定通道配置
        var sessions = await _storageService.GetSessionsAsync();
        var session = sessions.FirstOrDefault(s => s.Id == sessionId)
            ?? throw new ArgumentException($"会话不存在: {sessionId}");

        // 2. 确定要导出的通道
        var channelIndices = options.ChannelIndices?.Length > 0
            ? options.ChannelIndices
            : Enumerable.Range(0, session.ChannelCount).ToArray();
        var channelNames = session.ChannelNames;

        // 3. 获取总数据量（用于进度计算）
        var allReadings = await _storageService.GetReadingsAsync(
            sessionId, options.StartTime, options.EndTime);
        var totalCount = allReadings.Count;

        if (totalCount == 0)
        {
            // 写入空文件（仅头行）
            await WriteEmptyFileAsync(filePath, channelNames, channelIndices, options);
            progress?.Report(1.0);
            return;
        }

        // 4. 流式写入
        try
        {
            using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));

            // 写入头行
            if (options.IncludeHeader)
            {
                var header = BuildHeaderLine(channelNames, channelIndices, options);
                await writer.WriteLineAsync(header);
            }

            // 写入数据行
            var written = 0;
            foreach (var reading in allReadings)
            {
                ct.ThrowIfCancellationRequested();

                var line = BuildDataLine(reading, channelIndices, options);
                await writer.WriteLineAsync(line);

                written++;
                if (written % 1000 == 0)
                {
                    progress?.Report((double)written / totalCount);
                }
            }

            progress?.Report(1.0);
        }
        catch (OperationCanceledException)
        {
            // 取消时删除不完整文件
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            throw;
        }
    }
}
```

### 2. CSV 行构建

```csharp
private static string BuildHeaderLine(
    string[] channelNames, int[] channelIndices, ExportOptions options)
{
    var parts = new List<string> { "Timestamp" };

    foreach (var idx in channelIndices)
    {
        parts.Add(idx < channelNames.Length ? channelNames[idx] : $"CH{idx}");
    }

    parts.Add("TotalField");

    if (options.IncludeCalibratedData)
    {
        parts.Add("IsCalibrated");
        parts.Add("IsOrthoCorrected");
    }

    return string.Join(",", parts);
}

private static string BuildDataLine(
    MagnetometerReading reading, int[] channelIndices, ExportOptions options)
{
    var sb = new StringBuilder();

    // 时间戳 - ISO 8601
    sb.Append(reading.Timestamp.ToString("O"));

    // 通道值
    foreach (var idx in channelIndices)
    {
        sb.Append(',');
        if (idx < reading.ChannelValues.Length)
        {
            sb.Append(reading.ChannelValues[idx].ToString("R"));  // Round-trip 全精度
        }
        // 超出范围则输出空
    }

    // TotalField
    sb.Append(',');
    if (reading.TotalField.HasValue)
    {
        sb.Append(reading.TotalField.Value.ToString("R"));
    }

    // 校准状态列
    if (options.IncludeCalibratedData)
    {
        sb.Append(',');
        sb.Append(reading.IsCalibrated ? '1' : '0');
        sb.Append(',');
        sb.Append(reading.IsOrthogonalityCorrected ? '1' : '0');
    }

    return sb.ToString();
}
```

### 3. 大数据量优化

当前 `IDataStorageService.GetReadingsAsync` 一次返回所有数据。对于超大会话（>100 万条），可能需要分批查询。有两种方案：

**方案 A（当前实现，简单可行）**: 一次性加载到内存后流式写入文件。100 万条 x 8 个 double 约 64 MB 内存，可接受。

**方案 B（未来优化）**: 在 `SqliteStorageService` 中新增分页查询方法:
```csharp
Task<IReadOnlyList<MagnetometerReading>> GetReadingsPagedAsync(
    string sessionId, int offset, int limit,
    DateTime? startTime = null, DateTime? endTime = null);
```
`CsvExporter` 改为分批读取、分批写入。在数据量超过 500 万条时建议实施此优化。

### 4. SessionListViewModel 中的导出触发

```csharp
// 在 SessionListViewModel 中注入 IDataExporter
private readonly IDataExporter _csvExporter;

[RelayCommand]
private async Task ExportSessionAsync(SessionInfo? session)
{
    if (session == null) return;

    var dialog = new Microsoft.Win32.SaveFileDialog
    {
        FileName = $"{session.Name}_{session.StartedAt:yyyyMMdd}.csv",
        DefaultExt = ".csv",
        Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*"
    };

    if (dialog.ShowDialog() != true) return;

    var options = new ExportOptions
    {
        IncludeHeader = true,
        IncludeCalibratedData = false,
    };

    var cts = new CancellationTokenSource();

    // TODO: 显示进度对话框
    var progressHandler = new Progress<double>(p =>
    {
        // 更新 UI 进度条
    });

    try
    {
        await _csvExporter.ExportAsync(
            session.Id, dialog.FileName, options, progressHandler, cts.Token);

        MessageBox.Show($"导出完成: {dialog.FileName}", "导出成功",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
    catch (OperationCanceledException)
    {
        MessageBox.Show("导出已取消", "提示",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
    catch (Exception ex)
    {
        MessageBox.Show($"导出失败: {ex.Message}", "错误",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
```

### 5. DI 注册

```csharp
// App.xaml.cs 中添加:
services.AddSingleton<IDataExporter, CsvExporter>();
```

### 6. 导出结果示例

对于一个三轴磁力仪会话，导出的 CSV 文件内容如下:

```csv
Timestamp,X,Y,Z,TotalField
2026-03-21T10:00:00.0000000Z,25134.56,1023.78,-45678.12,52001.34
2026-03-21T10:00:00.0100000Z,25135.01,1023.45,-45677.89,52001.67
2026-03-21T10:00:00.0200000Z,25134.89,1024.01,-45678.45,52001.52
```

若启用 `IncludeCalibratedData`:

```csv
Timestamp,X,Y,Z,TotalField,IsCalibrated,IsOrthoCorrected
2026-03-21T10:00:00.0000000Z,25134.56,1023.78,-45678.12,52001.34,1,0
```

## 验收标准

| # | 验收项 | 通过条件 |
| - | ------ | -------- |
| 1 | CSV 编码 | 导出文件为 UTF-8 with BOM，前 3 字节为 `EF BB BF` |
| 2 | 头行正确 | 首行包含 `Timestamp` 和对应的通道名称，以逗号分隔 |
| 3 | 时间戳格式 | 时间列为 ISO 8601 格式，精确到亚秒 |
| 4 | 数据完整性 | 导出的行数 = 数据库中会话的 `total_readings` |
| 5 | 通道选择 | 指定 `ChannelIndices = [0, 2]` 时，仅导出第 1 和第 3 通道 |
| 6 | 时间范围 | 指定 StartTime/EndTime 后，仅导出范围内的数据 |
| 7 | 进度报告 | 导出过程中 `IProgress<double>` 回调被调用，值从 0 递增到 1 |
| 8 | 取消操作 | 调用 `CancellationToken.Cancel()` 后导出中断，不完整文件被删除 |
| 9 | 大数据导出 | 100 万条数据导出耗时 < 10 秒 |
| 10 | Excel 兼容 | 导出的 CSV 文件可被 Excel 正确打开，中文不乱码 |
| 11 | UI 触发 | 在会话列表右键菜单点击"导出 CSV"可弹出文件保存对话框并完成导出 |
| 12 | 空会话 | 对无数据的会话导出，生成仅包含头行的空文件 |

## 单元测试要求

测试项目: `tests/MagnetometerSystem.Infrastructure.Tests/`

### 测试类: `CsvExporterTests`

使用 Mock 的 `IDataStorageService` 和临时文件路径进行测试。

| 测试方法 | 验证内容 |
| -------- | -------- |
| `Export_WritesUtf8WithBom` | 导出文件前 3 字节为 BOM (`EF BB BF`) |
| `Export_WritesCorrectHeader_TriaxialSensor` | 三轴传感器导出头行: `Timestamp,X,Y,Z,TotalField` |
| `Export_WritesCorrectHeader_SingleAxis` | 单轴传感器导出头行: `Timestamp,B,TotalField` |
| `Export_NoHeader_WhenOptionDisabled` | `IncludeHeader = false` 时首行即为数据行 |
| `Export_FilterByChannelIndices` | 指定通道索引后，仅包含指定通道的数据列 |
| `Export_FilterByTimeRange` | Mock 返回 10 条数据，指定时间范围后仅输出范围内的行 |
| `Export_IncludesCalibrationColumns` | `IncludeCalibratedData = true` 时包含 IsCalibrated 和 IsOrthoCorrected 列 |
| `Export_CorrectLineCount` | 导出 1000 条数据后，文件行数 = 1001 (头行 + 1000 数据行) |
| `Export_ProgressReported` | 导出 5000 条数据，progress 回调至少被调用 4 次 (每 1000 行) |
| `Export_CancellationDeletesFile` | 导出过程中取消，验证临时文件已被删除 |
| `Export_EmptySession_WritesHeaderOnly` | 空会话导出仅包含头行，文件大小 > 0 |
| `Export_TimestampFormat_IsIso8601` | 解析导出文件中的时间戳列，验证符合 ISO 8601 格式 |
| `Export_NullTotalField_WritesEmpty` | `TotalField = null` 时对应列为空 (两个逗号之间无内容) |
| `Export_FullPrecision` | 导出的浮点数可无损还原为原始 double 值 |
