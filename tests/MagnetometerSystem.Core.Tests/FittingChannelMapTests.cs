using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

public class FittingChannelMapTests
{
    private static int[]? Suggest(ProtocolConfig protocol, int groups) =>
        FittingChannelMap.Suggest(protocol.DerivedChannelNames, protocol.DerivedChannelUnits, groups);

    [Fact]
    public void Suggest_DataCardFullFrame_PicksMagneticComponentsNotGradientsOrAttitude()
    {
        var protocol = ProtocolConfig.CreateZdzC08();   // 21 通道：X1..Z2、ΔX..ΔZ（nT）、GPS、加速度、陀螺、磁力仪（设备单位）、深度
        Assert.Equal(new[] { 0, 1, 2 }, Suggest(protocol, 1));
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, Suggest(protocol, 2));
    }

    [Fact]
    public void Suggest_Cct5InterleavedOrder_GroupsByProbe()
    {
        // CCT-5 的通道顺序是 X1、X2、Y1、Y2、Z1、Z2、ADC6、ADC7：按顺序取前三个会得到 X1、X2、Y1。
        var protocol = ProtocolConfig.CreateCct5Gradiometer();
        Assert.Equal(new[] { 0, 2, 4 }, Suggest(protocol, 1));
        Assert.Equal(new[] { 0, 2, 4, 1, 3, 5 }, Suggest(protocol, 2));
    }

    [Theory]
    [InlineData(new[] { "Bz", "By", "Bx" }, new[] { 2, 1, 0 })]           // 名称识别轴，不按顺序
    [InlineData(new[] { "温度", "X", "Y", "Z" }, new[] { 1, 2, 3 })]       // 非磁场通道跳过
    [InlineData(new[] { "Mag_X", "Mag_Y", "Mag_Z" }, new[] { 0, 1, 2 })]
    public void Suggest_RecognisesAxisLetters(string[] names, int[] expected)
    {
        var units = names.Select(n => n == "温度" ? "°C" : "nT").ToArray();
        Assert.Equal(expected, FittingChannelMap.Suggest(names, units, 1));
    }

    [Fact]
    public void Suggest_WithoutAxisNames_OnlyFillsInOrderWhenTheCountIsExact()
    {
        Assert.Equal(new[] { 0, 1, 2 }, FittingChannelMap.Suggest(["CH0", "CH1", "CH2"], ["nT", "nT", "nT"], 1));
        Assert.Equal(new[] { 1, 2, 3 }, FittingChannelMap.Suggest(["T", "CH1", "CH2", "CH3"], ["°C", "uT", "µT", "uT"], 1));
        Assert.Equal(Enumerable.Range(0, 6), FittingChannelMap.Suggest(
            Enumerable.Range(0, 7).Select(i => $"CH{i}").ToArray(), ["nT", "nT", "nT", "nT", "nT", "nT", "°C"], 2));
        // 多于所需、少于所需或单位不同：不截取前缀，交给用户选择。
        Assert.Null(FittingChannelMap.Suggest(["CH0", "CH1", "CH2", "CH3"], ["nT", "nT", "nT", "nT"], 1));
        Assert.Null(FittingChannelMap.Suggest(["CH0", "CH1"], ["nT", "nT"], 1));
        Assert.Null(FittingChannelMap.Suggest(["CH0", "CH1", "CH2"], ["nT", "uT", "nT"], 1));
        Assert.Null(FittingChannelMap.Suggest(["CH0", "CH1", "CH2"], ["nT", "", "nT"], 1));
        Assert.Null(FittingChannelMap.Suggest(["X", "Y", "Z"], ["nT", "nT"], 1));
    }

    [Fact]
    public void Suggest_ProbeMustShareOneUnit()
    {
        Assert.Null(FittingChannelMap.Suggest(["X", "Y", "Z", "T"], ["nT", "uT", "nT", "nT"], 1));
        Assert.Equal(new[] { 3, 4, 5 }, FittingChannelMap.Suggest(
            ["X1", "Y1", "Z1", "X2", "Y2", "Z2"], ["nT", "uT", "nT", "nT", "nT", "nT"], 1));
    }

    [Fact]
    public void Validate_ReturnsUnitOrExplainsTheProblem()
    {
        string[] units = ["nT", "µT", "uT", "uT", "°C", "nT"];
        Assert.Equal("uT", FittingChannelMap.Validate([1, 2, 3], units, 1));
        Assert.Contains("各选一个", Assert.Throws<ArgumentException>(() => FittingChannelMap.Validate([1, 2, -1], units, 1)).Message);
        Assert.Contains("各选一个", Assert.Throws<ArgumentException>(() => FittingChannelMap.Validate([1, 2, 6], units, 1)).Message);
        Assert.Contains("各选一个", Assert.Throws<ArgumentException>(() => FittingChannelMap.Validate([1, 2, 3], units, 2)).Message);
        Assert.Contains("重复", Assert.Throws<ArgumentException>(() => FittingChannelMap.Validate([1, 2, 2], units, 1)).Message);
        Assert.Contains("单位", Assert.Throws<ArgumentException>(() => FittingChannelMap.Validate([0, 1, 2], units, 1)).Message);
        Assert.Contains("单位", Assert.Throws<ArgumentException>(() => FittingChannelMap.Validate([2, 3, 4], units, 1)).Message);
    }
}
