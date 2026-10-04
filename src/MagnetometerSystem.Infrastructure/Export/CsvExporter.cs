using System.Text;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Storage;

namespace MagnetometerSystem.Infrastructure.Export;

/// <summary>
/// CSV 格式数据导出器
/// </summary>
public class CsvExporter : IDataExporter
{
    private readonly IDataStorageService _storageService;

    public string Format => "CSV";

    public CsvExporter(IDataStorageService storageService)
    {
        _storageService = storageService;
    }

    public async Task ExportAsync(
        string sessionId,
        string filePath,
        ExportOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // 1. 获取会话信息以确定通道配置
        var sessions = await _storageService.GetSessionsAsync();
        ct.ThrowIfCancellationRequested();
        var session = sessions.FirstOrDefault(s => s.Id == sessionId)
            ?? throw new ArgumentException($"会话不存在: {sessionId}");

        // 2. 确定要导出的通道
        var channelIndices = options.ChannelIndices?.Length > 0
            ? options.ChannelIndices
            : Enumerable.Range(0, session.ChannelCount).ToArray();
        var channelNames = session.ChannelNames;

        // 3. 获取数据（用于进度计算）
        var allReadings = await _storageService.GetReadingsAsync(
            sessionId, options.StartTime, options.EndTime);
        ct.ThrowIfCancellationRequested();
        var totalCount = allReadings.Count;

        // 4. 流式写入
        try
        {
            // UTF-8 with BOM, CRLF 换行
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new UTF8Encoding(true))
            {
                NewLine = "\r\n"
            };

            // 写入头行
            if (options.IncludeHeader)
            {
                var header = BuildHeaderLine(channelNames, channelIndices, options);
                await writer.WriteLineAsync(header.AsMemory(), ct);
            }

            // 写入数据行
            var written = 0;
            foreach (var reading in allReadings)
            {
                ct.ThrowIfCancellationRequested();

                var line = BuildDataLine(reading, channelIndices, options);
                await writer.WriteLineAsync(line.AsMemory(), ct);

                written++;
                if (written % 1000 == 0 && written < totalCount)
                {
                    progress?.Report((double)written / totalCount);
                }
            }

            // Empty sessions and the last row use the same cancellation/cleanup path.
            await writer.FlushAsync(ct);
            ct.ThrowIfCancellationRequested();
            progress?.Report(1.0);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            // 取消时删除不完整文件
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            throw;
        }
    }

    private static string BuildHeaderLine(
        string[] channelNames, int[] channelIndices, ExportOptions options)
    {
        var parts = new List<string> { "Timestamp" };

        foreach (var idx in channelIndices)
        {
            parts.Add(idx < channelNames.Length ? channelNames[idx] : $"CH{idx}");
        }

        if (options.IncludeCalibratedData)
        {
            parts.Add("IsCalibrated");
            parts.Add("IsOrthoCorrected");
        }

        return string.Join(",", parts);
    }

    private static string BuildDataLine(
        MagnetometerReading reading, int[] channelIndices, ExportOptions options)
    {
        var sb = new StringBuilder();

        // 时间戳 - ISO 8601 round-trip format
        // 简洁格式：年-月-日 时:分:秒.毫秒（本地时间，DB 已统一在读取时转为本地）
        sb.Append(reading.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture));

        // 通道值
        foreach (var idx in channelIndices)
        {
            sb.Append(',');
            if (idx < reading.ChannelValues.Length)
            {
                sb.Append(reading.ChannelValues[idx].ToString("R")); // Round-trip 全精度
            }
            // 超出范围则输出空
        }

        // 校准状态列
        if (options.IncludeCalibratedData)
        {
            sb.Append(',');
            sb.Append(reading.IsCalibrated ? '1' : '0');
            sb.Append(',');
            sb.Append(reading.IsOrthogonalityCorrected ? '1' : '0');
        }

        return sb.ToString();
    }
}
