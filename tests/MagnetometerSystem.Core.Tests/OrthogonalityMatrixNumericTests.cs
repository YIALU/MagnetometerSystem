using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

/// <summary>
/// 带非对角元素的补偿矩阵：已有用例只用单位阵或对角阵，行列顺序弄反也能通过。
/// 这里用手算期望值锁定 corrected = M * (raw - offset) 的行优先约定，并用已知畸变检查拟合后改正真正生效。
/// </summary>
public class OrthogonalityMatrixNumericTests
{
    // 非对称矩阵：转置后结果不同，用来区分行优先与列优先。
    private static readonly double[] FullMatrix = [1.1, 0.2, -0.05, 0.03, 0.95, 0.1, -0.02, 0.04, 1.05];

    private static OrthogonalityParams Full(string id = "full") => new()
    {
        Id = id, Unit = "nT", Offset = [10, -20, 30], CompensationMatrix = (double[])FullMatrix.Clone()
    };

    [Fact]
    public void Apply_FullMatrix_MatchesHandComputedRowMajorProduct()
    {
        // raw - offset = (100, 100, -200)
        var result = Full().Apply(110, 80, -170);

        Assert.Equal(140.0, result[0], precision: 9); // 1.1*100 + 0.2*100 + (-0.05)*(-200)
        Assert.Equal(78.0, result[1], precision: 9);  // 0.03*100 + 0.95*100 + 0.1*(-200)
        Assert.Equal(-208.0, result[2], precision: 9); // -0.02*100 + 0.04*100 + 1.05*(-200)
        // 列优先（转置）会得到 117，确认测试能区分两种约定。
        Assert.NotEqual(117.0, result[0], precision: 6);
    }

    [Fact]
    public void Corrector_FullMatrixOnExplicitNonContiguousChannels_LeavesOtherChannelsAndOriginalsIntact()
    {
        // 通道 0 是温度，3 是辅助电压；磁场三轴按 X=4, Y=1, Z=2 的顺序映射。
        double[] raw = [25.5, 80, -170, 3.3, 110];
        var reading = new MagnetometerReading
        {
            SensorType = SensorType.Generic, Timestamp = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc),
            ChannelValues = (double[])raw.Clone()
        };

        var corrected = new OrthogonalityCorrector().ApplyToReading(Full(), null, reading, [4, 1, 2]);

