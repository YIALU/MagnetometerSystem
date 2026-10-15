using MagnetometerSystem.Core.Calibration;

namespace MagnetometerSystem.Core.Tests;

public class CalibrationCsvImporterTests
{
    private static void AssertSamples(IReadOnlyList<double[]> actual, params double[][] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], actual[i]);
    }

    [Fact]
    public void HeaderAliasesLocateAxesByNameInAnyOrderAndCase()
    {
        var result = CalibrationCsvImporter.Parse(["time,BZ,bx,by", "0.1,3,1,2", "0.2,6,4,5", "", "bad,row"], dual: false);

        AssertSamples(result.First, [1, 2, 3], [4, 5, 6]);
        Assert.Empty(result.Second);
        Assert.Equal(1, result.SkippedLines);   // 空行不计入跳过数
    }

    [Fact]
    public void ChannelNumberAliasesAreAcceptedAsHeaders()
    {
        var result = CalibrationCsvImporter.Parse(["CH2,CH0,CH1", "3,1,2"], dual: false);

        AssertSamples(result.First, [1, 2, 3]);
    }

    [Fact]
    public void HeaderWithoutAnAxisColumnSkipsEveryRow()
    {
        var result = CalibrationCsvImporter.Parse(["x,y,temp", "1,2,3", "4,5,6"], dual: false);

        Assert.Empty(result.First);
        Assert.Equal(2, result.SkippedLines);
    }

    [Fact]
    public void WithoutHeaderANonNumericFirstColumnIsTreatedAsTimestamp()
    {
        var result = CalibrationCsvImporter.Parse(["2026-10-10T08:00:00,1.5,-2.5,3e2", "1,2,3"], dual: false);

        AssertSamples(result.First, [1.5, -2.5, 300], [1, 2, 3]);
        Assert.Equal(0, result.SkippedLines);
    }

    [Fact]
    public void CommaSemicolonTabAndExponentsAreAccepted()
    {
        var result = CalibrationCsvImporter.Parse(["1;2;3", "4\t5\t6", "7, 8 ;\t9", "1e-3,-2E+2,.5"], dual: false);

        AssertSamples(result.First, [1, 2, 3], [4, 5, 6], [7, 8, 9], [0.001, -200, 0.5]);
        Assert.Equal(0, result.SkippedLines);
    }

    [Fact]
    public void FirstLineWithTooFewColumnsIsDataNotAHeader()
    {
        var result = CalibrationCsvImporter.Parse(["x,y", "1,2,3"], dual: false);

        AssertSamples(result.First, [1, 2, 3]);
        Assert.Equal(1, result.SkippedLines);
    }

    [Fact]
    public void DualWithoutHeaderNeedsSixColumnsAndKeepsTheFirstGroupWhenTheSecondIsInvalid()
    {
        var result = CalibrationCsvImporter.Parse(
            ["1,2,3,4,5,6", "1,2,3", "t,7,8,9,10,11,12", "13,14,15,x,17,18"], dual: true);

        AssertSamples(result.First, [1, 2, 3], [7, 8, 9], [13, 14, 15]);
        AssertSamples(result.Second, [4, 5, 6], [10, 11, 12]);
        Assert.Equal(1, result.SkippedLines);   // 只有 3 列的行在双三轴下整行跳过
    }

    [Fact]
    public void DualWithHeaderKeepsTheFirstGroupWhenSecondGroupColumnsAreMissing()
    {
        var result = CalibrationCsvImporter.Parse(["x1,y1,z1,x2,y2,z2", "1,2,3,4,5,6", "7,8,9"], dual: true);

        AssertSamples(result.First, [1, 2, 3], [7, 8, 9]);
        AssertSamples(result.Second, [4, 5, 6]);
        Assert.Equal(0, result.SkippedLines);
    }

    [Fact]
    public void RowsWithoutThreeValidValuesAreSkipped()
    {
        var result = CalibrationCsvImporter.Parse(["x,y,z", "1,2", "1,two,3"], dual: false);

        Assert.Empty(result.First);
        Assert.Equal(2, result.SkippedLines);
    }

    [Fact]
    public void EmptyInputYieldsNoSamples()
    {
        var result = CalibrationCsvImporter.Parse([], dual: true);

        Assert.Empty(result.First);
        Assert.Empty(result.Second);
        Assert.Equal(0, result.SkippedLines);
    }
}
