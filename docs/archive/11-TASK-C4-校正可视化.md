# TASK C-4：校正可视化

**文档版本**: v1.0
**编写日期**: 2026-03-21
**优先级**: P0
**所属阶段**: Phase 4
**任务流**: Stream C（正交度校正）
**前置依赖**: C-1（正交度计算引擎）、C-2（正交度校正应用）

---

## 一、基本信息

### 1.1 任务概述

实现正交度校正前后数据的对比可视化，作为 C-3 向导界面 Step 4（查看结果）的核心组件。通过散点图投影和总场时间序列对比，让用户直观验证校正效果——理想情况下，校正前的椭球数据在校正后应变为接近圆球的分布。

### 1.2 可视化方案

| 图表 | 类型 | 内容 |
|------|------|------|
| XY 投影 | ScottPlot 散点图 | X-Y 平面投影，红色原始 + 蓝色校正 |
| XZ 投影 | ScottPlot 散点图 | X-Z 平面投影，红色原始 + 蓝色校正 |
| YZ 投影 | ScottPlot 散点图 | Y-Z 平面投影，红色原始 + 蓝色校正 |
| 总场时间序列 | ScottPlot 折线图 | 校正前(红) vs 校正后(蓝) 总场强度 |
| 残差分布 | ScottPlot 直方图（可选） | 校正后残差分布 |

### 1.3 涉及的现有代码

| 文件 | 作用 |
|------|------|
| `Core/Calibration/IOrthogonalityService.cs` | 调用 `Apply()` 计算校正后数据 |
| `Core/Models/OrthogonalityParams.cs` | 校正参数，调用 `Apply(x,y,z)` |
| ScottPlot 5.x NuGet 包 | 已在项目中引用（实时图表模块使用） |

---

## 二、功能需求

### 2.1 三面投影散点图

#### 2.1.1 XY 投影面板

- **X 轴**: Bx (nT)
- **Y 轴**: By (nT)
- **原始数据**: 红色散点（半透明），标记为 "原始数据"
- **校正数据**: 蓝色散点（半透明），标记为 "校正数据"
- **参考圆**: 灰色虚线圆，半径 = 参考场强，圆心 = 原点
- **坐标轴**: 等比例缩放（AxisScaleLock），确保圆不变形
- **图例**: 右上角，显示 "原始" 和 "校正" 两个图例项

#### 2.1.2 XZ 投影面板

- **X 轴**: Bx (nT)
- **Y 轴**: Bz (nT)
- 其余同 XY 面板

#### 2.1.3 YZ 投影面板

- **X 轴**: By (nT)
- **Y 轴**: Bz (nT)
- 其余同 XY 面板

### 2.2 总场时间序列图

- **X 轴**: 样本序号（或时间戳，若可用）
- **Y 轴**: 总场强度 |B| (nT)
- **红色折线**: 原始数据总场 = sqrt(Bx^2 + By^2 + Bz^2)
- **蓝色折线**: 校正后总场
- **绿色水平线**: 参考场强值（虚线）
- **图例**: 右上角

### 2.3 残差分布直方图（可选）

- **X 轴**: 残差值 (nT)，即 |B_corrected| - referenceFieldStrength
- **Y 轴**: 频次
- **柱状图**: 20~30 个区间
- **标注**: 均值和标准差竖线

### 2.4 图表交互

- ScottPlot 默认交互（拖拽平移、滚轮缩放）
- "重置视图" 按钮，恢复默认缩放范围
- 鼠标悬停可查看坐标值（ScottPlot 内置 Crosshair）

---

## 三、接口契约

### 3.1 可视化组件接口

可视化逻辑可以直接嵌入 `OrthogonalityCalibrationViewModel`（Step 4 部分），或抽取为独立的 UserControl。推荐后者以保持代码清晰。

```csharp
// 文件: App/Views/CalibrationVisualizationControl.xaml.cs
// 或直接在 OrthogonalityCalibrationView.xaml 的 Step 4 区域实现
namespace MagnetometerSystem.App.Views;

/// <summary>
/// 校正可视化 UserControl
/// </summary>
public partial class CalibrationVisualizationControl : UserControl
{
    // 依赖属性，用于从外部绑定数据
    public static readonly DependencyProperty RawDataProperty = ...;
    public static readonly DependencyProperty CorrectedDataProperty = ...;
    public static readonly DependencyProperty ReferenceFieldStrengthProperty = ...;
    public static readonly DependencyProperty FitQualityProperty = ...;

    /// <summary>原始数据 N×3</summary>
    public double[,] RawData { get; set; }

    /// <summary>校正后数据 N×3</summary>
    public double[,] CorrectedData { get; set; }

    /// <summary>参考场强 (nT)</summary>
    public double ReferenceFieldStrength { get; set; }

    /// <summary>拟合质量</summary>
    public FitQuality? FitQuality { get; set; }

    /// <summary>更新所有图表</summary>
    public void UpdatePlots();
}
```

