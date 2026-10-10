using System.Globalization;
using System.Text;
using MagnetometerSystem.Infrastructure.Export;

namespace MagnetometerSystem.Infrastructure.Tests;

public class IntervalCsvExporterTests
{
    private static readonly double[] Times = [0, 0.5, 1, 1.5];
    private static readonly double[][] Channels = [[10, 1.0 / 3, 0.1 + 0.2, double.NaN], [-1, -2, -3, -4]];

    private static string[] Lines(string[] names, string[] units, int start, int count)
    {
        var writer = new StringWriter { NewLine = "\n" };
        IntervalCsvExporter.Write(writer, Times, Channels, names, units, start, count);
        return writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public void WritesOnlyTheRequestedRowsWithRoundTripValues()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            // 小数点不随系统区域设置变成逗号。
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            Assert.Equal(
            [
                "ElapsedSeconds,\"Bx (nT)\",\"温度 (°C)\"",
                "0.5,0.3333333333333333,-2",
                "1,0.30000000000000004,-3",
                "1.5,NaN,-4",
            ], Lines(["Bx", "温度"], ["nT", "°C"], start: 1, count: 3));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void ChannelNamesAreQuotedWithInnerQuotesDoubled()
    {
        var header = Lines(["Bx, \"探头 A\"", "无单位"], ["nT"], start: 0, count: 0).Single();

        // 缺单位的通道写成空括号。
        Assert.Equal("ElapsedSeconds,\"Bx, \"\"探头 A\"\" (nT)\",\"无单位 ()\"", header);
    }

    [Fact]
    public async Task FileIsUtf8WithBomAndReplacesAnExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"interval-{Guid.NewGuid():N}.csv");
        try
        {
            await File.WriteAllTextAsync(path, new string('x', 4096));

            await IntervalCsvExporter.WriteFileAsync(path, Times, Channels, ["Bx", "By"], ["nT", "nT"], start: 0, count: 1);

            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            var nl = Environment.NewLine;
            Assert.Equal($"ElapsedSeconds,\"Bx (nT)\",\"By (nT)\"{nl}0,10,-1{nl}", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
