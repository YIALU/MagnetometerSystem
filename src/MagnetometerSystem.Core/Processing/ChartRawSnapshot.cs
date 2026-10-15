namespace MagnetometerSystem.Core.Processing;

/// <summary>
/// 实时图表缓冲中原始读数（校正前）的一份副本：时间（采集开始后的秒数）、按通道索引排列的原始值，以及通道名称和单位。
/// 区间统计与区间导出只用它，不用显示值。
/// </summary>
public sealed record ChartRawSnapshot(double[] Times, double[][] Channels, string[] Names, string[] Units);