### 3.2 ViewModel 侧数据准备

在 `OrthogonalityCalibrationViewModel` 中，Step 3 计算完成后准备可视化数据：

```csharp
// 计算完成后，准备校正后数据用于可视化
private void PrepareVisualizationData()
{
    if (CalculationResult?.Parameters == null) return;

    var rawData = ConvertToMatrix(_collectedData);
    int n = rawData.GetLength(0);
    var correctedData = new double[n, 3];

    for (int i = 0; i < n; i++)
    {
        var corrected = CalculationResult.Parameters.Apply(
            rawData[i, 0], rawData[i, 1], rawData[i, 2]);
        correctedData[i, 0] = corrected[0];
        correctedData[i, 1] = corrected[1];
        correctedData[i, 2] = corrected[2];
    }

    RawDataForVisualization = rawData;
    CorrectedDataForVisualization = correctedData;
}
```

---

## 四、文件清单

### 4.1 新建文件

| 文件路径 | 说明 |
|----------|------|
| `src/MagnetometerSystem.App/Views/CalibrationVisualizationControl.xaml` | 可视化 UserControl XAML |
| `src/MagnetometerSystem.App/Views/CalibrationVisualizationControl.xaml.cs` | 可视化代码隐藏，ScottPlot 绑定逻辑 |

### 4.2 修改文件

| 文件路径 | 修改说明 |
|----------|----------|
| `src/MagnetometerSystem.App/Views/OrthogonalityCalibrationView.xaml` | Step 4 区域嵌入 `CalibrationVisualizationControl` |
| `src/MagnetometerSystem.App/ViewModels/OrthogonalityCalibrationViewModel.cs` | 新增可视化数据属性和准备方法 |

---

## 五、数据库变更

无。

---

## 六、实现指南

### 6.1 XAML 布局结构

```xml
<!-- CalibrationVisualizationControl.xaml -->
<UserControl x:Class="MagnetometerSystem.App.Views.CalibrationVisualizationControl"
             xmlns:scottPlot="clr-namespace:ScottPlot.WPF;assembly=ScottPlot.WPF">

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="*"/>    <!-- 散点图行 -->
            <RowDefinition Height="*"/>    <!-- 总场 + 直方图行 -->
        </Grid.RowDefinitions>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>  <!-- 左 -->
            <ColumnDefinition Width="*"/>  <!-- 中 -->
            <ColumnDefinition Width="*"/>  <!-- 右 -->
        </Grid.ColumnDefinitions>

        <!-- XY 投影 -->
        <GroupBox Header="XY 平面投影" Grid.Row="0" Grid.Column="0" Margin="4">
            <scottPlot:WpfPlot x:Name="PlotXY"/>
        </GroupBox>

        <!-- XZ 投影 -->
        <GroupBox Header="XZ 平面投影" Grid.Row="0" Grid.Column="1" Margin="4">
            <scottPlot:WpfPlot x:Name="PlotXZ"/>
        </GroupBox>

        <!-- YZ 投影 -->
        <GroupBox Header="YZ 平面投影" Grid.Row="0" Grid.Column="2" Margin="4">
            <scottPlot:WpfPlot x:Name="PlotYZ"/>
        </GroupBox>

        <!-- 总场时间序列 -->
        <GroupBox Header="总场强度对比" Grid.Row="1" Grid.Column="0"
                  Grid.ColumnSpan="2" Margin="4">
            <scottPlot:WpfPlot x:Name="PlotTotalField"/>
        </GroupBox>

        <!-- 残差直方图 -->
        <GroupBox Header="残差分布" Grid.Row="1" Grid.Column="2" Margin="4">
            <scottPlot:WpfPlot x:Name="PlotResidual"/>
        </GroupBox>
    </Grid>
</UserControl>
```

### 6.2 散点图绑定代码

