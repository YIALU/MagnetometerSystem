using System.Globalization;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Processing;

namespace MagnetometerSystem.Core.Tests;

public class CrosshairReadoutTests
{
    private static readonly double[] Times = [0, 1, 2];
    private static readonly double[][] Raw = [[10, 11, 12], [20.5, 21.5, 22.5], [30]];

    private static ChannelDisplayConfig Channel(int index, string name, string unit) =>
        new() { ChannelIndex = index, Name = name, Unit = unit };

    private static string? Format(double t, params ChannelDisplayConfig[] channels)
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            return CrosshairReadout.Format(Times, Raw, t, channels);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void ShowsTheNearestPointsRawValuesInChannelOrder()
    {
        var text = Format(1.4, Channel(1, "Bz", "nT"), Channel(0, "温度", ""));

        Assert.Equal("1.000 s（原始值）\nBz  21.5 nT\n温度  11", text);
    }

    [Theory]
    [InlineData(0.5, "1.000")]    // 与前后两点等距时取后一点
    [InlineData(0.49, "0.000")]
    [InlineData(2, "2.000")]
    public void PicksTheClosestTime(double t, string expectedTime) =>
        Assert.StartsWith($"{expectedTime} s", Format(t, Channel(0, "Bx", "nT")));

    [Theory]
    [InlineData(-0.1)]
    [InlineData(2.1)]
    public void OutsideTheDataRangeThereIsNoReadout(double t) =>
        Assert.Null(Format(t, Channel(0, "Bx", "nT")));

    [Fact]
    public void ChannelsWithoutDataAtThatPointAreSkipped()
    {
        var text = Format(1, Channel(2, "短", "nT"), Channel(5, "缺", "nT"), Channel(0, "Bx", "nT"));

        Assert.Equal("1.000 s（原始值）\nBx  11 nT", text);
    }
}
