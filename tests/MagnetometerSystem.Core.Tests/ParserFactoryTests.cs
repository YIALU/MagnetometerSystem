using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;

namespace MagnetometerSystem.Core.Tests;

public class ParserFactoryTests
{
    [Theory]
    [InlineData("ASCII_CSV")]
    [InlineData("ASCII_SPACE")]
    [InlineData("ASCII_AUTO")]
    public void Create_AsciiProtocolTypes_ReturnsAsciiLineParser(string protocolType)
    {
        var parser = ParserFactory.Create(protocolType);

        Assert.NotNull(parser);
        Assert.IsType<AsciiLineParser>(parser);
    }

    [Theory]
    [InlineData("BINARY_FLOAT")]
    [InlineData("BINARY_DOUBLE")]
    public void Create_BinaryProtocolTypes_ReturnsBinaryFrameParser(string protocolType)
    {
        var parser = ParserFactory.Create(protocolType);

        Assert.NotNull(parser);
        Assert.IsType<BinaryFrameParser>(parser);
    }

    [Fact]
    public void Create_UnknownProtocolType_ReturnsDefaultAsciiLineParser()
    {
        var parser = ParserFactory.Create("UNKNOWN_PROTOCOL");

        Assert.NotNull(parser);
        Assert.IsType<AsciiLineParser>(parser);
    }

    [Fact]
    public void Create_CaseInsensitive_WorksWithLowerCase()
    {
        var parser = ParserFactory.Create("ascii_csv");

        Assert.NotNull(parser);
        Assert.IsType<AsciiLineParser>(parser);
    }

    [Fact]
    public void Create_FromAsciiConfig_ReturnsConfigurableAsciiParser()
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Ascii,
            AsciiDelimiter = ",",
        };

        var parser = ParserFactory.Create(config);

        Assert.NotNull(parser);
        Assert.IsType<ConfigurableAsciiParser>(parser);
    }

    [Fact]
    public void Create_FromBinaryConfig_ReturnsConfigurableBinaryParser()
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Binary,
            FrameHeader = "AA55",
        };

        var parser = ParserFactory.Create(config);

        Assert.NotNull(parser);
        Assert.IsType<ConfigurableBinaryParser>(parser);
    }

    [Fact]
    public void Create_WithExpectedChannels_ParserRespectsChannelCount()
    {
        var parser = ParserFactory.Create("ASCII_CSV", expectedChannels: 4);

        Assert.NotNull(parser);
        Assert.IsType<AsciiLineParser>(parser);

        // Verify by feeding data and checking output
        var bytes = System.Text.Encoding.ASCII.GetBytes("1.0,2.0\n");
        parser.Feed(bytes, 0, bytes.Length);
        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(4, reading.ChannelValues.Length);
    }

    [Fact]
    public void Create_AllFactoryMethods_ReturnIDataParser()
    {
        IDataParser p1 = ParserFactory.Create("ASCII_CSV");
        IDataParser p2 = ParserFactory.Create(new ProtocolConfig { Category = ProtocolCategory.Ascii });

        Assert.NotNull(p1);
        Assert.NotNull(p2);
    }
}