```csharp
// CalibrationVisualizationControl.xaml.cs
public void UpdatePlots()
{
    if (RawData == null || CorrectedData == null) return;

    UpdateProjectionPlot(PlotXY, 0, 1, "Bx (nT)", "By (nT)");
    UpdateProjectionPlot(PlotXZ, 0, 2, "Bx (nT)", "Bz (nT)");
    UpdateProjectionPlot(PlotYZ, 1, 2, "By (nT)", "Bz (nT)");
    UpdateTotalFieldPlot();
    UpdateResidualHistogram();
}

private void UpdateProjectionPlot(WpfPlot plot, int axisA, int axisB,
    string xLabel, string yLabel)
{
    plot.Plot.Clear();

    int n = RawData.GetLength(0);

    // 原始数据 — 红色半透明散点
    var rawX = new double[n];
    var rawY = new double[n];
    for (int i = 0; i < n; i++)
    {
        rawX[i] = RawData[i, axisA];
        rawY[i] = RawData[i, axisB];
    }
    var rawScatter = plot.Plot.Add.ScatterPoints(rawX, rawY);
    rawScatter.Color = ScottPlot.Colors.Red.WithAlpha(100);
    rawScatter.LegendText = "原始数据";
    rawScatter.MarkerSize = 3;

    // 校正数据 — 蓝色半透明散点
    var corrX = new double[n];
    var corrY = new double[n];
    for (int i = 0; i < n; i++)
    {
        corrX[i] = CorrectedData[i, axisA];
        corrY[i] = CorrectedData[i, axisB];
    }
    var corrScatter = plot.Plot.Add.ScatterPoints(corrX, corrY);
    corrScatter.Color = ScottPlot.Colors.Blue.WithAlpha(150);
    corrScatter.LegendText = "校正数据";
    corrScatter.MarkerSize = 3;

    // 参考圆 — 灰色虚线
    if (ReferenceFieldStrength > 0)
    {
        var circle = plot.Plot.Add.Circle(0, 0, ReferenceFieldStrength);
        circle.LineColor = ScottPlot.Colors.Gray;
        circle.LinePattern = ScottPlot.LinePattern.Dashed;
        circle.LineWidth = 1;
    }

    // 坐标轴设置
    plot.Plot.Axes.SetLimitsEqual();
    plot.Plot.XLabel(xLabel);
    plot.Plot.YLabel(yLabel);
    plot.Plot.Legend.IsVisible = true;

    plot.Refresh();
}

private void UpdateTotalFieldPlot()
{
    PlotTotalField.Plot.Clear();
    int n = RawData.GetLength(0);

    var rawTotal = new double[n];
    var corrTotal = new double[n];
    var indices = new double[n];

    for (int i = 0; i < n; i++)
    {
        indices[i] = i;
        rawTotal[i] = Math.Sqrt(
            RawData[i, 0] * RawData[i, 0] +
            RawData[i, 1] * RawData[i, 1] +
            RawData[i, 2] * RawData[i, 2]);
        corrTotal[i] = Math.Sqrt(
            CorrectedData[i, 0] * CorrectedData[i, 0] +
            CorrectedData[i, 1] * CorrectedData[i, 1] +
            CorrectedData[i, 2] * CorrectedData[i, 2]);
    }

    var rawLine = PlotTotalField.Plot.Add.ScatterLine(indices, rawTotal);
    rawLine.Color = ScottPlot.Colors.Red;
    rawLine.LegendText = "原始总场";

    var corrLine = PlotTotalField.Plot.Add.ScatterLine(indices, corrTotal);
    corrLine.Color = ScottPlot.Colors.Blue;
    corrLine.LegendText = "校正总场";

    // 参考线
    if (ReferenceFieldStrength > 0)
    {
        var refLine = PlotTotalField.Plot.Add.HorizontalLine(ReferenceFieldStrength);
        refLine.Color = ScottPlot.Colors.Green;
        refLine.LinePattern = ScottPlot.LinePattern.Dashed;
        refLine.LegendText = "参考场强";
    }

    PlotTotalField.Plot.XLabel("样本序号");
    PlotTotalField.Plot.YLabel("|B| (nT)");
    PlotTotalField.Plot.Legend.IsVisible = true;
    PlotTotalField.Refresh();
}

private void UpdateResidualHistogram()
{
    PlotResidual.Plot.Clear();
    int n = CorrectedData.GetLength(0);

    var residuals = new double[n];
    for (int i = 0; i < n; i++)
    {
        double total = Math.Sqrt(
            CorrectedData[i, 0] * CorrectedData[i, 0] +
            CorrectedData[i, 1] * CorrectedData[i, 1] +
            CorrectedData[i, 2] * CorrectedData[i, 2]);
        residuals[i] = total - ReferenceFieldStrength;
    }

    // ScottPlot 5.x 直方图
    var hist = ScottPlot.Statistics.Histogram.WithBinCount(30, residuals);
    var bar = PlotResidual.Plot.Add.Bars(hist.Bins.Select(b => b.Center).ToArray(),
                                          hist.Bins.Select(b => (double)b.Count).ToArray());

    // 均值标注线
    double mean = residuals.Average();
    var meanLine = PlotResidual.Plot.Add.VerticalLine(mean);
    meanLine.Color = ScottPlot.Colors.Red;
    meanLine.LinePattern = ScottPlot.LinePattern.Dashed;
    meanLine.LegendText = $"均值: {mean:F2} nT";

    PlotResidual.Plot.XLabel("残差 (nT)");
    PlotResidual.Plot.YLabel("频次");
    PlotResidual.Plot.Legend.IsVisible = true;
    PlotResidual.Refresh();
}
```

