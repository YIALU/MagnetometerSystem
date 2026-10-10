using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Processing;

/// <summary>
/// 图表的一帧：视图模型按当前数据与设置算好，绘图器只按它画，不再读取视图模型的数据或设置。
/// 曲线都是显示值（加显示偏移、滤波、降采样之后）；统计表、十字准线和区间统计用的原始值不在这里。
/// </summary>
/// <param name="IsMultiPlot">多图模式：每个通道一张图，见 <paramref name="Panels"/>；否则所有曲线画在一张图上，见 <paramref name="Curves"/>。</param>
/// <param name="Axes">坐标轴范围与网格；为 null 表示时间窗口内还没有数据，没有新内容可画。</param>
/// <param name="Units">单图的单位轴，按顺序第一个在左侧、其余在右侧，温度单位排在最后。</param>
/// <param name="Curves">单图的曲线，按绘制顺序：协议通道按通道索引，然后是计算通道。</param>
/// <param name="Panels">多图的各张图，按显示顺序：可见的协议通道按通道列表的顺序，然后是计算通道。</param>
public sealed record ChartFrame(bool IsMultiPlot, ChartAxes? Axes,
    IReadOnlyList<string> Units, IReadOnlyList<ChartCurve> Curves, IReadOnlyList<ChartPanel> Panels)
{
    /// <summary>时间窗口内还没有数据。</summary>
    public static ChartFrame NoData(bool isMultiPlot) => new(isMultiPlot, null, [], [], []);
}

/// <summary>坐标轴范围与网格：自动滚动时 X 轴取 [XMin, XMax]；Y 轴自动缩放，或固定为 [YMin, YMax]。时间为采集开始后的秒数。</summary>
public sealed record ChartAxes(double XMin, double XMax, bool AutoScroll, bool AutoScaleY, double YMin, double YMax, bool ShowGrid);

/// <summary>一条曲线。</summary>
/// <param name="Xs">时间（采集开始后的秒数）。</param>
/// <param name="Ys">显示值。</param>
/// <param name="Color">ARGB 颜色；null 表示用绘图库的默认配色（通道还没有显示配置时）。</param>
/// <param name="LineWidth">线宽。</param>
/// <param name="Legend">单图的图例文字；null 表示不设图例（多图）。</param>
/// <param name="Unit">单图中这条曲线所在的单位轴（见 <see cref="ChartFrame.Units"/>）；null 表示默认的左轴。</param>
public sealed record ChartCurve(double[] Xs, double[] Ys, (byte A, byte R, byte G, byte B)? Color,
    float LineWidth, string? Legend, string? Unit);

/// <summary>多图模式下的一张图。</summary>
/// <param name="AxisLabel">左轴标题：名称和单位。</param>
/// <param name="Curve">曲线；协议通道在时间窗口内缺数据时为 null，这时只画坐标轴。</param>
/// <param name="Statistics">右上角的显示窗口统计（按显示值算）；没有曲线时为 null。</param>
/// <param name="Channel">这张图对应的协议通道，十字准线只读这个通道；计算通道为 null，十字准线读全部可见通道。</param>
public sealed record ChartPanel(string AxisLabel, ChartCurve? Curve, string? Statistics, ChannelDisplayConfig? Channel);

/// <summary>
/// 曲线上的叠加层：区间阴影、拖动预览和十字准线位置（采集开始后的秒数），没有时为 null。
/// 只画在曲线上，不影响数据、保存或统计。
/// </summary>
public sealed record ChartOverlay((double Start, double End)? Interval, (double Start, double End)? Drag, double? Crosshair);
