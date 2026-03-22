using System.Text;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;

namespace MagnetometerSystem.Core.Tests;

public class ConfigurableAsciiParserTests
{
    private static void FeedString(IDataParser parser, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        parser.Feed(bytes, 0, bytes.Length);
    }

    [Fact]
    public void TryParse_CustomCommaDelimiter_ParsesCorrectly()
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Ascii,
            AsciiDelimiter = ",",
        };
        var parser = new ConfigurableAsciiParser(config);
        FeedString(parser, "100.5,200.25,300.75\n");

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(3, reading.ChannelValues.Length);
        Assert.Equal(100.5, reading.ChannelValues[0], 2);
        Assert.Equal(200.25, reading.ChannelValues[1], 2);
        Assert.Equal(300.75, reading.ChannelValues[2], 2);
    }

    [Fact]
    public void TryParse_SemicolonDelimiter_ParsesCorrectly()
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Ascii,
            AsciiDelimiter = ";",
        };
        var parser = new ConfigurableAsciiParser(config);
        FeedString(parser, "1.0;2.0;3.0\n");

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(3, reading.ChannelValues.Length);
        Assert.Equal(1.0, reading.ChannelValues[0], 2);
        Assert.Equal(2.0, reading.ChannelValues[1], 2);
        Assert.Equal(3.0, reading.ChannelValues[2], 2);
    }

    [Fact]
    public void TryParse_FieldMappingWithScaleAndOffset_AppliesTransformation()
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Ascii,
            AsciiDelimiter = ",",
            FieldMappings =
            [
                new FieldMapping { Name = "X", ChannelIndex = 0, ByteOffset = 0, Scale = 2.0, Offset = 10.0 },
                new FieldMapping { Name = "Y", ChannelIndex = 1, ByteOffset = 1, Scale = 0.5, Offset = -5.0 },
            ]
        };
        var parser = new ConfigurableAsciiParser(config);
        FeedString(parser, "100.0,200.0\n");

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(2, reading.ChannelValues.Length);
        // val * Scale + Offset: 100.0 * 2.0 + 10.0 = 210.0
        Assert.Equal(210.0, reading.ChannelValues[0], 2);
        // 200.0 * 0.5 + (-5.0) = 95.0
        Assert.Equal(95.0, reading.ChannelValues[1], 2);
    }

    [Fact]
    public void TryParse_FieldMappingColumnReordering_MapsCorrectColumns()
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Ascii,
            AsciiDelimiter = ",",
            FieldMappings =
            [
                // Map column 2 to channel 0, column 0 to channel 1
                new FieldMapping { Name = "Z->Ch0", ChannelIndex = 0, ByteOffset = 2 },
                new FieldMapping { Name = "X->Ch1", ChannelIndex = 1, ByteOffset = 0 },
            ]
        };
        var parser = new ConfigurableAsciiParser(config);
        FeedString(parser, "10.0,20.0,30.0\n");

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(2, reading.ChannelValues.Length);
        Assert.Equal(30.0, reading.ChannelValues[0], 2);  // column 2 => channel 0
        Assert.Equal(10.0, reading.ChannelValues[1], 2);  // column 0 => channel 1
    }

    [Fact]
    public void TryParse_HasHeader_SkipsFirstLine()
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Ascii,
            AsciiDelimiter = ",",
            AsciiHasHeader = true,
        };
        var parser = new ConfigurableAsciiParser(config);
        FeedString(parser, "X,Y,Z\n50.0,60.0,70.0\n");

        // First call should skip the header line
        bool result1 = parser.TryParse(out var reading1);
        Assert.False(result1);

        // Second call should parse the data
        bool result2 = parser.TryParse(out var reading2);
        Assert.True(result2);
        Assert.NotNull(reading2);
        Assert.Equal(50.0, reading2.ChannelValues[0], 2);
    }

    [Fact]
    public void TryParse_CommentLine_ReturnsFalse()
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Ascii,
            AsciiDelimiter = ",",
        };
        var parser = new ConfigurableAsciiParser(config);
        FeedString(parser, "# comment\n");

        bool result = parser.TryParse(out var reading);

        Assert.False(result);
    }

    [Fact]
    public void TryParse_NoFieldMappings_ParsesAllNumericFields()
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Ascii,
            AsciiDelimiter = ",",
            // No FieldMappings - should parse all fields in order
        };
        var parser = new ConfigurableAsciiParser(config);
        FeedString(parser, "11.0,22.0,33.0,44.0\n");

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(4, reading.ChannelValues.Length);
        Assert.Equal(11.0, reading.ChannelValues[0], 2);
        Assert.Equal(44.0, reading.ChannelValues[3], 2);
    }

    [Fact]
    public void Constructor_NullConfig_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigurableAsciiParser(null!));
    }
}
