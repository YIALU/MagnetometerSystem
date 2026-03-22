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
