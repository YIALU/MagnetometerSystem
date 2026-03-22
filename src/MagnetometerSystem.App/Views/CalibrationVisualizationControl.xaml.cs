using System.Windows;
using System.Windows.Controls;
using MagnetometerSystem.Core.Calibration;
using ScottPlot;
using ScottPlot.WPF;

namespace MagnetometerSystem.App.Views;

/// <summary>
/// 校正可视化 UserControl。
/// 显示正交度校正前后数据的对比：三面投影散点图、总场时间序列、残差直方图。
/// 可嵌入正交度校正向导的 Step 4（查看结果）。
/// </summary>
public partial class CalibrationVisualizationControl : UserControl
{
    /// <summary>降采样阈值：超过此数量的数据点将进行均匀抽样</summary>
    private const int DownsampleThreshold = 5000;

    #region Dependency Properties

    /// <summary>原始数据 N x 3 矩阵</summary>
    public static readonly DependencyProperty RawDataProperty =
        DependencyProperty.Register(
            nameof(RawData),
            typeof(double[,]),
            typeof(CalibrationVisualizationControl),
            new PropertyMetadata(null, OnDataChanged));

    /// <summary>校正后数据 N x 3 矩阵</summary>
    public static readonly DependencyProperty CorrectedDataProperty =
        DependencyProperty.Register(
            nameof(CorrectedData),
            typeof(double[,]),
            typeof(CalibrationVisualizationControl),
            new PropertyMetadata(null, OnDataChanged));

    /// <summary>参考场强 (nT)</summary>
    public static readonly DependencyProperty ReferenceFieldStrengthProperty =
        DependencyProperty.Register(
            nameof(ReferenceFieldStrength),
            typeof(double),
            typeof(CalibrationVisualizationControl),
            new PropertyMetadata(0.0, OnDataChanged));

    /// <summary>拟合质量</summary>
    public static readonly DependencyProperty FitQualityProperty =
        DependencyProperty.Register(
            nameof(FitQuality),
            typeof(FitQuality),
            typeof(CalibrationVisualizationControl),
            new PropertyMetadata(null));

    public double[,]? RawData
    {
        get => (double[,]?)GetValue(RawDataProperty);
        set => SetValue(RawDataProperty, value);
    }

    public double[,]? CorrectedData
    {
        get => (double[,]?)GetValue(CorrectedDataProperty);
        set => SetValue(CorrectedDataProperty, value);
    }

    public double ReferenceFieldStrength
    {
        get => (double)GetValue(ReferenceFieldStrengthProperty);
        set => SetValue(ReferenceFieldStrengthProperty, value);
    }

    public FitQuality? FitQuality
    {
        get => (FitQuality?)GetValue(FitQualityProperty);
        set => SetValue(FitQualityProperty, value);
    }

    #endregion

    public CalibrationVisualizationControl()
    {
        InitializeComponent();
    }

