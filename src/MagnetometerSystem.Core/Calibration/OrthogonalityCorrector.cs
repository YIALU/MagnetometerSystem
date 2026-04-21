using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Calibration;

/// <summary>
/// 正交度校正应用器。
/// 负责将补偿参数应用于单条或批量磁力读数。
/// 无状态，线程安全。
/// </summary>
public class OrthogonalityCorrector
{
    /// <summary>
    /// 对单条读数应用正交度校正（单组参数，用于三轴传感器）
    /// </summary>
    /// <param name="parameters">正交度参数</param>
    /// <param name="reading">原始读数</param>
    /// <returns>校正后的新读数实例</returns>
    public MagnetometerReading ApplyToReading(
        OrthogonalityParams parameters, MagnetometerReading reading)
    {
        return ApplyToReading(parameters, null, reading);
    }

    /// <summary>
    /// 对单条读数应用正交度校正（双三轴，两组独立参数）
    /// </summary>
    /// <param name="firstGroup">第一组三轴的正交度参数</param>
    /// <param name="secondGroup">第二组三轴的正交度参数（仅双三轴传感器使用）</param>
    /// <param name="reading">原始读数</param>
    /// <returns>校正后的新读数实例</returns>
    public MagnetometerReading ApplyToReading(
        OrthogonalityParams firstGroup, OrthogonalityParams? secondGroup,
        MagnetometerReading reading)
    {
        // 仅对三轴和双三轴传感器有效
        if (reading.SensorType != SensorType.TriaxialFluxgate &&
            reading.SensorType != SensorType.DualTriaxialFluxgate)
            return reading;

        if (reading.ChannelValues.Length < 3)
            return reading;

        var values = (double[])reading.ChannelValues.Clone();

        // 第一组三轴 (通道 0, 1, 2)
        var c1 = firstGroup.Apply(values[0], values[1], values[2]);
        values[0] = c1[0];
        values[1] = c1[1];
        values[2] = c1[2];

        // 双三轴第二组 (通道 3, 4, 5)
        if (reading.SensorType == SensorType.DualTriaxialFluxgate
            && values.Length >= 6 && secondGroup != null)
        {
            var c2 = secondGroup.Apply(values[3], values[4], values[5]);
            values[3] = c2[0];
            values[4] = c2[1];
            values[5] = c2[2];
        }

        // 创建新的不可变实例，不修改原始 reading
        return new MagnetometerReading
        {
            Id = reading.Id,
            Timestamp = reading.Timestamp,
            SessionId = reading.SessionId,
            SensorType = reading.SensorType,
            ChannelValues = values,
            IsCalibrated = reading.IsCalibrated,
            IsOrthogonalityCorrected = true
        };
    }

    /// <summary>
    /// 批量校正（异步，支持进度报告和取消）
    /// </summary>
    public async Task<BatchCorrectionResult> ApplyBatchAsync(
        OrthogonalityParams parameters,
        IReadOnlyList<MagnetometerReading> readings,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<MagnetometerReading>(readings.Count);
            for (int i = 0; i < readings.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(ApplyToReading(parameters, readings[i]));
                progress?.Report((i + 1) * 100 / readings.Count);
            }
            return new BatchCorrectionResult
            {
                CorrectedReadings = results,
                ProcessedCount = results.Count
            };
        }, cancellationToken);
    }
}

/// <summary>
/// 批量校正结果
/// </summary>
public class BatchCorrectionResult
{
    /// <summary>校正后的读数列表</summary>
    public IReadOnlyList<MagnetometerReading> CorrectedReadings { get; set; } = [];

    /// <summary>已处理的读数数量</summary>
    public int ProcessedCount { get; set; }
}
