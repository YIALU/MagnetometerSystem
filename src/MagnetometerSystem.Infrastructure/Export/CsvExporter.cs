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
        // 1. 获取会话信息以确定通道配置
        var sessions = await _storageService.GetSessionsAsync();
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
        var totalCount = allReadings.Count;

        if (totalCount == 0)
        {
            // 写入空文件（仅头行）
            await WriteEmptyFileAsync(filePath, channelNames, channelIndices, options);
            progress?.Report(1.0);
            return;
        }

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
                await writer.WriteLineAsync(header);
            }

            // 写入数据行
            var written = 0;
            foreach (var reading in allReadings)
            {
                ct.ThrowIfCancellationRequested();

                var line = BuildDataLine(reading, channelIndices, options);
                await writer.WriteLineAsync(line);

                written++;
                if (written % 1000 == 0)
                {
                    progress?.Report((double)written / totalCount);
                }
            }

            progress?.Report(1.0);
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

    private static async Task WriteEmptyFileAsync(
        string filePath,
        string[] channelNames,
        int[] channelIndices,
        ExportOptions options)
    {
        using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(true))
        {
            NewLine = "\r\n"
        };

        if (options.IncludeHeader)
        {
            var header = BuildHeaderLine(channelNames, channelIndices, options);
            await writer.WriteLineAsync(header);
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

        parts.Add("TotalField");

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
        sb.Append(reading.Timestamp.ToString("O"));

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

        // TotalField
        sb.Append(',');
        if (reading.TotalField.HasValue)
        {
            sb.Append(reading.TotalField.Value.ToString("R"));
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
