# TASK-E2: 数字滤波（DataProcessor）

**文档版本**: v1.0
**更新日期**: 2026-03-21
**优先级**: P1
**阶段**: Phase 4
**流**: Stream C
**依赖**: 无硬依赖

---

## 一、基本信息

### 背景

磁力计原始数据常含高频噪声，需要提供基本的数字滤波能力。当前系统缺少滤波处理模块。

### 目标

实现移动平均和中值滤波两种基础滤波算法，通过 `IDataProcessor` 接口暴露，并在实时图表界面中提供滤波开关与参数配置，将滤波结果以虚线叠加显示在原始信号上。

---

## 二、功能需求

1. **移动平均滤波** — 滑动窗口求平均值，平滑高频噪声。
2. **中值滤波** — 滑动窗口取中值，有效去除脉冲噪声。
3. **边界处理** — 窗口不足时使用可用数据计算（缩窗处理）。
4. **UI 控制** — 提供启用/禁用开关、滤波类型选择、窗口大小输入。
5. **叠加显示** — 滤波后数据在同一图表上以虚线样式绘制，与原始信号区分。

---

## 三、接口契约

### IDataProcessor 接口

```csharp
namespace MagnetometerSystem.Core.Processing;

/// <summary>
/// 数据处理器接口，提供基础滤波算法。
/// </summary>
public interface IDataProcessor
{
    /// <summary>
    /// 移动平均滤波。
    /// </summary>
    /// <param name="data">原始数据数组。</param>
    /// <param name="windowSize">窗口大小，必须为正奇数或正整数。</param>
    /// <returns>滤波后的数据数组，长度与输入相同。</returns>
    double[] MovingAverage(double[] data, int windowSize);

    /// <summary>
    /// 中值滤波。
    /// </summary>
    /// <param name="data">原始数据数组。</param>
    /// <param name="windowSize">窗口大小，必须为正奇数。</param>
    /// <returns>滤波后的数据数组，长度与输入相同。</returns>
    double[] MedianFilter(double[] data, int windowSize);
}
```

### DataProcessor 实现

```csharp
namespace MagnetometerSystem.Core.Processing;

public class DataProcessor : IDataProcessor
{
    public double[] MovingAverage(double[] data, int windowSize);
    public double[] MedianFilter(double[] data, int windowSize);
}
```

### ViewModel 新增属性

```csharp
// RealtimeChartViewModel 中新增
public bool IsFilterEnabled { get; set; }          // 是否启用滤波
public FilterType FilterType { get; set; }          // 滤波类型枚举
public int FilterWindowSize { get; set; } = 5;      // 滤波窗口大小

public enum FilterType
{
    MovingAverage,  // 移动平均
    Median          // 中值滤波
}
```

---

## 四、文件清单

| 操作 | 文件路径 | 说明 |
|------|---------|------|
| 新建 | `Core/Processing/IDataProcessor.cs` | 滤波接口定义 |
| 新建 | `Core/Processing/DataProcessor.cs` | 滤波算法实现 |
| 新建 | `Core/Processing/FilterType.cs` | 滤波类型枚举（如不放入已有文件） |
| 修改 | `App/ViewModels/RealtimeChartViewModel.cs` | 添加滤波属性和滤波调用逻辑 |
| 修改 | `App/Views/RealtimeChartView.xaml` | 添加滤波控制面板 UI |

---

## 五、实现指南

### 5.1 移动平均算法

```
输入: data[0..N-1], windowSize = W
输出: result[0..N-1]

halfW = W / 2
for i = 0 to N-1:
    start = max(0, i - halfW)
    end = min(N-1, i + halfW)
    result[i] = sum(data[start..end]) / (end - start + 1)
```

优化提示：可使用滑动求和，避免每个位置重复求和。

### 5.2 中值滤波算法

