using System.Globalization;
using System.Text;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Storage;

namespace MagnetometerSystem.Infrastructure.Export;

/// <summary>按会话通道顺序导出 CSV，完成前不覆盖目标文件。</summary>
public class CsvExporter(IDataStorageService storageService) : IDataExporter
{
    public string Format => "CSV";

    public async Task ExportAsync(string sessionId, string filePath, ExportOptions options,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        if (options.StartTime > options.EndTime)
            throw new ArgumentException("导出起始时间不能晚于结束时间。");
        if (options.DecimalPlaces is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(options.DecimalPlaces));
        var session = (await storageService.GetSessionsAsync()).FirstOrDefault(s => s.Id == sessionId)
            ?? throw new ArgumentException($"会话不存在: {sessionId}");
        var indices = options.ChannelIndices is { Length: > 0 }
            ? options.ChannelIndices.ToArray() : Enumerable.Range(0, session.ChannelCount).ToArray();
        if (indices.Any(i => i < 0 || i >= session.ChannelCount) || indices.Distinct().Count() != indices.Length)
            throw new ArgumentException("导出通道索引越界或重复。");
        var readings = await storageService.GetReadingsAsync(sessionId, options.StartTime, options.EndTime);
        ct.ThrowIfCancellationRequested();
        var corrections = new Dictionary<long, CorrectedReading>();
        if (options.Source != ExportDataSource.Raw)
        {
            var corrected = await storageService.GetCorrectedReadingsAsync(sessionId, options.CorrectionProfileId);
            if (corrected.Select(r => r.CorrectionProfileId).Distinct().Skip(1).Any())
                throw new ArgumentException("存在多个改正版本，请指定导出参数配置。");
            foreach (var item in corrected.OrderBy(r => r.CorrectedAt).ThenBy(r => r.Id))
                corrections[item.OriginalReadingId] = item;
            if (readings.Any(r => !corrections.ContainsKey(r.Id)))
                throw new InvalidOperationException("所选区间未全部生成指定版本的改正结果。");
        }
        var fullPath = Path.GetFullPath(filePath);
        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(true)) { NewLine = "\r\n" })
            {
                if (options.IncludeHeader)
                    await writer.WriteLineAsync(BuildHeader(session, indices, options).AsMemory(), ct);
                for (var i = 0; i < readings.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    corrections.TryGetValue(readings[i].Id, out var corrected);
                    await writer.WriteLineAsync(BuildLine(readings[i], corrected, indices, options).AsMemory(), ct);
                    if ((i + 1) % 1000 == 0 && i + 1 < readings.Count)
                        progress?.Report((double)(i + 1) / readings.Count);
                }
                await writer.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            progress?.Report(1);
            ct.ThrowIfCancellationRequested();
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static string BuildHeader(SessionInfo session, int[] indices, ExportOptions options)
    {
        var fields = new List<string> { "Timestamp" };
        foreach (var index in indices)
        {
            var name = index < session.ChannelNames.Length ? session.ChannelNames[index] : $"CH{index}";
            var unit = options.IncludeUnits && index < session.ChannelUnits.Length && !string.IsNullOrEmpty(session.ChannelUnits[index])
                ? $" [{session.ChannelUnits[index]}]" : "";
            if (options.Source == ExportDataSource.RawAndCorrected)
            {
                fields.Add(name + "_raw" + unit);
                fields.Add(name + "_corrected" + unit);
            }
            else fields.Add(name + unit);
        }
        if (options.IncludeCalibratedData) fields.AddRange(["IsCalibrated", "IsOrthoCorrected"]);
        return string.Join(",", fields.Select(Escape));
    }

    private static string BuildLine(MagnetometerReading reading, CorrectedReading? corrected,
        int[] indices, ExportOptions options)
    {
        var rawValues = reading.OriginalChannelValues ?? reading.ChannelValues;
        if (options.Source != ExportDataSource.Corrected &&
            (reading.IsCalibrated || reading.IsOrthogonalityCorrected) && reading.OriginalChannelValues == null)
            throw new InvalidOperationException("历史记录只有处理后数据，不能将其标记为原始数据导出。");
        var fields = new List<string> { reading.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) };
        string Value(double[] values, int index)
        {
            if (index >= values.Length || !double.IsFinite(values[index]))
                throw new InvalidDataException("导出数据缺少通道或包含非有限数值。");
            return values[index].ToString(options.DecimalPlaces is int places ? $"F{places}" : "R", CultureInfo.InvariantCulture);
        }
        foreach (var index in indices)
        {
            fields.Add(Value(options.Source == ExportDataSource.Corrected ? corrected!.CorrectedValues : rawValues, index));
            if (options.Source == ExportDataSource.RawAndCorrected) fields.Add(Value(corrected!.CorrectedValues, index));
        }
        if (options.IncludeCalibratedData)
            fields.AddRange(["0", options.Source == ExportDataSource.Raw ? "0" : "1"]);
        return string.Join(",", fields.Select(Escape));
    }

    private static string Escape(string text) => text.IndexOfAny([',', '"', '\r', '\n']) >= 0
        ? '"' + text.Replace("\"", "\"\"") + '"' : text;
}
