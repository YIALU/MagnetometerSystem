using System.Text.Json;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Infrastructure.Export;

namespace MagnetometerSystem.Infrastructure.Tests;

public class OrthogonalityProfileExporterTests
{
    [Fact]
    public void CsvQuotesNamesAndSerialsPerRfc4180()
    {
        var profile = new OrthogonalityParams
        {
            Name = "探头 \"A\", 第 2 组", SensorSerial = "SN-1\n备用", Unit = "nT", SampleCount = 48,
            Offset = [1.5, -2, 3], CompensationMatrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
        };
        var csv = OrthogonalityProfileExporter.BuildCsv(profile);
        var rows = ParseCsv(csv);
        Assert.Equal(2, rows.Count);
        Assert.Equal(19, rows[0].Count);
        Assert.Equal(19, rows[1].Count); // 引号、逗号与换行都留在字段内，后续数值不错位
        Assert.Equal(profile.Name, rows[1][0]);
        Assert.Equal(profile.SensorSerial, rows[1][1]);
        Assert.Equal("1.5", rows[1][7]);
        Assert.Equal("1", rows[1][18]);
    }

    [Fact]
    public void CsvWritesCanonicalUnitOptionalResidualsAndRoundTripNumbers()
    {
        var profile = new OrthogonalityParams
        {
            Name = "P", Unit = " µT ", SampleCount = 12, ResidualStd = 0.1 + 0.2,
            Offset = [1.0 / 3, 0, -2.5], CompensationMatrix = [1.0000001, 0, 0, 0, 1, 0, 0, 0, 1],
        };
        var fields = ParseCsv(OrthogonalityProfileExporter.BuildCsv(profile))[1];

        Assert.Equal("uT", fields[3]);
        Assert.Equal("12", fields[4]);
        Assert.Equal("", fields[5]);                      // 未记录的残差均值留空
        Assert.Equal("0.30000000000000004", fields[6]);   // 往返格式，不丢精度
        Assert.Equal(1.0 / 3, double.Parse(fields[7], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("1.0000001", fields[10]);
    }

    [Fact]
    public void JsonIsIndentedAndRoundTripsTheProfileWithItsUnit()
    {
        var profile = new OrthogonalityParams
        {
            Name = "探头 A", SensorSerial = "SN-1", Unit = "mT", SampleCount = 48, ResidualStd = 1e-6,
            Offset = [0.5, -1, 2], CompensationMatrix = [1, 0.01, 0, 0, 1, 0, 0, 0, 1],
        };
        var json = OrthogonalityProfileExporter.BuildJson(profile);

        Assert.Contains("\n  \"Name\"", json.ReplaceLineEndings("\n"));
        Assert.DoesNotContain("UnitDisplay", json);
        var back = JsonSerializer.Deserialize<OrthogonalityParams>(json)!;
        Assert.Equal(profile.Id, back.Id);
        Assert.Equal("探头 A", back.Name);
        Assert.Equal("SN-1", back.SensorSerial);
        Assert.Equal("mT", back.Unit);
        Assert.Equal(48, back.SampleCount);
        Assert.Equal(1e-6, back.ResidualStd);
        Assert.Equal(profile.Offset, back.Offset);
        Assert.Equal(profile.CompensationMatrix, back.CompensationMatrix);
    }

    // '/' 与 '\0' 在 Windows 和 Linux 上都不能出现在文件名里；其余非法字符随系统而定，这里不依赖。
    [Theory]
    [InlineData("探头 A", "探头 A")]
    [InlineData("  a/b  ", "ab")]
    [InlineData("/ name /", "name")]
    [InlineData("", "profile")]
    [InlineData("   ", "profile")]
    [InlineData("//\0", "profile")]
    public void FileNameDropsCharactersTheSystemRejects(string name, string expected) =>
        Assert.Equal(expected, OrthogonalityProfileExporter.SanitizeFileName(name));

    [Fact]
    public void FileNameIsCappedAtEightyCharacters() =>
        Assert.Equal(new string('x', 80), OrthogonalityProfileExporter.SanitizeFileName(new string('x', 100)));

    /// <summary>按 RFC 4180 读取 CSV（引号内的逗号、换行与加倍引号）。</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = new List<string>();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row); }
        return rows;
    }
}
