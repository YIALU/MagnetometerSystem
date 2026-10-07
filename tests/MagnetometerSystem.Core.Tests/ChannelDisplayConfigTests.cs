using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

public class ChannelDisplayConfigTests
{
    [Fact]
    public void PresetColors_AreTheEightValidatedColors()
    {
        // 默认色与界面色块一致：只用经色觉辨识校验的 8 色。
        Assert.Equal(
            ["#FF2A78D6", "#FFEB6834", "#FF1BAF7A", "#FFEDA100",
             "#FFE87BA4", "#FF008300", "#FF4A3AA7", "#FFE34948"],
            ChannelDisplayConfig.PresetColors);
    }

    [Fact]
    public void PresetColors_AreAllDistinct()
    {
        Assert.Equal(
            ChannelDisplayConfig.PresetColors.Length,
            ChannelDisplayConfig.PresetColors.Distinct().Count());
    }

    [Fact]
    public void PresetColors_AreAllParseable()
    {
        foreach (var hex in ChannelDisplayConfig.PresetColors)
        {
            var (a, _, _, _) = new ChannelDisplayConfig { ColorHex = hex }.ParseColor();
            // ParseColor 对无法识别的格式回落到不透明蓝，alpha 仍为 255，
            // 所以额外断言字符串本身的形状，避免哑失败。
            Assert.Equal(255, a);
            Assert.StartsWith("#", hex);
            Assert.Equal(9, hex.Length);
        }
    }

    [Fact]
    public void CreateDefaults_CyclesTheEightColorsAcrossAllZdzChannels()
    {
        var protocol = ProtocolConfig.CreateZdzC08();

        var configs = ChannelDisplayConfig.CreateDefaults(
            protocol.DerivedChannelCount,
            [.. protocol.DerivedChannelNames]);

        Assert.Equal(21, configs.Length);
        // 前 8 个通道颜色互不相同；之后按 8 色回绕，靠通道名区分。
        Assert.Equal(8, configs.Take(8).Select(c => c.ColorHex).Distinct().Count());
        Assert.Equal(configs[0].ColorHex, configs[8].ColorHex);
        Assert.Equal(configs[4].ColorHex, configs[20].ColorHex);
        Assert.Equal("X1", configs[0].Name);
        Assert.Equal("入水深度", configs[20].Name);
        Assert.All(configs, c => Assert.True(c.Visible));
    }
}
