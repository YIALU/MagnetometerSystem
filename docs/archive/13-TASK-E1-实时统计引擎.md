# TASK-E1: 实时统计引擎（RollingStatisticsEngine）

**文档版本**: v1.0
**更新日期**: 2026-03-21
**优先级**: P1
**阶段**: Phase 4
**流**: Stream C / Stream D
**依赖**: 无

---

## 一、基本信息

### 背景

当前 `RealtimeChartViewModel.UpdateStatistics()` 方法内联实现了统计计算逻辑，直接依赖 ViewModel 内部的数据缓冲区。这导致统计功能无法在批处理、回放、导出等场景中复用。

### 目标

将窗口化统计计算抽取为独立引擎 `RollingStatisticsEngine`，自行管理各通道的循环缓冲区，按 `StatisticsConfig.WindowSeconds` 裁剪旧数据，调用现有 `StatisticsResultItem.Compute()` 完成计算。

---

## 二、功能需求

1. **多通道缓冲管理** — 按通道索引维护独立的循环缓冲区，支持动态通道数（构造时指定 `maxChannels`）。
2. **时间窗口裁剪** — 每次 `AddSample` 时记录时间戳，`ComputeAll` 时根据 `WindowSeconds` 丢弃超出窗口的旧样本。
3. **统计计算委托** — 内部调用 `StatisticsResultItem.Compute(name, data)` 获取 Mean、StdDev、PeakToPeak、Rms、Min、Max。
4. **配置热更新** — `Configure(StatisticsConfig)` 可在运行期间随时调用，立即生效。
5. **清空重置** — `Clear()` 清空全部通道缓冲区，用于采集重新开始等场景。
6. **线程安全** — `AddSample` 可能在数据接收线程调用，`ComputeAll` 在 UI 定时器调用，需加锁保护。

---

## 三、接口契约

### 类定义

```csharp
namespace MagnetometerSystem.Core.Processing;

public class RollingStatisticsEngine
{
    /// <summary>
    /// 创建统计引擎实例。
    /// </summary>
    /// <param name="maxChannels">支持的最大通道数。</param>
    public RollingStatisticsEngine(int maxChannels);

    /// <summary>
    /// 更新统计配置（窗口大小、显示项等）。可在运行期间多次调用。
    /// </summary>
    public void Configure(StatisticsConfig config);

    /// <summary>
    /// 添加一组采样值。channelValues.Length 必须 &lt;= maxChannels。
    /// </summary>
    /// <param name="timestamp">采样时间戳（秒）。</param>
    /// <param name="channelValues">各通道的当前值。</param>
    public void AddSample(double timestamp, double[] channelValues);

    /// <summary>
    /// 对所有活跃通道计算窗口化统计量。
    /// </summary>
    /// <param name="channelNames">各通道名称，与 AddSample 的索引对应。</param>
    /// <returns>每个通道的 StatisticsResultItem。</returns>
    public StatisticsResultItem[] ComputeAll(string[] channelNames);

    /// <summary>
    /// 清空所有通道的缓冲区数据。
    /// </summary>
    public void Clear();
}
```

### 内部结构

```
RollingStatisticsEngine
├── _buffers: (double timestamp, double value)[][] — 每通道一个时间戳-值对环形缓冲区
├── _config: StatisticsConfig — 当前配置
├── _lock: object — 同步锁
└── _maxChannels: int
```

### 依赖的现有类型

| 类型 | 所在位置 | 用途 |
|------|---------|------|
| `StatisticsResultItem` | `Core/Models/StatisticsResultItem.cs` | 静态方法 `Compute(name, data)` |
| `StatisticsConfig` | `Core/Models/StatisticsConfig.cs` | 窗口大小及显示开关 |
| `CircularBuffer<T>` | `Core/Helpers/CircularBuffer.cs` | 可选复用，或自行用数组实现 |

---

## 四、文件清单

| 操作 | 文件路径 | 说明 |
|------|---------|------|
| 新建 | `Core/Processing/RollingStatisticsEngine.cs` | 统计引擎主类 |
| 可选修改 | `App/ViewModels/RealtimeChartViewModel.cs` | 将 `UpdateStatistics` 委托给引擎（非必须） |

---

## 五、实现指南

### 5.1 缓冲区设计

- 每个通道维护一个 `List<(double Timestamp, double Value)>` 或复用 `CircularBuffer<(double, double)>`。
- `AddSample` 将 `(timestamp, channelValues[i])` 追加到第 `i` 个通道的缓冲区。
- 缓冲区最大容量建议 100,000（与现有 `CircularBuffer` 一致），超出时自动丢弃最旧数据。

### 5.2 窗口裁剪

```
ComputeAll 流程:
1. 获取锁
2. 计算 cutoffTime = 最新时间戳 - config.WindowSeconds
3. 对每个通道：
   a. 找到第一个 timestamp >= cutoffTime 的索引
   b. 提取该索引之后的 value 数组 → ReadOnlySpan<double>
   c. 调用 StatisticsResultItem.Compute(channelNames[i], span)
4. 释放锁
5. 返回结果数组
```

### 5.3 线程安全

- 使用 `lock(_lock)` 保护 `AddSample`、`ComputeAll`、`Clear`。
- 锁粒度：整个操作加锁，避免在计算过程中缓冲区被修改。

### 5.4 可选集成

如需替换 `RealtimeChartViewModel.UpdateStatistics()`：
```csharp
// 在 RealtimeChartViewModel 中
private readonly RollingStatisticsEngine _statsEngine = new(32);

// DataBus.ReadingReceived 回调中：
_statsEngine.AddSample(reading.Timestamp, reading.ChannelValues);

// 定时器回调中：
var results = _statsEngine.ComputeAll(channelNames);
StatisticsItems = new ObservableCollection<StatisticsResultItem>(results);
```

---

## 六、验收标准

1. `RollingStatisticsEngine` 可独立实例化，不依赖任何 ViewModel 或 UI 组件。
2. 添加 10,000 个样本后 `ComputeAll` 返回正确的 Mean、StdDev、PeakToPeak、Rms、Min、Max。
3. 设置 `WindowSeconds = 5`，添加 10 秒数据后，统计结果仅反映最近 5 秒的数据。
4. `Clear()` 后 `ComputeAll` 返回空数组或全零结果。
5. 多线程并发调用 `AddSample` 和 `ComputeAll` 不抛出异常。
6. Core 项目编译通过，无新增对 CommunityToolkit 的依赖。

---

## 七、单元测试要求

测试文件：`tests/MagnetometerSystem.Core.Tests/Processing/RollingStatisticsEngineTests.cs`

| 测试方法 | 说明 |
|---------|------|
| `ComputeAll_NoData_ReturnsEmpty` | 无数据时返回空数组 |
| `ComputeAll_SingleChannel_CorrectMean` | 单通道添加已知数据，验证均值 |
| `ComputeAll_MultiChannel_IndependentResults` | 多通道数据互不干扰 |
| `ComputeAll_WindowTrimming_OnlyRecentData` | 设置 WindowSeconds=2，添加 5 秒数据，仅最近 2 秒参与计算 |
| `Configure_UpdatesWindowSize` | 运行中更改 WindowSeconds，下次计算使用新窗口 |
| `Clear_ResetsAllBuffers` | Clear 后 ComputeAll 返回空结果 |
| `AddSample_ExceedsCapacity_NoException` | 超过缓冲区容量后不抛异常，旧数据被丢弃 |
| `ConcurrentAccess_NoException` | 多线程并发 AddSample + ComputeAll 不崩溃 |
