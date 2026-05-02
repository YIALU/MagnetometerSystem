using MagnetometerSystem.Core.Models;
using System.Collections.ObjectModel;

namespace MagnetometerSystem.Core.Tests;

/// <summary>
/// 验证 ChannelDisplayConfig.ChannelIndex 在集合重排（拖拽）后保持不变
/// 这是多图表拖拽错位修复的核心不变量：物理通道索引不随显示位置改变
/// </summary>
public class ChannelReorderTests
{
    [Fact]
    public void ChannelIndex_IsPreservedAfterMove()
    {
        // Arrange: 创建 3 个通道配置，ChannelIndex 与初始位置对应
        var configs = new ObservableCollection<ChannelDisplayConfig>
        {
            new() { Name = "CH0", ChannelIndex = 0, Visible = true },
            new() { Name = "CH1", ChannelIndex = 1, Visible = true },
            new() { Name = "CH2", ChannelIndex = 2, Visible = true },
        };

        // Act: 模拟拖拽 — 将索引 0 的项移到位置 2（从第一移到最后）
        var item = configs[0];
        configs.RemoveAt(0);
        configs.Insert(2, item);

        // Assert: 显示顺序变了，但 ChannelIndex 不变
        Assert.Equal("CH1", configs[0].Name);
        Assert.Equal(1, configs[0].ChannelIndex);

        Assert.Equal("CH2", configs[1].Name);
        Assert.Equal(2, configs[1].ChannelIndex);

        Assert.Equal("CH0", configs[2].Name);
        Assert.Equal(0, configs[2].ChannelIndex); // 关键：移动后 ChannelIndex 仍为 0
    }

    [Fact]
    public void ChannelIndex_IsPreservedAfterMultipleMoves()
    {
        // Arrange
        var configs = new ObservableCollection<ChannelDisplayConfig>
        {
            new() { Name = "Bx", ChannelIndex = 0 },
            new() { Name = "By", ChannelIndex = 1 },
            new() { Name = "Bz", ChannelIndex = 2 },
            new() { Name = "Total", ChannelIndex = 3 },
        };

        // Act: 多次拖拽操作
        // 1. 把 Bz (index 2) 移到最前
        var bz = configs[2];
        configs.RemoveAt(2);
        configs.Insert(0, bz);

        // 2. 把 Total (现在在 index 3) 移到 index 1
        var total = configs[3];
        configs.RemoveAt(3);
        configs.Insert(1, total);

        // Assert: 所有 ChannelIndex 均未随位置变化
        Assert.Equal(2, configs[0].ChannelIndex); // Bz
        Assert.Equal(3, configs[1].ChannelIndex); // Total
        Assert.Equal(0, configs[2].ChannelIndex); // Bx
        Assert.Equal(1, configs[3].ChannelIndex); // By
    }

    [Fact]
    public void ChannelIndex_CanBeUsedToLookUpDataIndependentlyOfPosition()
    {
        // 验证修复逻辑：用 config.ChannelIndex 作为 channelData 下标
        var configs = new ObservableCollection<ChannelDisplayConfig>
        {
            new() { Name = "CH_B", ChannelIndex = 1, Visible = true },
            new() { Name = "CH_A", ChannelIndex = 0, Visible = true },
        };

        // 模拟 channelData: channel 0 全为 100，channel 1 全为 200
        var channelData = new double[][]
        {
            Enumerable.Repeat(100.0, 10).ToArray(),
            Enumerable.Repeat(200.0, 10).ToArray(),
        };

        // 按 configs 顺序取数据（修复后的逻辑）
        var results = new List<double>();
        foreach (var config in configs.Where(c => c.Visible))
        {
            int ch = config.ChannelIndex;
            results.Add(channelData[ch][0]);
        }

        // 第一个显示通道是 CH_B (ChannelIndex=1)，应取 200
        Assert.Equal(200.0, results[0]);
        // 第二个是 CH_A (ChannelIndex=0)，应取 100
        Assert.Equal(100.0, results[1]);
    }
}