### 6.3 ScottPlot 5.x API 注意事项

- ScottPlot 5.x 的 API 与 4.x 有较大差异，注意使用 `plot.Plot.Add.*` 而非 `plot.Plot.Add*`
- 等比例缩放使用 `plot.Plot.Axes.SetLimitsEqual()`
- 散点图点使用 `Add.ScatterPoints()`，折线使用 `Add.ScatterLine()`
- 颜色使用 `ScottPlot.Colors.Red` 而非 `System.Drawing.Color`
- 半透明使用 `.WithAlpha(byte)` 方法
- 直方图 API 需确认当前 ScottPlot 5.x 版本的具体实现，可能需要手动计算 bin 再用 Bar 图绘制

### 6.4 性能优化

- 数据点数 > 5000 时，对散点图做降采样（均匀抽样），避免渲染卡顿
- 降采样后保留数据点的分布特征（不只取前 N 个点）
- 直方图计算为 O(N)，无性能问题

---

## 七、验收标准

### 7.1 功能验收

| 编号 | 验收项 | 通过条件 |
|------|--------|----------|
| AC-1 | XY 投影显示 | 原始数据红色、校正数据蓝色，两组散点均可见 |
| AC-2 | XZ 投影显示 | 同 AC-1 |
| AC-3 | YZ 投影显示 | 同 AC-1 |
| AC-4 | 参考圆 | 参考场强对应的圆在每个投影图中可见 |
| AC-5 | 等比例缩放 | 三个投影图坐标轴等比例，圆不变形为椭圆 |
| AC-6 | 总场对比 | 校正前后总场折线图显示，校正后更平稳 |
| AC-7 | 参考线 | 总场图中绿色虚线标出参考场强 |
| AC-8 | 残差直方图 | 直方图显示校正后残差分布，均值标注线可见 |
| AC-9 | 图例 | 每个图表的图例正确显示 |
| AC-10 | 视觉效果 | 校正前椭球形投影 → 校正后接近圆形投影 |
| AC-11 | 嵌入向导 | 可视化组件正确嵌入 C-3 向导的 Step 4 |

### 7.2 非功能验收

| 编号 | 验收项 | 通过条件 |
|------|--------|----------|
| NF-1 | 渲染性能 | 5000 个数据点渲染 < 1 秒 |
| NF-2 | 交互响应 | 图表拖拽缩放流畅 |
| NF-3 | 内存 | 10000 个数据点的可视化内存增长 < 50MB |

---

## 八、单元测试要求

可视化组件为 UI 层，单元测试主要覆盖数据准备逻辑。

测试文件：`tests/MagnetometerSystem.Core.Tests/Calibration/CalibrationVisualizationTests.cs`

### 8.1 测试用例清单

| 编号 | 测试名称 | 测试内容 |
|------|----------|----------|
| T-01 | `PrepareVisualizationData_CorrectDimensions` | 校正后数据矩阵维度与原始数据一致 |
| T-02 | `PrepareVisualizationData_IdentityParams_DataUnchanged` | 单位矩阵参数时校正数据等于原始数据 |
| T-03 | `PrepareVisualizationData_CorrectedTotalField_MoreUniform` | 校正后总场标准差 < 原始总场标准差 |
| T-04 | `ComputeResiduals_CorrectValues` | 残差 = |B_corrected| - referenceFieldStrength 计算正确 |
| T-05 | `ComputeResiduals_MeanMatchesFitQuality` | 残差均值与 FitQuality.ResidualMean 一致 |
| T-06 | `Downsampling_PreservesDistribution` | 降采样后数据分布特征保持（覆盖度不显著下降） |

**注意**：ScottPlot 渲染本身不做单元测试。图表的视觉效果通过人工验收确认。
