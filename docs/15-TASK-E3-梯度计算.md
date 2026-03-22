# TASK-E3: 梯度计算（GradientCalculator）

**文档版本**: v1.0
**更新日期**: 2026-03-21
**优先级**: P2
**阶段**: Phase 4
**流**: Stream C
**依赖**: 无硬依赖

---

## 一、基本信息

### 背景

当前系统已通过 `ComputedChannelDefinition` 支持梯度类型（`ComputedChannelType.Gradient`），在向导中选择两个源通道后计算 `(a) - (b)` 的差值。但这只是简单的场差，未考虑传感器间的基线距离，无法得到物理意义上的梯度值（单位 nT/m）。

### 目标

新增 `GradientCalculator` 静态工具类，实现带基线距离参数的梯度计算。在梯度向导 UI 中增加基线距离输入字段，使计算结果具有正确的物理单位。

---

## 二、功能需求

1. **单点梯度计算** — 给定两个传感器的磁场值和基线距离，返回梯度值 `(B1 - B2) / distance`。
2. **批量轴向梯度** — 对两组传感器数据数组逐元素计算梯度，返回结果数组。
3. **基线距离参数** — 以米为单位，必须为正数。
4. **向导 UI 扩展** — 在梯度类型的计算通道向导中添加基线距离输入框。
5. **公式集成** — 将基线距离纳入 `ComputedChannelDefinition` 的公式生成，使 `FormulaEvaluator` 能正确计算。

---

## 三、接口契约

### GradientCalculator 静态类

```csharp
namespace MagnetometerSystem.Core.Processing;

/// <summary>
/// 磁场梯度计算工具类。
/// </summary>
public static class GradientCalculator
{
    /// <summary>
    /// 计算两点间的磁场梯度。
    /// </summary>
    /// <param name="value1">传感器1的磁场值（nT）。</param>
    /// <param name="value2">传感器2的磁场值（nT）。</param>
    /// <param name="baselineDistanceMeters">基线距离（米），必须 > 0。</param>
    /// <returns>梯度值（nT/m）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">baselineDistanceMeters &lt;= 0。</exception>
    public static double ComputeGradient(double value1, double value2, double baselineDistanceMeters);

    /// <summary>
    /// 批量计算两组传感器数据的逐点梯度。
    /// </summary>
    /// <param name="sensor1">传感器1的数据数组。</param>
    /// <param name="sensor2">传感器2的数据数组，长度必须与 sensor1 相同。</param>
    /// <param name="baselineDistanceMeters">基线距离（米），必须 > 0。</param>
    /// <returns>梯度数组（nT/m），长度与输入相同。</returns>
    public static double[] ComputeAxisGradients(double[] sensor1, double[] sensor2, double baselineDistanceMeters);
}
```

### ViewModel 新增属性

```csharp
// RealtimeChartViewModel 中梯度向导相关
public double WizardBaselineDistance { get; set; } = 1.0;  // 默认 1 米
```

### 公式生成变更

当前梯度公式：`({CH_A} - {CH_B})`

新梯度公式：`(({CH_A} - {CH_B}) / {BASELINE})`

其中 `{BASELINE}` 替换为实际的基线距离数值。

---

## 四、文件清单

| 操作 | 文件路径 | 说明 |
|------|---------|------|
| 新建 | `Core/Processing/GradientCalculator.cs` | 梯度计算静态工具类 |
| 修改 | `App/ViewModels/RealtimeChartViewModel.cs` | 添加 `WizardBaselineDistance` 属性，修改梯度公式生成逻辑 |
| 修改 | `App/Views/RealtimeChartView.xaml` | 梯度向导中添加基线距离输入框 |

---

## 五、实现指南

### 5.1 核心算法