```
输入: data[0..N-1], windowSize = W (奇数)
输出: result[0..N-1]

halfW = W / 2
for i = 0 to N-1:
    start = max(0, i - halfW)
    end = min(N-1, i + halfW)
    window = data[start..end] 的副本
    排序 window
    result[i] = window[window.Length / 2]
```

### 5.3 参数校验

- `windowSize` 必须 >= 1。
- `data` 为 null 或空时返回空数组。
- 中值滤波的 `windowSize` 应为奇数，若传入偶数则自动 +1。

### 5.4 UI 集成

在 `RealtimeChartView.xaml` 的工具栏或侧边栏区域添加：

```xml
<!-- 滤波控制面板 -->
<StackPanel Orientation="Horizontal" Margin="4">
    <CheckBox Content="启用滤波" IsChecked="{Binding IsFilterEnabled}" />
    <ComboBox SelectedValue="{Binding FilterType}" Margin="4,0"
              ItemsSource="{Binding FilterTypes}"
              DisplayMemberPath="DisplayName" SelectedValuePath="Value" />
    <TextBlock Text="窗口大小:" VerticalAlignment="Center" Margin="4,0" />
    <TextBox Text="{Binding FilterWindowSize, UpdateSourceTrigger=PropertyChanged}"
             Width="50" />
</StackPanel>
```

### 5.5 图表叠加绘制

在 `RealtimeChartViewModel` 的图表更新逻辑中：

```
if IsFilterEnabled:
    rawData = 当前通道显示数据
    filteredData = FilterType switch
    {
        MovingAverage => _processor.MovingAverage(rawData, FilterWindowSize),
        Median => _processor.MedianFilter(rawData, FilterWindowSize),
    }
    添加虚线 Scatter/Signal 到 ScottPlot（LineStyle = dashed, 颜色稍浅）
```

ScottPlot 5.x 虚线设置：
```csharp
var sig = plot.Add.Signal(filteredData);
sig.LineStyle.Pattern = LinePattern.Dashed;
sig.LineStyle.Width = 1.5f;
sig.Color = originalColor.WithAlpha(0.6);
```

---

## 六、验收标准

1. `DataProcessor.MovingAverage` 对常数数组返回相同常数。
2. `DataProcessor.MedianFilter` 对含单个脉冲的数据能正确消除脉冲。
3. 两种滤波方法输出数组长度与输入一致。
4. UI 中勾选"启用滤波"后，图表上出现虚线滤波曲线。
5. 切换滤波类型和修改窗口大小后，图表实时更新。
6. 禁用滤波后，虚线曲线消失。
7. Core 项目中 `IDataProcessor` 和 `DataProcessor` 不依赖任何 UI 或 CommunityToolkit 组件。

---

## 七、单元测试要求

测试文件：`tests/MagnetometerSystem.Core.Tests/Processing/DataProcessorTests.cs`

| 测试方法 | 说明 |
|---------|------|
| `MovingAverage_ConstantData_ReturnsSameValues` | 常数数组滤波后不变 |
| `MovingAverage_KnownSequence_CorrectResult` | 已知序列验证手工计算结果 |
| `MovingAverage_WindowSize1_ReturnsCopy` | 窗口=1 时返回原始数据副本 |
| `MovingAverage_EmptyData_ReturnsEmpty` | 空数组输入返回空数组 |
| `MovingAverage_BoundaryHandling_NoException` | 边界处缩窗处理不抛异常 |
| `MedianFilter_SinglePulse_Removed` | 中值滤波消除单点脉冲 |
| `MedianFilter_KnownSequence_CorrectResult` | 已知序列验证手工计算结果 |
| `MedianFilter_EvenWindowSize_AdjustedToOdd` | 偶数窗口自动调整为奇数 |
| `MedianFilter_EmptyData_ReturnsEmpty` | 空数组输入返回空数组 |
| `MedianFilter_WindowLargerThanData_NoException` | 窗口大于数据长度时不抛异常 |
