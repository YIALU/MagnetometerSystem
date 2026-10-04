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
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(firstGroup);
        // 仅对三轴和双三轴传感器有效
        if (reading.SensorType != SensorType.TriaxialFluxgate &&
            reading.SensorType != SensorType.DualTriaxialFluxgate)
            return reading;

        var expectedCount = reading.SensorType == SensorType.DualTriaxialFluxgate ? 6 : 3;
        if (reading.ChannelValues.Length != expectedCount)
            throw new ArgumentException("通道布局不明确，请显式选择要改正的三个通道。");
        return ApplyToReading(firstGroup, secondGroup, reading, [0, 1, 2],
            secondGroup != null && expectedCount == 6 ? [3, 4, 5] : null);
    }

    /// <summary>按显式索引改正三通道组，保留温度等未选中通道和最初原始值。</summary>
    public MagnetometerReading ApplyToReading(OrthogonalityParams firstGroup,
        OrthogonalityParams? secondGroup, MagnetometerReading reading,
        IReadOnlyList<int> firstChannels, IReadOnlyList<int>? secondChannels = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(firstGroup);
        firstGroup.Validate();
        ValidateChannels(firstChannels, reading.ChannelValues.Length);
        if ((secondGroup is null) != (secondChannels is null))
            throw new ArgumentException("第二组参数与通道映射必须同时提供。");
        if (secondChannels != null)
        {
            secondGroup!.Validate();
            ValidateChannels(secondChannels, reading.ChannelValues.Length);
            if (firstChannels.Intersect(secondChannels).Any())
                throw new ArgumentException("两组正交度通道不能重叠。");
        }
        var result = reading.DeepClone();
        result.OriginalChannelValues ??= (double[])reading.ChannelValues.Clone();
        ApplyGroup(firstGroup, firstChannels, result.ChannelValues);
        if (secondChannels != null)
            ApplyGroup(secondGroup!, secondChannels, result.ChannelValues);
        result.IsOrthogonalityCorrected = true;
        return result;
    }

    private static void ValidateChannels(IReadOnlyList<int> channels, int count)
    {
        if (channels is not { Count: 3 } || channels.Distinct().Count() != 3 ||
            channels.Any(i => i < 0 || i >= count))
            throw new ArgumentException("正交度改正需要三个不重复且有效的通道索引。");
    }

    private static void ApplyGroup(OrthogonalityParams parameters, IReadOnlyList<int> channels, double[] values)
    {
        var corrected = parameters.Apply(values[channels[0]], values[channels[1]], values[channels[2]]);
        for (var i = 0; i < 3; i++) values[channels[i]] = corrected[i];
    }

    public Task<BatchCorrectionResult> ApplyBatchAsync(OrthogonalityParams firstGroup,
        OrthogonalityParams? secondGroup, IReadOnlyList<MagnetometerReading> readings,
        IReadOnlyList<int> firstChannels, IReadOnlyList<int>? secondChannels = null,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var result = new List<MagnetometerReading>(readings.Count);
            foreach (var reading in readings)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(ApplyToReading(firstGroup, secondGroup, reading, firstChannels, secondChannels));
                progress?.Report(result.Count * 100 / readings.Count);
            }
            return new BatchCorrectionResult { CorrectedReadings = result, ProcessedCount = result.Count };
        }, cancellationToken);
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
        return await ApplyBatchAsync(parameters, null, readings, progress, cancellationToken);
    }

    /// <summary>
    /// 批量校正（双组，用于双三轴传感器；secondGroup 为 null 时等价于单组）
    /// </summary>
    public async Task<BatchCorrectionResult> ApplyBatchAsync(
        OrthogonalityParams firstGroup,
        OrthogonalityParams? secondGroup,
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
                results.Add(ApplyToReading(firstGroup, secondGroup, readings[i]));
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