    private static void OnDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CalibrationVisualizationControl control)
        {
            control.UpdatePlots();
        }
    }

    /// <summary>
    /// 更新所有图表
    /// </summary>
    public void UpdatePlots()
    {
        if (RawData == null || CorrectedData == null)
            return;

        if (RawData.GetLength(1) < 3 || CorrectedData.GetLength(1) < 3)
            return;

        UpdateProjectionPlot(XYPlot, 0, 1, "Bx (nT)", "By (nT)");
        UpdateProjectionPlot(XZPlot, 0, 2, "Bx (nT)", "Bz (nT)");
        UpdateProjectionPlot(YZPlot, 1, 2, "By (nT)", "Bz (nT)");
        UpdateTotalFieldPlot();
        UpdateResidualHistogram();
    }

    /// <summary>
    /// 更新一个投影散点图（XY / XZ / YZ）
    /// </summary>
    private void UpdateProjectionPlot(WpfPlot wpfPlot, int axisA, int axisB,
        string xLabel, string yLabel)
    {
        var plot = wpfPlot.Plot;
        plot.Clear();

        int n = RawData!.GetLength(0);
        var indices = GetDownsampleIndices(n);
        int count = indices.Length;

        // 原始数据 - 红色半透明散点
        var rawX = new double[count];
        var rawY = new double[count];
        for (int i = 0; i < count; i++)
        {
            int idx = indices[i];
            rawX[i] = RawData[idx, axisA];
            rawY[i] = RawData[idx, axisB];
        }

        var rawScatter = plot.Add.ScatterPoints(rawX, rawY);
        rawScatter.Color = Colors.Red.WithAlpha(100);
        rawScatter.MarkerSize = 3;
        rawScatter.LegendText = "原始数据";

        // 校正数据 - 蓝色半透明散点
        var corX = new double[count];
        var corY = new double[count];
        for (int i = 0; i < count; i++)
        {
            int idx = indices[i];
            corX[i] = CorrectedData![idx, axisA];
            corY[i] = CorrectedData[idx, axisB];
        }

        var corScatter = plot.Add.ScatterPoints(corX, corY);
        corScatter.Color = Colors.Blue.WithAlpha(150);
        corScatter.MarkerSize = 3;
        corScatter.LegendText = "校正数据";

        // 参考圆 - 灰色虚线
        if (ReferenceFieldStrength > 0)
        {
            const int circlePoints = 361;
            var circleX = new double[circlePoints];
            var circleY = new double[circlePoints];
            for (int i = 0; i < circlePoints; i++)
            {
                double angle = i * Math.PI / 180.0;
                circleX[i] = ReferenceFieldStrength * Math.Cos(angle);
                circleY[i] = ReferenceFieldStrength * Math.Sin(angle);
            }

            var circle = plot.Add.ScatterLine(circleX, circleY);
            circle.Color = Colors.Gray;
            circle.LineWidth = 1;
            circle.LinePattern = LinePattern.Dashed;
            circle.LegendText = "参考圆";
        }

        // 坐标轴设置
        plot.Axes.AutoScale();
        plot.Axes.Bottom.Label.Text = xLabel;
        plot.Axes.Left.Label.Text = yLabel;
        plot.Legend.IsVisible = true;

        wpfPlot.Refresh();
    }

    /// <summary>
    /// 更新总场时间序列对比图
    /// </summary>
    private void UpdateTotalFieldPlot()
    {
        var plot = TotalFieldPlot.Plot;
        plot.Clear();

        int n = RawData!.GetLength(0);
        var rawTotal = new double[n];
        var corTotal = new double[n];
        var indices = new double[n];

        for (int i = 0; i < n; i++)
        {
            indices[i] = i;
            rawTotal[i] = Math.Sqrt(
                RawData[i, 0] * RawData[i, 0] +
                RawData[i, 1] * RawData[i, 1] +
                RawData[i, 2] * RawData[i, 2]);
            corTotal[i] = Math.Sqrt(
                CorrectedData![i, 0] * CorrectedData[i, 0] +
                CorrectedData[i, 1] * CorrectedData[i, 1] +
                CorrectedData[i, 2] * CorrectedData[i, 2]);
        }

        // 降采样
        var dsIndices = GetDownsampleIndices(n);
        var dsX = new double[dsIndices.Length];
        var dsRawY = new double[dsIndices.Length];
        var dsCorY = new double[dsIndices.Length];
        for (int i = 0; i < dsIndices.Length; i++)
        {
            int idx = dsIndices[i];
            dsX[i] = indices[idx];
            dsRawY[i] = rawTotal[idx];
            dsCorY[i] = corTotal[idx];
        }

        // 原始总场 - 红色折线
        var rawLine = plot.Add.ScatterLine(dsX, dsRawY);
        rawLine.Color = Colors.Red;
        rawLine.LineWidth = 1;
        rawLine.LegendText = "原始总场";

        // 校正总场 - 蓝色折线
        var corLine = plot.Add.ScatterLine(dsX, dsCorY);
        corLine.Color = Colors.Blue;
        corLine.LineWidth = 1;
        corLine.LegendText = "校正总场";

        // 参考场强 - 绿色虚线水平线
        if (ReferenceFieldStrength > 0)
        {
            var refLine = plot.Add.HorizontalLine(ReferenceFieldStrength);
            refLine.Color = Colors.Green;
            refLine.LinePattern = LinePattern.Dashed;
            refLine.LineWidth = 1;
            refLine.LegendText = "参考场强";
        }

        plot.Axes.AutoScale();
        plot.Axes.Bottom.Label.Text = "样本序号";
        plot.Axes.Left.Label.Text = "|B| (nT)";
        plot.Legend.IsVisible = true;

        TotalFieldPlot.Refresh();
    }

    /// <summary>
    /// 更新残差分布直方图
    /// </summary>
    private void UpdateResidualHistogram()
    {
        var plot = ResidualPlot.Plot;
        plot.Clear();

        int n = CorrectedData!.GetLength(0);
        var residuals = new double[n];
        for (int i = 0; i < n; i++)
        {
            double total = Math.Sqrt(
                CorrectedData[i, 0] * CorrectedData[i, 0] +
                CorrectedData[i, 1] * CorrectedData[i, 1] +
                CorrectedData[i, 2] * CorrectedData[i, 2]);
            residuals[i] = total - ReferenceFieldStrength;
        }

        // 手动计算直方图 bin
        const int binCount = 25;
        double minVal = residuals[0], maxVal = residuals[0];
        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            if (residuals[i] < minVal) minVal = residuals[i];
            if (residuals[i] > maxVal) maxVal = residuals[i];
            sum += residuals[i];
        }

        double mean = sum / n;
        double range = maxVal - minVal;
        if (range < 1e-10) range = 1.0;

        double binWidth = range / binCount;
        var binCenters = new double[binCount];
        var binCounts = new double[binCount];

        for (int i = 0; i < binCount; i++)
        {
            binCenters[i] = minVal + (i + 0.5) * binWidth;
        }

        for (int i = 0; i < n; i++)
        {
            int bin = (int)((residuals[i] - minVal) / binWidth);
            if (bin >= binCount) bin = binCount - 1;
            if (bin < 0) bin = 0;
            binCounts[bin]++;
        }

        // 用 ScatterLine 模拟柱状图（阶梯线）
        // 每个 bin 用 4 个点画矩形顶部
        var barX = new List<double>();
        var barY = new List<double>();
        for (int i = 0; i < binCount; i++)
        {
            double left = minVal + i * binWidth;
            double right = left + binWidth;
            barX.Add(left);
            barY.Add(binCounts[i]);
            barX.Add(right);
            barY.Add(binCounts[i]);
        }

        var bars = plot.Add.ScatterLine(barX.ToArray(), barY.ToArray());
        bars.Color = Colors.Blue;
        bars.LineWidth = 2;
        bars.LegendText = "残差分布";

        // 均值标注线
        var meanLine = plot.Add.VerticalLine(mean);
        meanLine.Color = Colors.Red;
        meanLine.LinePattern = LinePattern.Dashed;
        meanLine.LineWidth = 1;
        meanLine.LegendText = $"均值: {mean:F2} nT";

        plot.Axes.AutoScale();
        plot.Axes.Bottom.Label.Text = "残差 (nT)";
        plot.Axes.Left.Label.Text = "频次";
        plot.Legend.IsVisible = true;

        ResidualPlot.Refresh();
    }

    /// <summary>
    /// 对数据点进行均匀降采样。
    /// 当数据量超过 DownsampleThreshold 时，均匀抽样以保持分布特征。
    /// </summary>
    private static int[] GetDownsampleIndices(int totalCount)
    {
        if (totalCount <= DownsampleThreshold)
        {
            var all = new int[totalCount];
            for (int i = 0; i < totalCount; i++)
                all[i] = i;
            return all;
        }

        // 均匀抽样
        var indices = new int[DownsampleThreshold];
        double step = (double)(totalCount - 1) / (DownsampleThreshold - 1);
        for (int i = 0; i < DownsampleThreshold; i++)
        {
            indices[i] = (int)Math.Round(i * step);
        }

        return indices;
    }
}
