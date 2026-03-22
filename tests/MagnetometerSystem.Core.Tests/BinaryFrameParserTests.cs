using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;

namespace MagnetometerSystem.Core.Tests;

public class BinaryFrameParserTests
{
    /// <summary>
    /// Build a valid binary frame: [0xAA, 0x55, dataLen, ...floatData..., checksum, 0x0D]
    /// Checksum = XOR of all bytes from header through data (indices 0..2+dataLen-1).
    /// </summary>
    private static byte[] BuildFloatFrame(params float[] values)
    {
        int dataLen = values.Length * 4;
        // frame: header(2) + length(1) + data(dataLen) + checksum(1) + tail(1)
        int frameLen = 2 + 1 + dataLen + 1 + 1;
        var frame = new byte[frameLen];
        frame[0] = 0xAA;
        frame[1] = 0x55;
        frame[2] = (byte)dataLen;

        for (int i = 0; i < values.Length; i++)
        {
            var bytes = BitConverter.GetBytes(values[i]);
            Array.Copy(bytes, 0, frame, 3 + i * 4, 4);
        }

        // XOR checksum over header + length + data
        byte checksum = 0;
        for (int i = 0; i < 2 + 1 + dataLen; i++)
            checksum ^= frame[i];
        frame[frameLen - 2] = checksum;
        frame[frameLen - 1] = 0x0D;

        return frame;
    }

    private static byte[] BuildDoubleFrame(params double[] values)
    {
        int dataLen = values.Length * 8;
        int frameLen = 2 + 1 + dataLen + 1 + 1;
        var frame = new byte[frameLen];
        frame[0] = 0xAA;
        frame[1] = 0x55;
        frame[2] = (byte)dataLen;

        for (int i = 0; i < values.Length; i++)
        {
            var bytes = BitConverter.GetBytes(values[i]);
            Array.Copy(bytes, 0, frame, 3 + i * 8, 8);
        }

        byte checksum = 0;
        for (int i = 0; i < 2 + 1 + dataLen; i++)
            checksum ^= frame[i];
        frame[frameLen - 2] = checksum;
        frame[frameLen - 1] = 0x0D;

        return frame;
    }

    [Fact]
    public void TryParse_ValidFloatFrame_ReturnsCorrectValues()
    {
        var parser = new BinaryFrameParser();
        var frame = BuildFloatFrame(100.5f, 200.25f, 300.75f);
        parser.Feed(frame, 0, frame.Length);

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(3, reading.ChannelValues.Length);
        Assert.Equal(100.5, reading.ChannelValues[0], 1);
        Assert.Equal(200.25, reading.ChannelValues[1], 2);
        Assert.Equal(300.75, reading.ChannelValues[2], 2);
    }

    [Fact]
    public void TryParse_ValidDoubleFrame_ReturnsCorrectValues()
    {
        var parser = new BinaryFrameParser(useDouble: true);
        var frame = BuildDoubleFrame(12345.6789, 23456.7891, 34567.8912);
        parser.Feed(frame, 0, frame.Length);

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(3, reading.ChannelValues.Length);
        Assert.Equal(12345.6789, reading.ChannelValues[0], 4);
        Assert.Equal(23456.7891, reading.ChannelValues[1], 4);
        Assert.Equal(34567.8912, reading.ChannelValues[2], 4);
    }

    [Fact]
    public void TryParse_InvalidChecksum_ReturnsFalse()
    {
        var parser = new BinaryFrameParser();
        var frame = BuildFloatFrame(100.0f, 200.0f, 300.0f);
        // Corrupt the checksum
        frame[^2] ^= 0xFF;
        parser.Feed(frame, 0, frame.Length);

        bool result = parser.TryParse(out var reading);

        Assert.False(result);
    }

    [Fact]
    public void TryParse_IncompleteFrame_ReturnsFalse()
    {
        var parser = new BinaryFrameParser();
        var frame = BuildFloatFrame(100.0f, 200.0f, 300.0f);
        // Feed only the first half
        int halfLen = frame.Length / 2;
        parser.Feed(frame, 0, halfLen);

        bool result = parser.TryParse(out var reading);

        Assert.False(result);
    }

    [Fact]
    public void TryParse_MultipleFrames_ParsesBothSequentially()
    {
        var parser = new BinaryFrameParser();
        var frame1 = BuildFloatFrame(10.0f, 20.0f, 30.0f);
        var frame2 = BuildFloatFrame(40.0f, 50.0f, 60.0f);

        // Concatenate both frames
        var combined = new byte[frame1.Length + frame2.Length];
        Array.Copy(frame1, 0, combined, 0, frame1.Length);
        Array.Copy(frame2, 0, combined, frame1.Length, frame2.Length);
        parser.Feed(combined, 0, combined.Length);

        // Parse first frame
        bool result1 = parser.TryParse(out var reading1);
        Assert.True(result1);
        Assert.NotNull(reading1);
        Assert.Equal(10.0, reading1.ChannelValues[0], 1);

        // Parse second frame
        bool result2 = parser.TryParse(out var reading2);
        Assert.True(result2);
        Assert.NotNull(reading2);
        Assert.Equal(40.0, reading2.ChannelValues[0], 1);

        // No more frames
        bool result3 = parser.TryParse(out var reading3);
        Assert.False(result3);
    }

    [Fact]
    public void TryParse_GarbageBeforeHeader_SkipsToValidFrame()
    {
        var parser = new BinaryFrameParser();
        var frame = BuildFloatFrame(99.0f);
        // Prepend garbage bytes
        var garbage = new byte[] { 0x01, 0x02, 0x03, 0xFF };
        var combined = new byte[garbage.Length + frame.Length];
        Array.Copy(garbage, 0, combined, 0, garbage.Length);
        Array.Copy(frame, 0, combined, garbage.Length, frame.Length);
        parser.Feed(combined, 0, combined.Length);

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(99.0, reading.ChannelValues[0], 1);
    }

    [Fact]
    public void TryParse_InvalidTail_ReturnsFalse()
    {
        var parser = new BinaryFrameParser();
        var frame = BuildFloatFrame(100.0f, 200.0f, 300.0f);
        // Corrupt the tail byte
        frame[^1] = 0xFF;
        parser.Feed(frame, 0, frame.Length);

        bool result = parser.TryParse(out var reading);

        Assert.False(result);
    }

    [Fact]
    public void Reset_ClearsBuffer()
    {
        var parser = new BinaryFrameParser();
        var frame = BuildFloatFrame(100.0f);
        parser.Feed(frame, 0, frame.Length);
        parser.Reset();

        bool result = parser.TryParse(out var reading);

        Assert.False(result);
    }
}
