using System.Windows;
using System.Windows.Controls;
using MagnetometerSystem.App.Helpers;
using MagnetometerSystem.Core.Calibration;
using ScottPlot;
using ScottPlot.WPF;

namespace MagnetometerSystem.App.Views;

/// <summary>
/// 校正可视化 UserControl。
/// 显示正交度校正前后数据的对比：三面投影散点图、总场时间序列、残差直方图。
/// 嵌入正交度校正向导的第 3 步（计算与结果）。
/// </summary>
public partial class CalibrationVisualizationControl : UserControl
{
    /// <summary>降采样阈值：超过此数量的数据点将进行均匀抽样</summary>
    private const int DownsampleThreshold = 5000;

    // 配色：原始为中性灰，校正后取校验过的 8 色中的蓝；参考线用墨灰虚线，均值用橙。红色只留给错误。
    private static readonly Color RawColor = Color.FromHex("#9AA6A1");
    private static readonly Color CorrectedColor = Color.FromHex("#2A78D6");
    private static readonly Color ReferenceColor = Color.FromHex("#66726E");
    private static readonly Color MeanColor = Color.FromHex("#EB6834");

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

    /// <summary>数据单位（来自协议通道），用于坐标轴标签。</summary>
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(CalibrationVisualizationControl), new PropertyMetadata("", OnDataChanged));
    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    /// <summary>参考场强（与数据同单位）；不大于 0 时用校正后总场的均值（与计算器的残差定义一致）。</summary>
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
        foreach (var plot in new[] { XYPlot, XZPlot, YZPlot, TotalFieldPlot, ResidualPlot })
        {
            ChartFontHelper.Apply(plot.Plot);
            plot.Plot.Axes.Left.TickLabelStyle.FontSize = 11;
            plot.Plot.Axes.Bottom.TickLabelStyle.FontSize = 11;
            plot.Plot.Legend.FontSize = 11;
        }
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

        if (RawData.GetLength(1) < 3 || CorrectedData.GetLength(1) < 3 || RawData.GetLength(0) == 0
            || RawData.GetLength(0) != CorrectedData.GetLength(0))
            return;

        var reference = ResolveReference();
        UpdateProjectionPlot(XYPlot, 0, 1, $"Bx ({Unit})", $"By ({Unit})", reference, showLegend: true);
        UpdateProjectionPlot(XZPlot, 0, 2, $"Bx ({Unit})", $"Bz ({Unit})", reference, showLegend: false);
        UpdateProjectionPlot(YZPlot, 1, 2, $"By ({Unit})", $"Bz ({Unit})", reference, showLegend: false);
        UpdateTotalFieldPlot(reference);
        UpdateResidualHistogram(reference);
    }

    private static double Magnitude(double[,] data, int i) =>
        Math.Sqrt(data[i, 0] * data[i, 0] + data[i, 1] * data[i, 1] + data[i, 2] * data[i, 2]);

    /// <summary>参考场强：显式给定时用给定值，否则用校正后总场均值。</summary>
    private double ResolveReference()
    {
        if (ReferenceFieldStrength > 0) return ReferenceFieldStrength;
        int n = CorrectedData!.GetLength(0);
        double sum = 0;
        for (int i = 0; i < n; i++) sum += Magnitude(CorrectedData, i);
        return sum / n;
    }

    /// <summary>
    /// 更新一个投影散点图（XY / XZ / YZ）
    /// </summary>
    private void UpdateProjectionPlot(WpfPlot wpfPlot, int axisA, int axisB,
        string xLabel, string yLabel, double reference, bool showLegend)
    {
        var plot = wpfPlot.Plot;
        plot.Clear();

        int n = RawData!.GetLength(0);
        var indices = GetDownsampleIndices(n);
        int count = indices.Length;

        var rawX = new double[count];
        var rawY = new double[count];
        var corX = new double[count];
        var corY = new double[count];
        for (int i = 0; i < count; i++)
        {
            int idx = indices[i];
            rawX[i] = RawData[idx, axisA];
            rawY[i] = RawData[idx, axisB];
            corX[i] = CorrectedData![idx, axisA];
            corY[i] = CorrectedData[idx, axisB];
        }

        var rawScatter = plot.Add.ScatterPoints(rawX, rawY);
        rawScatter.Color = RawColor.WithAlpha(170);
        rawScatter.MarkerSize = 3;
        rawScatter.LegendText = "原始";

        var corScatter = plot.Add.ScatterPoints(corX, corY);
        corScatter.Color = CorrectedColor.WithAlpha(200);
        corScatter.MarkerSize = 3;
        corScatter.LegendText = "校正后";

        // 参考圆：校正后的点应落在以原点为圆心、半径为参考场强的圆附近
        if (reference > 0 && double.IsFinite(reference))
        {
            const int circlePoints = 361;
            var circleX = new double[circlePoints];
            var circleY = new double[circlePoints];
            for (int i = 0; i < circlePoints; i++)
            {
                double angle = i * Math.PI / 180.0;
                circleX[i] = reference * Math.Cos(angle);
                circleY[i] = reference * Math.Sin(angle);
            }

            var circle = plot.Add.ScatterLine(circleX, circleY);
            circle.Color = ReferenceColor;
            circle.LineWidth = 1;
            circle.LinePattern = LinePattern.Dashed;
            circle.LegendText = "参考圆";
        }

        plot.Axes.AutoScale();
        plot.Axes.SquareUnits();
        plot.Axes.Bottom.Label.Text = xLabel;
        plot.Axes.Left.Label.Text = yLabel;
        plot.Legend.IsVisible = showLegend;

        wpfPlot.Refresh();
    }

    /// <summary>
    /// 更新总场时间序列对比图
    /// </summary>
    private void UpdateTotalFieldPlot(double reference)
    {
        var plot = TotalFieldPlot.Plot;
        plot.Clear();

        int n = RawData!.GetLength(0);
        var dsIndices = GetDownsampleIndices(n);
        var dsX = new double[dsIndices.Length];
        var dsRawY = new double[dsIndices.Length];
        var dsCorY = new double[dsIndices.Length];
        for (int i = 0; i < dsIndices.Length; i++)
        {
            int idx = dsIndices[i];
            dsX[i] = idx;
            dsRawY[i] = Magnitude(RawData, idx);
            dsCorY[i] = Magnitude(CorrectedData!, idx);
        }

        var rawLine = plot.Add.ScatterLine(dsX, dsRawY);
        rawLine.Color = RawColor;
        rawLine.LineWidth = 1.5f;
        rawLine.LegendText = "原始总场";

        var corLine = plot.Add.ScatterLine(dsX, dsCorY);
        corLine.Color = CorrectedColor;
        corLine.LineWidth = 1.5f;
        corLine.LegendText = "校正后总场";

        if (reference > 0 && double.IsFinite(reference))
        {
            var refLine = plot.Add.HorizontalLine(reference);
            refLine.Color = ReferenceColor;
            refLine.LinePattern = LinePattern.Dashed;
            refLine.LineWidth = 1;
            refLine.LegendText = ReferenceFieldStrength > 0 ? "参考场强" : "校正后均值";
        }

        plot.Axes.AutoScale();
        plot.Axes.Bottom.Label.Text = "样本序号";
        plot.Axes.Left.Label.Text = $"|B| ({Unit})";
        plot.Legend.IsVisible = true;

        TotalFieldPlot.Refresh();
    }

    /// <summary>
    /// 更新残差分布直方图：残差 = 校正后总场 − 参考场强。
    /// </summary>
    private void UpdateResidualHistogram(double reference)
    {
        var plot = ResidualPlot.Plot;
        plot.Clear();

        int n = CorrectedData!.GetLength(0);
        var residuals = new double[n];
        for (int i = 0; i < n; i++)
            residuals[i] = Magnitude(CorrectedData, i) - reference;

        const int binCount = 25;
        double minVal = residuals.Min(), maxVal = residuals.Max();
        double mean = residuals.Average();
        double range = maxVal - minVal;
        if (range < 1e-10) range = 1.0;

        double binWidth = range / binCount;
        var binCenters = new double[binCount];
        var binCounts = new double[binCount];
        for (int i = 0; i < binCount; i++)
            binCenters[i] = minVal + (i + 0.5) * binWidth;
        foreach (var r in residuals)
        {
            int bin = Math.Clamp((int)((r - minVal) / binWidth), 0, binCount - 1);
            binCounts[bin]++;
        }

        var bars = plot.Add.Bars(binCenters, binCounts);
        bars.Color = CorrectedColor.WithAlpha(210);
        foreach (var bar in bars.Bars)
        {
            bar.Size = binWidth * 0.85;
            bar.LineWidth = 0;
        }

        var meanLine = plot.Add.VerticalLine(mean);
        meanLine.Color = MeanColor;
        meanLine.LinePattern = LinePattern.Dashed;
        meanLine.LineWidth = 1;
        meanLine.LegendText = $"均值 {mean:F2}";

        plot.Axes.AutoScale();
        plot.Axes.Margins(bottom: 0);
        plot.Axes.Bottom.Label.Text = $"残差 ({Unit})";
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
