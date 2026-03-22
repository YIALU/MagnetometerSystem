# TASK-B3: LTTB 降采样显示策略

## 基本信息

| 属性     | 值                           |
| -------- | ---------------------------- |
| 任务编号 | B3                           |
| 优先级   | P0                           |
| 阶段     | Phase 2 剩余                 |
| 流       | Stream D (Agent 4)           |
| 依赖     | 无                           |
| 状态     | 待实现                       |

**目标**：实现 LTTB（Largest Triangle Three Buckets）降采样算法，在图表渲染前将大量数据点压缩至目标数量，确保 100,000+ 数据点时仍可保持 30fps 以上刷新率，同时保留波形视觉特征。

---

## 功能需求

### 1. LTTB 降采样算法

LTTB 是一种面向可视化的降采样算法。核心思路：

1. 将原始数据平均划分为 `targetCount` 个桶（bucket）。
2. 第一个点和最后一个点始终保留。
3. 对中间每个桶，选择与前一个已选点和下一个桶的平均点构成的三角形面积最大的点——即最能代表该区间视觉特征的点。

该算法有效保留峰值、谷值和整体波形形状，视觉失真极小。

### 2. 按采样率的显示策略

| 采样率范围     | 策略                                          |
| -------------- | --------------------------------------------- |
| 高频 (>=100Hz) | 始终应用 LTTB 降采样                          |
| 中频 (1~100Hz) | 仅当数据点数 > `targetCount` 时应用 LTTB      |
| 低频 (<1Hz)    | 不降采样，使用 ScatterLine + 点标记(Marker)显示 |

### 3. targetCount 配置

- 默认值：`2000`（基于典型绘图区像素宽度）
- 通过 `RealtimeChartViewModel` 的属性暴露，可在运行时调整

---

## 接口契约

### 新建文件：`src/MagnetometerSystem.Core/Helpers/LttbDownsampler.cs`

```csharp
namespace MagnetometerSystem.Core.Helpers;

/// <summary>
/// Largest Triangle Three Buckets (LTTB) 降采样器
/// </summary>
public static class LttbDownsampler
{
    /// <summary>
    /// LTTB 降采样：将大量数据点降至 targetCount 个，保留波形特征。
    /// </summary>
    /// <param name="xs">时间轴数组（单调递增）</param>
    /// <param name="ys">值数组，与 xs 等长</param>
    /// <param name="targetCount">目标点数，必须 >= 2</param>
    /// <returns>降采样后的 (xs, ys) 元组；若原始点数 <= targetCount 则返回原数组的拷贝</returns>
    /// <exception cref="ArgumentException">xs 与 ys 长度不一致，或 targetCount < 2</exception>
    public static (double[] xs, double[] ys) Downsample(double[] xs, double[] ys, int targetCount);
}
```

**约束**：
- 该类位于 `MagnetometerSystem.Core` 项目，无 WPF/UI 依赖
- 纯静态方法，无副作用，线程安全
- 当 `xs.Length == 0` 时返回 `(Array.Empty<double>(), Array.Empty<double>())`
- 当 `xs.Length <= targetCount` 时原样返回（拷贝，不修改原数组引用）

### 修改文件：`src/MagnetometerSystem.App/ViewModels/RealtimeChartViewModel.cs`

新增属性：

```csharp
/// <summary>降采样目标点数（0 = 禁用降采样）</summary>
[ObservableProperty]
private int _downsampleTargetCount = 2000;
```

---

## 文件清单

| 操作 | 文件路径                                                                 | 说明                       |
| ---- | ------------------------------------------------------------------------ | -------------------------- |
| 新建 | `src/MagnetometerSystem.Core/Helpers/LttbDownsampler.cs`                 | LTTB 降采样静态类          |
| 修改 | `src/MagnetometerSystem.App/ViewModels/RealtimeChartViewModel.cs`        | 集成降采样到渲染流程       |
| 新建 | `tests/MagnetometerSystem.Core.Tests/Helpers/LttbDownsamplerTests.cs`   | 单元测试                   |

### 禁止修改的文件

- `src/MagnetometerSystem.Core/Models/` 目录下所有文件
- `src/MagnetometerSystem.Core/Protocol/` 目录下所有文件
- `src/MagnetometerSystem.Core/Communication/` 目录下所有文件

---

## 实现指南

### 步骤一：实现 LttbDownsampler

1. 在 `src/MagnetometerSystem.Core/Helpers/` 下创建 `LttbDownsampler.cs`。
2. 实现 LTTB 算法伪代码：

