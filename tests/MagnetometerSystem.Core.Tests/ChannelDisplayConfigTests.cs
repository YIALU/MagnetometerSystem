using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

public class ChannelDisplayConfigTests
{
    [Fact]
    public void PresetColors_CoverTwentyOneChannelsWithoutRepeat()
    {
        // 磁梯度数采卡-pt 是 21 通道。默认色按 index % Length 回绕，
        // 调色板短于通道数就会出现同色曲线（原来 8 色时 CH0/CH8/CH16 撞色）。
        Assert.True(ChannelDisplayConfig.PresetColors.Length >= 21);
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
    public void PresetColors_FirstEightUnchanged()
    {
        // 扩容不得打乱既有顺序：已保存的用户配置和既有截图依赖前 8 个色值。
        Assert.Equal(
            ["#FF0000FF", "#FFFF0000", "#FF008000", "#FFFF8C00",
             "#FF800080", "#FF00FFFF", "#FFFF00FF", "#FFB8860B"],
            ChannelDisplayConfig.PresetColors.Take(8));
    }

    [Fact]
    public void CreateDefaults_AssignsDistinctColorsToAllZdzChannels()
    {
        var protocol = ProtocolConfig.CreateZdzC08();

        var configs = ChannelDisplayConfig.CreateDefaults(
            protocol.DerivedChannelCount,
            [.. protocol.DerivedChannelNames]);

        Assert.Equal(21, configs.Length);
        Assert.Equal(21, configs.Select(c => c.ColorHex).Distinct().Count());
        Assert.Equal("X1", configs[0].Name);
        Assert.Equal("入水深度", configs[20].Name);
        Assert.All(configs, c => Assert.True(c.Visible));
    }
}
