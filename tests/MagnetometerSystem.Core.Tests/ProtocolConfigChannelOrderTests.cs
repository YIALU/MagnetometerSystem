using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

/// <summary>
/// 验证 ProtocolConfig.DerivedChannelNames 始终按 ChannelIndex 升序返回。
/// 用户在协议编辑器里上下调整 DataField 段顺序时，段的列表顺序会变但 ChannelIndex 不变；
/// parser 把值写入 values[ChannelIndex]（物理槽位），因此通道名必须跟着 ChannelIndex 走，
/// 否则图表标题与波形错位、数据库 JSON 名值错配。
/// </summary>
public class ProtocolConfigChannelOrderTests
{
    [Fact]
    public void DerivedChannelNames_Segments_OrderedByChannelIndex_WhenListOrderDiffers()
    {
        // Arrange: 段的列表顺序 (Z, X, Y) 与 ChannelIndex 顺序 (X=0, Y=1, Z=2) 不一致
        // 模拟用户把 Z 段上移到第一位
        var config = new ProtocolConfig
        {
            Segments =
            {
                new() { Type = SegmentType.Header, Name = "帧头", ByteCount = 2, FixedHexValue = "AA55" },
                new() { Type = SegmentType.DataField, Name = "Z", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 2 },
                new() { Type = SegmentType.DataField, Name = "X", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 0 },
                new() { Type = SegmentType.DataField, Name = "Y", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 1 },
            }
        };

        // Act
        var names = config.DerivedChannelNames;

        // Assert: 按 ChannelIndex 升序，而非列表顺序
        Assert.Equal(new[] { "X", "Y", "Z" }, names);
    }

    [Fact]
    public void DerivedChannelNames_FieldMappings_OrderedByChannelIndex_WhenListOrderDiffers()
    {
        // Arrange: 旧 FieldMapping 模式，列表顺序与 ChannelIndex 不一致
        var config = new ProtocolConfig
        {
            FieldMappings =
            {
                new() { Name = "Z", ChannelIndex = 2 },
                new() { Name = "X", ChannelIndex = 0 },
                new() { Name = "Y", ChannelIndex = 1 },
            }
        };

        // Act
        var names = config.DerivedChannelNames;

        // Assert
        Assert.Equal(new[] { "X", "Y", "Z" }, names);
    }

    [Fact]
    public void DerivedChannelNames_UnchangedOrder_RemainsStable()
    {
        // Arrange: 列表顺序 == ChannelIndex 顺序（未调过顺序的旧协议），应保持不变（向后兼容）
        var config = new ProtocolConfig
        {
            Segments =
            {
                new() { Type = SegmentType.DataField, Name = "X", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 0 },
                new() { Type = SegmentType.DataField, Name = "Y", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 1 },
                new() { Type = SegmentType.DataField, Name = "Z", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 2 },
            }
        };

        // Act
        var names = config.DerivedChannelNames;

        // Assert
        Assert.Equal(new[] { "X", "Y", "Z" }, names);
    }
}
