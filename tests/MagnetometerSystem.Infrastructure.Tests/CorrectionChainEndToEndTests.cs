using System.Globalization;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.Infrastructure.Tests;

/// <summary>
/// 一条完整的改正链：拟合 → 存入配置库 → 重新打开数据库读回 → 对已保存会话批量改正 → 存改正表 → 导出 CSV。
/// 其他用例直接 new 参数对象，这里用的是从 SQLite 读回的那一份，并用带交叉耦合的真实拟合矩阵。
/// </summary>
public sealed class CorrectionChainEndToEndTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "correction-chain-" + Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "chain.db");

    // 温度在 0，辅助电压在 3；磁场三轴按 X=1, Y=2, Z=4 映射，验证改正只动所选通道。
    private static readonly string[] Names = ["Temperature", "X", "Y", "Aux", "Z"];
    private static readonly string[] Units = ["°C", "nT", "nT", "V", "nT"];
    private static readonly int[] Magnetic = [1, 2, 4];
    private static readonly double[] Distortion = [1.03, 0.04, -0.02, 0.01, 0.97, 0.05, -0.03, 0.02, 1.01];
    private static readonly double[] TrueOffset = [350, -120, 80];
    private const double Field = 50000;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    private static double[] Distorted(int i, int count)
    {
        double z = 1 - 2.0 * (i + .5) / count, r = Math.Sqrt(1 - z * z), phi = i * Math.PI * (3 - Math.Sqrt(5));
        double[] b = [r * Math.Cos(phi) * Field, r * Math.Sin(phi) * Field, z * Field];
        return
        [
            Distortion[0] * b[0] + Distortion[1] * b[1] + Distortion[2] * b[2] + TrueOffset[0],
            Distortion[3] * b[0] + Distortion[4] * b[1] + Distortion[5] * b[2] + TrueOffset[1],
            Distortion[6] * b[0] + Distortion[7] * b[1] + Distortion[8] * b[2] + TrueOffset[2],
        ];
    }

    [Fact]
    public async Task FittedProfileReloadedFromSqlite_CorrectsSavedSessionAndCsvWithoutTouchingRawData()
    {
        // 1. 拟合并存入配置库。
        const int fitCount = 300;
        var fitData = new double[fitCount, 3];
        for (int i = 0; i < fitCount; i++)
        {
            var v = Distorted(i, fitCount);
            for (int axis = 0; axis < 3; axis++) fitData[i, axis] = v[axis];
        }
        var fit = new OrthogonalityCalculator().Calculate(fitData, referenceFieldStrength: Field, unit: "nT");
        Assert.True(fit.Success, fit.ErrorMessage);
        fit.Parameters.Name = "交叉耦合探头";
        var database = new DatabaseInitializer(DatabasePath);
        await database.InitializeAsync();
        await new SqliteCalibrationRepository(database).SaveOrthogonalityProfileAsync(fit.Parameters);

        // 2. 采集一段会话：原始值按协议顺序写入，温度和辅助通道随行变化。
        const int sessionCount = 120;
        var start = new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);
        var sent = Enumerable.Range(0, sessionCount).Select(i =>
        {
            var m = Distorted(i * 7 % sessionCount, sessionCount);
            return new double[] { 20 + i * .01, m[0], m[1], 1.5 + i * .001, m[2] };
        }).ToArray();
        string sessionId;
        using (var storage = new SqliteStorageService(database, new DataBus()))
        {
            sessionId = await storage.StartSessionAsync("改正链", new SensorConfig
            {
                Type = SensorType.Generic, ChannelCountOverride = Names.Length,
                ChannelNamesOverride = Names, ChannelUnitsOverride = Units
            }, new ConnectionConfig { Type = ConnectionType.Tcp });
            await storage.SaveReadingsAsync(sent.Select((values, i) => new MagnetometerReading
            {
                SessionId = sessionId, Timestamp = start.AddMilliseconds(i * 10), ChannelValues = (double[])values.Clone()
            }).ToList());
            await storage.EndSessionAsync(sessionId);
        }
        SqliteConnection.ClearAllPools();

        // 3. 重新打开数据库（相当于重启程序），读回配置并批量改正。
        var reopened = new DatabaseInitializer(DatabasePath);
        await reopened.InitializeAsync();
        var profile = await new SqliteCalibrationRepository(reopened).GetOrthogonalityProfileAsync(fit.Parameters.Id);
        Assert.NotNull(profile);
        Assert.Equal("nT", profile!.Unit);
        Assert.Equal(fit.Parameters.Offset, profile.Offset);
        Assert.Equal(fit.Parameters.CompensationMatrix, profile.CompensationMatrix);

        using var reopenedStorage = new SqliteStorageService(reopened, new DataBus());
        var stored = await reopenedStorage.GetReadingsAsync(sessionId);
        Assert.Equal(sessionCount, stored.Count);
        foreach (var unit in Magnetic.Select(i => Units[i])) profile.ValidateUnit(unit);
        var snapshot = new OrthogonalityCorrectionSnapshot(profile, null, Magnetic);
        await reopenedStorage.SaveCorrectedReadingsAsync(await snapshot.ApplyBatchAsync(new OrthogonalityCorrector(), stored));

        // 4. 改正表：数值等于手算 M * (raw - offset)，只动所选三通道，总场被拉回参考场强。
        var corrected = (await reopenedStorage.GetCorrectedReadingsAsync(sessionId, snapshot.VersionId))
            .ToDictionary(c => c.OriginalReadingId);
        Assert.Equal(sessionCount, corrected.Count);
        var m = profile.CompensationMatrix;
        var o = profile.Offset;
        var fields = new List<double>();
        foreach (var raw in stored)
        {
            var c = corrected[raw.Id];
            Assert.Equal(raw.Timestamp, c.Timestamp);
            Assert.True(c.IsOrthogonalityCorrected);
            double dx = raw.ChannelValues[1] - o[0], dy = raw.ChannelValues[2] - o[1], dz = raw.ChannelValues[4] - o[2];
            double[] expected =
            [
                raw.ChannelValues[0],
                m[0] * dx + m[1] * dy + m[2] * dz,
                m[3] * dx + m[4] * dy + m[5] * dz,
                raw.ChannelValues[3],
                m[6] * dx + m[7] * dy + m[8] * dz,
            ];
            for (int ch = 0; ch < expected.Length; ch++) Assert.Equal(expected[ch], c.CorrectedValues[ch], 1e-6);
            Assert.Equal(raw.ChannelValues[0], c.CorrectedValues[0]);
            Assert.Equal(raw.ChannelValues[3], c.CorrectedValues[3]);
            fields.Add(Math.Sqrt(c.CorrectedValues[1] * c.CorrectedValues[1]
                + c.CorrectedValues[2] * c.CorrectedValues[2] + c.CorrectedValues[4] * c.CorrectedValues[4]));
        }
        Assert.True(fields.Max() - fields.Min() < 1, $"改正后总场起伏 {fields.Max() - fields.Min()} nT");
        Assert.Equal(Field, fields.Average(), 1.0);

        // 5. 原始表逐值不变，顺序与发送顺序一致。
        var rawAfter = await reopenedStorage.GetReadingsAsync(sessionId);
        Assert.Equal(sent.Length, rawAfter.Count);
        for (int i = 0; i < sent.Length; i++)
        {
            Assert.Equal(sent[i], rawAfter[i].ChannelValues);
            Assert.False(rawAfter[i].IsOrthogonalityCorrected);
        }

        // 6. CSV：原始 + 改正双列与两张表逐值一致；纯原始导出不混入改正值。
        var bothPath = Path.Combine(_directory, "both.csv");
        var rawPath = Path.Combine(_directory, "raw.csv");
        var exporter = new CsvExporter(reopenedStorage);
        await exporter.ExportAsync(sessionId, bothPath, new ExportOptions
        {
            Source = ExportDataSource.RawAndCorrected, CorrectionProfileId = snapshot.VersionId, IncludeUnits = true
        });
        await exporter.ExportAsync(sessionId, rawPath, new ExportOptions { Source = ExportDataSource.Raw, IncludeUnits = true });

        var both = await File.ReadAllLinesAsync(bothPath);
        Assert.Equal(sessionCount + 1, both.Length);
        var header = SplitCsv(both[0]);
        for (int ch = 0; ch < Names.Length; ch++)
        {
            Assert.Equal($"{Names[ch]}_raw [{Units[ch]}]", header[1 + ch * 2]);
            Assert.Equal($"{Names[ch]}_corrected [{Units[ch]}]", header[2 + ch * 2]);
        }
        for (int row = 0; row < sessionCount; row++)
        {
            var cells = SplitCsv(both[row + 1]);
            var raw = rawAfter[row];
            var c = corrected[raw.Id];
            for (int ch = 0; ch < Names.Length; ch++)
            {
                Assert.Equal(raw.ChannelValues[ch], double.Parse(cells[1 + ch * 2], CultureInfo.InvariantCulture));
                Assert.Equal(c.CorrectedValues[ch], double.Parse(cells[2 + ch * 2], CultureInfo.InvariantCulture));
            }
            Assert.Equal(snapshot.VersionId, cells[^1]); // 版本键含逗号，必须被正确加引号。
        }

        var rawLines = await File.ReadAllLinesAsync(rawPath);
        Assert.Equal(sessionCount + 1, rawLines.Length);
        Assert.DoesNotContain("corrected", rawLines[0]);
        for (int row = 0; row < sessionCount; row++)
        {
            var cells = SplitCsv(rawLines[row + 1]);
            for (int ch = 0; ch < Names.Length; ch++)
                Assert.Equal(sent[row][ch], double.Parse(cells[1 + ch], CultureInfo.InvariantCulture));
        }
    }

    /// <summary>按 RFC 4180 拆一行：引号包裹的字段可含逗号，"" 表示一个引号。</summary>
    private static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(ch);
        }
        cells.Add(cell.ToString());
        return cells.ToArray();
    }
}
