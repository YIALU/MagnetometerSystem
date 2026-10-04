using System.Security.Cryptography;
using System.Text.Json;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Calibration;

/// <summary>冻结一次批量改正的计算参数和通道映射；版本身份不随界面选择或配置编辑改变。</summary>
public sealed class OrthogonalityCorrectionSnapshot
{
    private readonly OrthogonalityParams _first;
    private readonly OrthogonalityParams? _second;
    private readonly int[] _firstChannels;
    private readonly int[]? _secondChannels;

    public string VersionId { get; }

    public OrthogonalityCorrectionSnapshot(OrthogonalityParams first, OrthogonalityParams? second,
        IReadOnlyList<int> firstChannels, IReadOnlyList<int>? secondChannels = null)
    {
        ArgumentNullException.ThrowIfNull(first);
        if ((second is null) != (secondChannels is null))
            throw new ArgumentException("第二组参数与通道映射必须同时提供。");
        _first = Copy(first);
        _second = second == null ? null : Copy(second);
        _firstChannels = firstChannels.ToArray();
        _secondChannels = secondChannels?.ToArray();
        // Apply 实际读取的数值只有 Offset 和 CompensationMatrix。名称、备注等不改变计算身份。
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            First = new { _first.Id, _first.Offset, _first.CompensationMatrix, Channels = _firstChannels },
            Second = _second == null ? null : new { _second.Id, _second.Offset, _second.CompensationMatrix, Channels = _secondChannels }
        })));
        static string Group(OrthogonalityParams profile, int[] channels) =>
            $"{Uri.EscapeDataString(profile.Id)}[{string.Join(",", channels)}]";
        VersionId = $"ortho-v1|{Group(_first, _firstChannels)}|{(_second == null ? "none" : Group(_second, _secondChannels!))}|{fingerprint}";
    }

    private static OrthogonalityParams Copy(OrthogonalityParams source)
    {
        source.Validate();
        return new OrthogonalityParams
        {
            Id = source.Id, Name = source.Name,
            Offset = (double[])source.Offset.Clone(),
            CompensationMatrix = (double[])source.CompensationMatrix.Clone()
        };
    }

    public async Task<IReadOnlyList<CorrectedReading>> ApplyBatchAsync(OrthogonalityCorrector corrector,
        IReadOnlyList<MagnetometerReading> readings, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = await corrector.ApplyBatchAsync(_first, _second, readings,
            _firstChannels, _secondChannels, progress, cancellationToken);
        return readings.Select((reading, i) => CorrectedReading.FromOriginal(
            reading, result.CorrectedReadings[i].ChannelValues, VersionId)).ToArray();
    }

    /// <summary>旧的纯配置 ID 原样保留，新版本显示两组 ID、映射和短指纹。</summary>
    public static string DisplayName(string versionId)
    {
        var parts = versionId.Split('|');
        if (parts.Length != 4 || parts[0] != "ortho-v1" || parts[3].Length != 64)
            return $"旧配置：{versionId}";
        var second = parts[2] == "none" ? "" : $" + 第二组 {Uri.UnescapeDataString(parts[2])}";
        return $"第一组 {Uri.UnescapeDataString(parts[1])}{second} · {parts[3][..12]}";
    }
}