```
function Downsample(xs, ys, targetCount):
    n = xs.Length
    if n <= targetCount or targetCount < 2:
        return copy of (xs, ys)

    result_xs = new double[targetCount]
    result_ys = new double[targetCount]

    // 第一个点始终保留
    result_xs[0] = xs[0]
    result_ys[0] = ys[0]

    bucketSize = (n - 2) / (targetCount - 2)   // double 类型

    selectedIndex = 0  // 上一个已选点的索引

    for bucket = 1 to targetCount - 2:
        // 当前桶的范围
        bucketStart = floor(1 + (bucket - 1) * bucketSize)
        bucketEnd   = floor(1 + bucket * bucketSize)
        if bucketEnd > n - 1: bucketEnd = n - 1

        // 下一个桶的平均点（用于三角形面积计算）
        nextBucketStart = floor(1 + bucket * bucketSize)
        nextBucketEnd   = floor(1 + (bucket + 1) * bucketSize)
        if nextBucketEnd > n - 1: nextBucketEnd = n - 1
        avgX = average of xs[nextBucketStart..nextBucketEnd]
        avgY = average of ys[nextBucketStart..nextBucketEnd]

        // 在当前桶中选择使三角形面积最大的点
        maxArea = -1
        bestIndex = bucketStart
        for i = bucketStart to bucketEnd:
            area = abs((xs[selectedIndex] - avgX) * (ys[i] - ys[selectedIndex])
                     - (xs[selectedIndex] - xs[i]) * (avgY - ys[selectedIndex])) * 0.5
            if area > maxArea:
                maxArea = area
                bestIndex = i

        result_xs[bucket] = xs[bestIndex]
        result_ys[bucket] = ys[bestIndex]
        selectedIndex = bestIndex

    // 最后一个点始终保留
    result_xs[targetCount - 1] = xs[n - 1]
    result_ys[targetCount - 1] = ys[n - 1]

    return (result_xs, result_ys)
```

### 步骤二：在 RealtimeChartViewModel 中集成

1. 添加 `DownsampleTargetCount` 属性（默认 2000）。

2. 创建一个私有辅助方法，统一降采样逻辑：

```csharp
/// <summary>
/// 根据降采样策略处理窗口数据
/// </summary>
private (double[] xs, double[] ys) ApplyDownsampling(double[] windowTimes, double[] windowValues)
{
    if (DownsampleTargetCount <= 0 || windowTimes.Length <= DownsampleTargetCount)
        return (windowTimes, windowValues);

    return LttbDownsampler.Downsample(windowTimes, windowValues, DownsampleTargetCount);
}
```

3. **RenderSinglePlot 方法**（约第 284 行）：在 `plot.Add.ScatterLine(windowTimes, windowValues)` 之前插入降采样：

```csharp
// 原代码:
var sig = plot.Add.ScatterLine(windowTimes, windowValues);

// 改为:
var (plotXs, plotYs) = ApplyDownsampling(windowTimes, windowValues);
var sig = plot.Add.ScatterLine(plotXs, plotYs);
```

4. **RenderMultiPlot 方法**（约第 328 行）：同样模式：

```csharp
// 原代码:
var sig = plot.Add.ScatterLine(windowTimes, windowValues);

// 改为:
var (plotXs, plotYs) = ApplyDownsampling(windowTimes, windowValues);
var sig = plot.Add.ScatterLine(plotXs, plotYs);
```

5. **RenderComputedChannels 方法**（约第 375 行）：同样模式：

```csharp
// 原代码:
var compSig = plot.Add.ScatterLine(windowTimes, computedValues);

// 改为:
var (plotXs, plotYs) = ApplyDownsampling(windowTimes, computedValues);
var compSig = plot.Add.ScatterLine(plotXs, plotYs);
```

6. 低频数据点标记：当数据点数较少（如 `windowTimes.Length < DownsampleTargetCount` 且点数稀疏，可用 `count < 某阈值` 或按采样率判断），为 scatter 添加 Marker：

```csharp
// 低频数据：显示点标记
if (windowTimes.Length > 1)
{
    double effectiveSampleRate = (windowTimes.Length - 1) / (windowTimes[^1] - windowTimes[0]);
    if (effectiveSampleRate < 1.0)
    {
        sig.MarkerSize = 5;
        sig.MarkerShape = ScottPlot.MarkerShape.FilledCircle;
    }
}
```

### 步骤三：添加 using 引用

在 `RealtimeChartViewModel.cs` 顶部确保已有：

```csharp
using MagnetometerSystem.Core.Helpers;
```

该 using 已存在（因 `CircularBuffer` 和 `FormulaEvaluator`），无需额外添加。

---

## 验收标准

