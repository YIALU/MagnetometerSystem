using System.Globalization;
using System.Text;

namespace MagnetometerSystem.Infrastructure.Export;

/// <summary>实时图表“区间”页的导出：把曲线缓冲中的一段原始读数写成 CSV（UTF-8 带 BOM）。</summary>
public static class IntervalCsvExporter
{
    /// <summary>
    /// 写表头和第 <paramref name="start"/> 起的 <paramref name="count"/> 行数据。表头每个通道写成带引号的“名称 (单位)”，内部引号加倍；
    /// 时间与数值用不变区域性的往返格式（R），能读回同一个 double。
    /// </summary>
    public static void Write(TextWriter writer, double[] times, double[][] channels,
        IReadOnlyList<string> names, IReadOnlyList<string> units, int start, int count)
    {
        writer.Write("ElapsedSeconds");
        for (int ch = 0; ch < names.Count; ch++)
            writer.Write(",\"" + (names[ch] + " (" + units.ElementAtOrDefault(ch) + ")").Replace("\"", "\"\"") + "\"");
        writer.WriteLine();

        for (int i = start; i < start + count; i++)
        {
            writer.Write(times[i].ToString("R", CultureInfo.InvariantCulture));
            for (int ch = 0; ch < channels.Length; ch++)
                writer.Write("," + channels[ch][i].ToString("R", CultureInfo.InvariantCulture));
            writer.WriteLine();
        }
    }

    /// <summary>在后台线程写入文件，已有文件被覆盖。</summary>
    public static Task WriteFileAsync(string path, double[] times, double[][] channels,
        IReadOnlyList<string> names, IReadOnlyList<string> units, int start, int count) =>
        Task.Run(() =>
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            Write(writer, times, channels, names, units, start, count);
        });
}
