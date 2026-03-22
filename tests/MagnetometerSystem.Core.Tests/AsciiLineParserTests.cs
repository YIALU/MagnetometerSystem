using System.Text;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;

namespace MagnetometerSystem.Core.Tests;

public class AsciiLineParserTests
{
    private static void FeedString(IDataParser parser, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        parser.Feed(bytes, 0, bytes.Length);
    }

    [Fact]
    public void TryParse_CommaSeparatedLine_ReturnsCorrectValues()
    {
        var parser = new AsciiLineParser();
        FeedString(parser, "12345.67,23456.78,34567.89\r\n");

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(3, reading.ChannelValues.Length);
        Assert.Equal(12345.67, reading.ChannelValues[0], 2);
        Assert.Equal(23456.78, reading.ChannelValues[1], 2);
        Assert.Equal(34567.89, reading.ChannelValues[2], 2);
    }

    [Fact]
    public void TryParse_TabDelimited_ReturnsCorrectValues()
    {
        var parser = new AsciiLineParser();
        FeedString(parser, "100.0\t200.0\t300.0\n");

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(3, reading.ChannelValues.Length);
        Assert.Equal(100.0, reading.ChannelValues[0], 2);
        Assert.Equal(200.0, reading.ChannelValues[1], 2);
        Assert.Equal(300.0, reading.ChannelValues[2], 2);
    }

    [Fact]
    public void TryParse_SpaceDelimited_ReturnsCorrectValues()
    {
        var parser = new AsciiLineParser();
        FeedString(parser, "1.5 2.5 3.5\n");

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(3, reading.ChannelValues.Length);
        Assert.Equal(1.5, reading.ChannelValues[0], 2);
        Assert.Equal(2.5, reading.ChannelValues[1], 2);
        Assert.Equal(3.5, reading.ChannelValues[2], 2);
    }

    [Fact]
    public void TryParse_IncompleteLine_ReturnsFalse()
    {
        var parser = new AsciiLineParser();
        FeedString(parser, "100.0,200.0,300.0");  // no newline

        bool result = parser.TryParse(out var reading);

        Assert.False(result);
        Assert.Null(reading);
    }

    [Fact]
    public void TryParse_NonNumericValues_SkipsNonNumericFields()
    {
        var parser = new AsciiLineParser();
        FeedString(parser, "abc,def,ghi\n");

        bool result = parser.TryParse(out var reading);

        // All fields are non-numeric, so values.Count == 0 => returns false
        Assert.False(result);
    }

    [Fact]
    public void TryParse_MixedNumericAndNonNumeric_ParsesOnlyNumeric()
    {
        var parser = new AsciiLineParser();
        FeedString(parser, "100.0,abc,300.0\n");

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        // Only numeric values are collected: 100.0 and 300.0
        Assert.Equal(2, reading.ChannelValues.Length);
        Assert.Equal(100.0, reading.ChannelValues[0], 2);
        Assert.Equal(300.0, reading.ChannelValues[1], 2);
    }

    [Fact]
    public void TryParse_ExpectedChannels_PadsWithZeros()
    {
        var parser = new AsciiLineParser(expectedChannels: 4);
        FeedString(parser, "10.0,20.0\n");

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(4, reading.ChannelValues.Length);
        Assert.Equal(10.0, reading.ChannelValues[0], 2);
        Assert.Equal(20.0, reading.ChannelValues[1], 2);
        Assert.Equal(0.0, reading.ChannelValues[2], 2);
        Assert.Equal(0.0, reading.ChannelValues[3], 2);
    }

    [Fact]
    public void TryParse_CommentLine_ReturnsFalse()
    {
        var parser = new AsciiLineParser();
        FeedString(parser, "# this is a comment\n");

        bool result = parser.TryParse(out var reading);

        Assert.False(result);
    }

    [Fact]
    public void Reset_ClearsBuffer_SubsequentParseReturnsFalse()
    {
        var parser = new AsciiLineParser();
        FeedString(parser, "100.0,200.0,300.0\r\n");
        parser.Reset();

        bool result = parser.TryParse(out var reading);

        Assert.False(result);
    }

    [Fact]
    public void TryParse_CustomDelimiterOnly_ParsesWithSpecifiedDelimiter()
    {
        // Use semicolon as delimiter (not in default set)
        var parser = new AsciiLineParser(delimiters: [';']);
        FeedString(parser, "1.0;2.0;3.0\n");

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(3, reading.ChannelValues.Length);
        Assert.Equal(1.0, reading.ChannelValues[0], 2);
        Assert.Equal(2.0, reading.ChannelValues[1], 2);
        Assert.Equal(3.0, reading.ChannelValues[2], 2);
    }
}