| 编号 | 验收条件                                                                 | 验证方式       |
| ---- | ------------------------------------------------------------------------ | -------------- |
| AC-1 | 100,000+ 数据点时图表渲染帧率 >= 30fps                                   | 手动测试       |
| AC-2 | 降采样后的图表与原始数据在视觉上无明显差异（峰/谷/趋势均保留）           | 目视对比       |
| AC-3 | 低频数据 (<1Hz) 显示点标记，不进行降采样                                  | 手动测试       |
| AC-4 | `DownsampleTargetCount` 设为 0 时禁用降采样，行为与修改前完全一致        | 手动测试       |
| AC-5 | 单元测试全部通过                                                         | `dotnet test`  |
| AC-6 | 未修改任何 Core/Models/、Core/Protocol/、Core/Communication/ 下的文件     | `git diff`     |

---

## 单元测试要求

文件路径：`tests/MagnetometerSystem.Core.Tests/Helpers/LttbDownsamplerTests.cs`

### 测试用例

```csharp
[TestClass]
public class LttbDownsamplerTests
{
    /// <summary>
    /// 正弦波降采样后应保留峰值和谷值
    /// </summary>
    [TestMethod]
    public void Downsample_SineWave_PreservesPeaksAndValleys()
    {
        // Arrange: 生成一个周期的正弦波，10000 个点
        int n = 10000;
        var xs = new double[n];
        var ys = new double[n];
        for (int i = 0; i < n; i++)
        {
            xs[i] = i;
            ys[i] = Math.Sin(2 * Math.PI * i / n);
        }

        // Act: 降采样至 200 个点
        var (rx, ry) = LttbDownsampler.Downsample(xs, ys, 200);

        // Assert
        Assert.AreEqual(200, rx.Length);
        Assert.AreEqual(200, ry.Length);

        // 降采样结果中应包含接近 +1 的峰值和接近 -1 的谷值
        double maxY = ry.Max();
        double minY = ry.Min();
        Assert.IsTrue(maxY > 0.95, $"峰值应接近 1.0，实际为 {maxY}");
        Assert.IsTrue(minY < -0.95, $"谷值应接近 -1.0，实际为 {minY}");
    }

    /// <summary>
    /// 数据点数少于目标数时应返回原数据（不变）
    /// </summary>
    [TestMethod]
    public void Downsample_FewerPointsThanTarget_ReturnsUnchanged()
    {
        var xs = new double[] { 1, 2, 3, 4, 5 };
        var ys = new double[] { 10, 20, 15, 25, 30 };

        var (rx, ry) = LttbDownsampler.Downsample(xs, ys, 100);

        Assert.AreEqual(5, rx.Length);
        CollectionAssert.AreEqual(xs, rx);
        CollectionAssert.AreEqual(ys, ry);
    }

    /// <summary>
    /// 空数组输入应返回空数组
    /// </summary>
    [TestMethod]
    public void Downsample_EmptyArray_ReturnsEmpty()
    {
        var (rx, ry) = LttbDownsampler.Downsample(
            Array.Empty<double>(), Array.Empty<double>(), 100);

        Assert.AreEqual(0, rx.Length);
        Assert.AreEqual(0, ry.Length);
    }

    /// <summary>
    /// 首尾点应始终被保留
    /// </summary>
    [TestMethod]
    public void Downsample_AlwaysPreservesFirstAndLastPoint()
    {
        int n = 5000;
        var xs = new double[n];
        var ys = new double[n];
        for (int i = 0; i < n; i++)
        {
            xs[i] = i * 0.1;
            ys[i] = i * i;
        }

        var (rx, ry) = LttbDownsampler.Downsample(xs, ys, 500);

        Assert.AreEqual(xs[0], rx[0], "第一个点的 X 应被保留");
        Assert.AreEqual(ys[0], ry[0], "第一个点的 Y 应被保留");
        Assert.AreEqual(xs[^1], rx[^1], "最后一个点的 X 应被保留");
        Assert.AreEqual(ys[^1], ry[^1], "最后一个点的 Y 应被保留");
    }

    /// <summary>
    /// 输出的 X 值应保持单调递增
    /// </summary>
    [TestMethod]
    public void Downsample_OutputXValuesAreMonotonicallyIncreasing()
    {
        int n = 10000;
        var xs = new double[n];
        var ys = new double[n];
        var rng = new Random(42);
        for (int i = 0; i < n; i++)
        {
            xs[i] = i;
            ys[i] = rng.NextDouble() * 100;
        }

        var (rx, _) = LttbDownsampler.Downsample(xs, ys, 500);

        for (int i = 1; i < rx.Length; i++)
        {
            Assert.IsTrue(rx[i] > rx[i - 1],
                $"X 值应单调递增，但 rx[{i}]={rx[i]} <= rx[{i - 1}]={rx[i - 1]}");
        }
    }
}
```