        Assert.Equal(140.0, corrected.ChannelValues[4], precision: 9);
        Assert.Equal(78.0, corrected.ChannelValues[1], precision: 9);
        Assert.Equal(-208.0, corrected.ChannelValues[2], precision: 9);
        Assert.Equal(25.5, corrected.ChannelValues[0]);
        Assert.Equal(3.3, corrected.ChannelValues[3]);
        Assert.True(corrected.IsOrthogonalityCorrected);
        Assert.Equal(raw, corrected.OriginalChannelValues);
        Assert.Equal(raw, reading.ChannelValues); // 输入读数本身不被改写。
        Assert.Null(reading.OriginalChannelValues);
        Assert.Equal(reading.Timestamp, corrected.Timestamp);
    }

    [Fact]
    public async Task BatchDualGroups_EachGroupUsesItsOwnFullMatrix()
    {
        var second = new OrthogonalityParams
        {
            Id = "second", Unit = "nT", Offset = [1, 2, 3],
            CompensationMatrix = [0.9, -0.1, 0, 0.05, 1.0, 0.02, 0, 0.3, 1.2]
        };
        var readings = Enumerable.Range(0, 3).Select(i => new MagnetometerReading
        {
            SensorType = SensorType.Generic, Timestamp = DateTime.UnixEpoch.AddSeconds(i),
            ChannelValues = [110 + i, 80, -170, 11, 12 + i, 13, 20]
        }).ToList();

        var batch = await new OrthogonalityCorrector().ApplyBatchAsync(Full(), second, readings, [0, 1, 2], [3, 4, 5]);

        for (int i = 0; i < readings.Count; i++)
        {
            var raw = readings[i].ChannelValues;
            var values = batch.CorrectedReadings[i].ChannelValues;
            AssertClose(Multiply(FullMatrix, [raw[0] - 10, raw[1] + 20, raw[2] - 30]), values[0..3]);
            AssertClose(Multiply(second.CompensationMatrix, [raw[3] - 1, raw[4] - 2, raw[5] - 3]), values[3..6]);
            Assert.Equal(20, values[6]);
            Assert.Equal(raw, batch.CorrectedReadings[i].OriginalChannelValues);
        }
    }

    [Fact]
    public void FitThenApply_RemovesKnownCrossAxisDistortion()
    {
        // 真实场：半径 50000 nT 的球面；传感器 = 带交叉耦合的非正交矩阵 D 加偏移。
        double[] trueOffset = [350, -120, 80];
        double[] distortion = [1.03, 0.04, -0.02, 0.01, 0.97, 0.05, -0.03, 0.02, 1.01];
        const int count = 400;
        var data = new double[count, 3];
        var field = new double[count];
        for (int i = 0; i < count; i++)
        {
            var b = SpherePoint(i, count, 50000);
            var measured = Multiply(distortion, b);
            for (int axis = 0; axis < 3; axis++) data[i, axis] = measured[axis] + trueOffset[axis];
        }

        var result = new OrthogonalityCalculator().Calculate(data, referenceFieldStrength: 50000, unit: "nT");

        Assert.True(result.Success, result.ErrorMessage);
        var p = result.Parameters;
        Assert.Equal("nT", p.Unit);
        for (int axis = 0; axis < 3; axis++) Assert.Equal(trueOffset[axis], p.Offset[axis], 0.5);
        // 矩阵确实带非对角补偿（相对对角元），而不是退化成只做偏移或缩放。
        double diagonal = Math.Abs(p.CompensationMatrix[0]);
        Assert.Contains(new[] { 1, 2, 3, 5, 6, 7 }, i => Math.Abs(p.CompensationMatrix[i]) / diagonal > 1e-3);

        for (int i = 0; i < count; i++)
        {
            var c = p.Apply(data[i, 0], data[i, 1], data[i, 2]);
            field[i] = Math.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2]);
        }
        double rawSpread = Enumerable.Range(0, count).Select(i =>
            Math.Sqrt(Square(data[i, 0] - trueOffset[0]) + Square(data[i, 1] - trueOffset[1]) + Square(data[i, 2] - trueOffset[2])))
            .Aggregate((min: double.MaxValue, max: double.MinValue), (a, v) => (Math.Min(a.min, v), Math.Max(a.max, v)), a => a.max - a.min);
        double correctedSpread = field.Max() - field.Min();
        Assert.True(rawSpread > 1000, $"畸变数据总场起伏 {rawSpread} 太小，测试无意义");
        Assert.True(correctedSpread < 1, $"改正后总场起伏 {correctedSpread} nT，畸变未被消除");
        Assert.Equal(50000, field.Average(), 1.0); // 改正结果落在参考场强上，单位仍是 nT。
    }

    [Fact]
    public void FitWithoutReferenceField_KeepsInputUnitScaleForValuesAndResiduals()
    {
        // 向导默认不填参考场强。此前结果被缩到单位球：总场约 1 却标 nT，残差也小约 5 万倍，评级总是“优秀”。
        double[] trueOffset = [350, -120, 80];
        double[] distortion = [1.03, 0.04, -0.02, 0.01, 0.97, 0.05, -0.03, 0.02, 1.01];
        const int count = 400;
        var rng = new Random(7);
        var data = new double[count, 3];
        for (int i = 0; i < count; i++)
        {
            var measured = Multiply(distortion, SpherePoint(i, count, 50000));
            // 约 ±30 nT 的测量噪声，让残差有真实量级。
            for (int axis = 0; axis < 3; axis++) data[i, axis] = measured[axis] + trueOffset[axis] + (rng.NextDouble() - .5) * 60;
        }

        var result = new OrthogonalityCalculator().Calculate(data, unit: "nT");

        Assert.True(result.Success, result.ErrorMessage);
        var p = result.Parameters;
        double meanRawRadius = Enumerable.Range(0, count).Average(i =>
            Math.Sqrt(Square(data[i, 0] - p.Offset[0]) + Square(data[i, 1] - p.Offset[1]) + Square(data[i, 2] - p.Offset[2])));
        double meanCorrected = Enumerable.Range(0, count).Average(i =>
        {
            var c = p.Apply(data[i, 0], data[i, 1], data[i, 2]);
            return Math.Sqrt(c[0] * c[0] + c[1] * c[1] + c[2] * c[2]);
        });
        Assert.Equal(meanRawRadius, meanCorrected, meanRawRadius * 1e-6);
        Assert.InRange(meanCorrected, 48000, 52000);
        // 残差按 nT 计：噪声约 17 nT 标准差，不能被缩成 1e-3 量级。
        Assert.InRange(result.Quality.ResidualStd, 5, 60);
        Assert.Equal(p.ResidualStd, result.Quality.ResidualStd);
    }

    private static double[] SpherePoint(int i, int count, double radius)
    {
        double z = 1 - 2.0 * (i + .5) / count, r = Math.Sqrt(1 - z * z), phi = i * Math.PI * (3 - Math.Sqrt(5));
        return [r * Math.Cos(phi) * radius, r * Math.Sin(phi) * radius, z * radius];
    }

    private static double Square(double v) => v * v;

    private static double[] Multiply(double[] rowMajor, double[] v) =>
    [
        rowMajor[0] * v[0] + rowMajor[1] * v[1] + rowMajor[2] * v[2],
        rowMajor[3] * v[0] + rowMajor[4] * v[1] + rowMajor[5] * v[2],
        rowMajor[6] * v[0] + rowMajor[7] * v[1] + rowMajor[8] * v[2],
    ];

    private static void AssertClose(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i], 1e-9);
    }
}