```csharp
public static double ComputeGradient(double value1, double value2, double baselineDistanceMeters)
{
    if (baselineDistanceMeters <= 0)
        throw new ArgumentOutOfRangeException(nameof(baselineDistanceMeters), "基线距离必须大于零。");

    return (value1 - value2) / baselineDistanceMeters;
}

public static double[] ComputeAxisGradients(double[] sensor1, double[] sensor2, double baselineDistanceMeters)
{
    if (baselineDistanceMeters <= 0)
        throw new ArgumentOutOfRangeException(nameof(baselineDistanceMeters));
    if (sensor1.Length != sensor2.Length)
        throw new ArgumentException("两组传感器数据长度必须相同。");

    var result = new double[sensor1.Length];
    for (int i = 0; i < sensor1.Length; i++)
        result[i] = (sensor1[i] - sensor2[i]) / baselineDistanceMeters;
    return result;
}
```

### 5.2 向导 UI 扩展

在梯度向导区域（`ComputedChannelType.Gradient` 对应的面板）中添加：

```xml
<!-- 梯度向导 - 基线距离 -->
<StackPanel Orientation="Horizontal" Margin="4"
            Visibility="{Binding IsGradientWizardVisible, Converter={StaticResource BoolToVisibility}}">
    <TextBlock Text="基线距离 (m):" VerticalAlignment="Center" Margin="4,0" />
    <TextBox Text="{Binding WizardBaselineDistance, UpdateSourceTrigger=PropertyChanged,
                    StringFormat=F3}" Width="80" />
</StackPanel>
```

### 5.3 公式生成修改

在 `RealtimeChartViewModel` 中生成梯度计算通道的逻辑：

```csharp
// 修改前
var formula = $"({{CH_{sourceA}}} - {{CH_{sourceB}}})";

// 修改后
var formula = $"(({{CH_{sourceA}}} - {{CH_{sourceB}}}) / {WizardBaselineDistance:F6})";
```

注意：`WizardBaselineDistance` 直接以数值形式嵌入公式字符串，`FormulaEvaluator` 的 Shunting-yard 解析器支持数值字面量和除法运算，无需修改解析器。

### 5.4 单位标注

梯度通道的单位应标注为 "nT/m"。可在 `ComputedChannelDefinition` 或显示配置中设置：

```csharp
channelDef.Unit = "nT/m";
```

---

## 六、验收标准

1. `ComputeGradient(50000, 49990, 1.0)` 返回 `10.0`。
2. `ComputeGradient(50000, 49990, 0.5)` 返回 `20.0`。
3. `baselineDistanceMeters <= 0` 时抛出 `ArgumentOutOfRangeException`。
4. `ComputeAxisGradients` 对长度不等的数组抛出 `ArgumentException`。
5. 梯度向导 UI 中可输入基线距离，默认值为 1.0。
6. 创建梯度计算通道后，公式中包含除以基线距离的运算。
7. 图表上显示的梯度值单位为 nT/m，数值正确。

---

## 七、单元测试要求

测试文件：`tests/MagnetometerSystem.Core.Tests/Processing/GradientCalculatorTests.cs`

| 测试方法 | 说明 |
|---------|------|
| `ComputeGradient_KnownValues_CorrectResult` | 已知值验证：(50000-49990)/1.0 = 10.0 |
| `ComputeGradient_HalfMeterBaseline_DoublesGradient` | 基线减半，梯度翻倍 |
| `ComputeGradient_EqualValues_ReturnsZero` | 两值相等时返回 0 |
| `ComputeGradient_ZeroDistance_ThrowsException` | 基线为 0 抛出异常 |
| `ComputeGradient_NegativeDistance_ThrowsException` | 基线为负数抛出异常 |
| `ComputeAxisGradients_Arrays_CorrectResults` | 数组批量计算验证 |
| `ComputeAxisGradients_DifferentLengths_ThrowsException` | 数组长度不同抛出异常 |
| `ComputeAxisGradients_EmptyArrays_ReturnsEmpty` | 空数组输入返回空数组 |
