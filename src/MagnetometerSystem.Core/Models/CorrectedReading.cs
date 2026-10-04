namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 存储校正结果，不覆盖原始数据。
/// 通过 OriginalReadingId 链接回原始读数。
/// </summary>
public class CorrectedReading
{
    /// <summary>数据库自增 ID</summary>
    public long Id { get; set; }

    /// <summary>原始读数 ID</summary>
    public long OriginalReadingId { get; set; }

    /// <summary>采集会话 ID</summary>
    public string SessionId { get; set; } = "";

    /// <summary>原始时间戳</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>改正版本键（配置、映射及参数指纹）；兼容旧记录中的单配置 ID。</summary>
    public string CorrectionProfileId { get; set; } = "";

    /// <summary>校正后的通道值</summary>
    public double[] CorrectedValues { get; set; } = [];

    /// <summary>校正后的总场强度</summary>
    public double? CorrectedTotalField { get; set; }

    /// <summary>是否已做正交度校正</summary>
    public bool IsOrthogonalityCorrected { get; set; }

    /// <summary>校正执行时间</summary>
    public DateTime CorrectedAt { get; set; }

    /// <summary>
    /// 从原始读数创建校正记录
    /// </summary>
    /// <param name="original">原始读数</param>
    /// <param name="correctedValues">校正后的通道值</param>
    /// <param name="correctionProfileId">改正版本键，或旧记录的单配置 ID</param>
    /// <returns>校正读数实例</returns>
    public static CorrectedReading FromOriginal(
        MagnetometerReading original,
        double[] correctedValues,
        string correctionProfileId)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(correctedValues);
        if (correctedValues.Length != original.ChannelValues.Length || correctedValues.Any(v => !double.IsFinite(v)))
            throw new ArgumentException("改正结果必须保留全部原始通道，并且只能包含有限数值。", nameof(correctedValues));
        var result = new CorrectedReading
        {
            OriginalReadingId = original.Id,
            SessionId = original.SessionId,
            Timestamp = original.Timestamp,
            CorrectionProfileId = correctionProfileId,
            CorrectedValues = (double[])correctedValues.Clone(),
            IsOrthogonalityCorrected = true,
            CorrectedAt = DateTime.UtcNow
        };

        // 仅兼容布局明确的旧三轴设备；任意协议必须显式定义总场计算通道。
        if ((original.SensorType == SensorType.TriaxialFluxgate && correctedValues.Length == 3) ||
            (original.SensorType == SensorType.DualTriaxialFluxgate && correctedValues.Length == 6))
        {
            result.CorrectedTotalField = Math.Sqrt(
                correctedValues[0] * correctedValues[0] +
                correctedValues[1] * correctedValues[1] +
                correctedValues[2] * correctedValues[2]);
        }

        return result;
    }
}
