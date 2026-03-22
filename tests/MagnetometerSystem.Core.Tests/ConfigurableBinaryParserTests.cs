using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;

namespace MagnetometerSystem.Core.Tests;

public class ConfigurableBinaryParserTests
{
    #region Legacy FieldMapping mode

    [Fact]
    public void TryParseLegacy_ValidFrame_ReturnsCorrectValues()
    {
        // Frame format: [AA 55] [lengthByte] [3x double data] [xor checksum] [0D]
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Binary,
            FrameHeader = "AA55",
            FrameTail = "0D",
            HasLengthByte = true,
            LengthByteCount = 1,
            Checksum = ChecksumType.Xor,
            ChecksumStartOffset = 0,
            FieldMappings =
            [
                new FieldMapping { Name = "X", ChannelIndex = 0, ByteOffset = 0, DataType = FieldDataType.Float },
                new FieldMapping { Name = "Y", ChannelIndex = 1, ByteOffset = 4, DataType = FieldDataType.Float },
                new FieldMapping { Name = "Z", ChannelIndex = 2, ByteOffset = 8, DataType = FieldDataType.Float },
            ],
        };
        var parser = new ConfigurableBinaryParser(config);

        var frame = BuildLegacyFrame(config, 100.5f, 200.25f, 300.75f);
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
    public void TryParseLegacy_InvalidChecksum_ReturnsFalse()
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Binary,
            FrameHeader = "AA55",
            FrameTail = "0D",
            HasLengthByte = true,
            LengthByteCount = 1,
            Checksum = ChecksumType.Xor,
            ChecksumStartOffset = 0,
            FieldMappings =
            [
                new FieldMapping { Name = "X", ChannelIndex = 0, ByteOffset = 0, DataType = FieldDataType.Float },
            ],
        };
        var parser = new ConfigurableBinaryParser(config);

        var frame = BuildLegacyFrame(config, 100.0f);
        // Corrupt checksum (second to last byte)
        frame[^2] ^= 0xFF;
        parser.Feed(frame, 0, frame.Length);

        bool result = parser.TryParse(out var reading);

        Assert.False(result);
    }

    [Fact]
    public void TryParseLegacy_IncompleteFrame_ReturnsFalse()
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Binary,
            FrameHeader = "AA55",
            FrameTail = "0D",
            HasLengthByte = true,
            LengthByteCount = 1,
            Checksum = ChecksumType.None,
            FieldMappings =
            [
                new FieldMapping { Name = "X", ChannelIndex = 0, ByteOffset = 0, DataType = FieldDataType.Float },
            ],
        };
        var parser = new ConfigurableBinaryParser(config);

        // Only feed header bytes
        parser.Feed([0xAA, 0x55], 0, 2);

        bool result = parser.TryParse(out var reading);

        Assert.False(result);
    }

    [Fact]
    public void TryParseLegacy_ScaleAndOffset_AppliesTransformation()
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Binary,
            FrameHeader = "AA55",
            FrameTail = "",
            HasLengthByte = true,
            LengthByteCount = 1,
            Checksum = ChecksumType.None,
            FieldMappings =
            [
                new FieldMapping
                {
                    Name = "X", ChannelIndex = 0, ByteOffset = 0,
                    DataType = FieldDataType.Float, Scale = 2.0, Offset = 10.0,
                },
            ],
        };
        var parser = new ConfigurableBinaryParser(config);

        // Build frame: [AA 55] [04] [float data]
        var floatBytes = BitConverter.GetBytes(50.0f);
        var frame = new byte[2 + 1 + 4]; // header + length + data (no checksum, no tail)
        frame[0] = 0xAA;
        frame[1] = 0x55;
        frame[2] = 4; // data length
        Array.Copy(floatBytes, 0, frame, 3, 4);
        parser.Feed(frame, 0, frame.Length);

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        // 50.0 * 2.0 + 10.0 = 110.0
        Assert.Equal(110.0, reading.ChannelValues[0], 1);
    }

    #endregion

    #region Segment mode

    [Fact]
    public void TryParseSegments_ValidFrame_ReturnsCorrectValues()
    {
        var config = CreateSegmentConfig(FieldDataType.Float);
        var parser = new ConfigurableBinaryParser(config);

        var frame = BuildSegmentFrame(config, 10.5f, 20.25f, 30.75f);
        parser.Feed(frame, 0, frame.Length);

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        Assert.Equal(3, reading.ChannelValues.Length);
        Assert.Equal(10.5, reading.ChannelValues[0], 1);
        Assert.Equal(20.25, reading.ChannelValues[1], 2);
        Assert.Equal(30.75, reading.ChannelValues[2], 2);
    }

    [Fact]
    public void TryParseSegments_InvalidChecksum_ReturnsFalse()
    {
        var config = CreateSegmentConfig(FieldDataType.Float);
        var parser = new ConfigurableBinaryParser(config);

        var frame = BuildSegmentFrame(config, 10.0f, 20.0f, 30.0f);
        // Find and corrupt checksum byte (second-to-last byte before tail)
        frame[^2] ^= 0xFF;
        parser.Feed(frame, 0, frame.Length);

        bool result = parser.TryParse(out var reading);

        Assert.False(result);
    }

    [Fact]
    public void TryParseSegments_InvalidTail_ReturnsFalse()
    {
        var config = CreateSegmentConfig(FieldDataType.Float);
        var parser = new ConfigurableBinaryParser(config);

        var frame = BuildSegmentFrame(config, 10.0f, 20.0f, 30.0f);
        frame[^1] = 0xFF; // corrupt tail
        parser.Feed(frame, 0, frame.Length);

        bool result = parser.TryParse(out var reading);

        Assert.False(result);
    }

    [Fact]
    public void TryParseSegments_WithScaleOffset_AppliesTransformation()
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Binary,
            Segments =
            [
                new FrameSegment { Type = SegmentType.Header, Name = "H", ByteCount = 2, FixedHexValue = "AA55" },
                new FrameSegment
                {
                    Type = SegmentType.DataField, Name = "X", ByteCount = 4,
                    DataType = FieldDataType.Float, ChannelIndex = 0, Scale = 3.0, Offset = 5.0,
                },
                new FrameSegment { Type = SegmentType.Tail, Name = "T", ByteCount = 1, FixedHexValue = "0D" },
            ],
        };
        config.ComputeSegmentOffsets();
        var parser = new ConfigurableBinaryParser(config);

        // Build frame manually: [AA 55] [float(10.0)] [0D]
        var floatBytes = BitConverter.GetBytes(10.0f);
        var frame = new byte[2 + 4 + 1];
        frame[0] = 0xAA;
        frame[1] = 0x55;
        Array.Copy(floatBytes, 0, frame, 2, 4);
        frame[^1] = 0x0D;
        parser.Feed(frame, 0, frame.Length);

        bool result = parser.TryParse(out var reading);

        Assert.True(result);
        Assert.NotNull(reading);
        // 10.0 * 3.0 + 5.0 = 35.0
        Assert.Equal(35.0, reading.ChannelValues[0], 1);
    }

    [Fact]
    public void Constructor_NullConfig_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ConfigurableBinaryParser(null!));
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Build a legacy-mode binary frame: [header] [length] [float data...] [checksum] [tail]
    /// </summary>
    private static byte[] BuildLegacyFrame(ProtocolConfig config, params float[] values)
    {
        var headerBytes = config.FrameHeaderBytes;
        var tailBytes = config.FrameTailBytes;
        int dataLen = values.Length * 4;
        bool hasChecksum = config.Checksum != ChecksumType.None;

        int frameLen = headerBytes.Length
            + (config.HasLengthByte ? config.LengthByteCount : 0)
            + dataLen
            + (hasChecksum ? 1 : 0)
            + tailBytes.Length;

        var frame = new byte[frameLen];
        int pos = 0;

        // Header
        Array.Copy(headerBytes, 0, frame, pos, headerBytes.Length);
        pos += headerBytes.Length;

        // Length byte
        if (config.HasLengthByte)
        {
            frame[pos] = (byte)dataLen;
            pos += config.LengthByteCount;
        }

        // Data
        for (int i = 0; i < values.Length; i++)
        {
            var bytes = BitConverter.GetBytes(values[i]);
            Array.Copy(bytes, 0, frame, pos, 4);
            pos += 4;
        }

        // Checksum
        if (hasChecksum)
        {
            byte checksum = 0;
            for (int i = config.ChecksumStartOffset; i < pos; i++)
            {
                checksum = config.Checksum switch
                {
                    ChecksumType.Xor => (byte)(checksum ^ frame[i]),
                    ChecksumType.Sum8 => (byte)(checksum + frame[i]),
                    _ => checksum,
                };
            }
            frame[pos] = checksum;
            pos++;
        }

        // Tail
        Array.Copy(tailBytes, 0, frame, pos, tailBytes.Length);

        return frame;
    }

    /// <summary>
    /// Create a segment-mode config with XOR checksum, header AA55, tail 0D, 3 float channels.
    /// </summary>
    private static ProtocolConfig CreateSegmentConfig(FieldDataType dataType)
    {
        int byteCount = FrameSegment.GetByteCountForDataType(dataType);
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Binary,
            Segments =
            [
                new FrameSegment { Type = SegmentType.Header, Name = "H", ByteCount = 2, FixedHexValue = "AA55" },
                new FrameSegment { Type = SegmentType.DataField, Name = "X", ByteCount = byteCount, DataType = dataType, ChannelIndex = 0 },
                new FrameSegment { Type = SegmentType.DataField, Name = "Y", ByteCount = byteCount, DataType = dataType, ChannelIndex = 1 },
                new FrameSegment { Type = SegmentType.DataField, Name = "Z", ByteCount = byteCount, DataType = dataType, ChannelIndex = 2 },
                new FrameSegment { Type = SegmentType.Checksum, Name = "CS", ByteCount = 1, ChecksumAlgorithm = ChecksumAlgorithm.Xor, ChecksumStartIndex = 0 },
                new FrameSegment { Type = SegmentType.Tail, Name = "T", ByteCount = 1, FixedHexValue = "0D" },
            ],
        };
        config.ComputeSegmentOffsets();
        return config;
    }

    /// <summary>
    /// Build a segment-mode binary frame from float values for a config created by CreateSegmentConfig.
    /// </summary>
    private static byte[] BuildSegmentFrame(ProtocolConfig config, params float[] values)
    {
        int totalLen = config.TotalFrameLength;
        var frame = new byte[totalLen];

        // Header
        frame[0] = 0xAA;
        frame[1] = 0x55;

        // Data fields
        var dataSegments = config.Segments.Where(s => s.Type == SegmentType.DataField).ToList();
        for (int i = 0; i < values.Length && i < dataSegments.Count; i++)
        {
            var seg = dataSegments[i];
            var bytes = BitConverter.GetBytes(values[i]);
            Array.Copy(bytes, 0, frame, seg.ComputedOffset, seg.ByteCount);
        }

        // Checksum (XOR from start)
        var checksumSeg = config.Segments.First(s => s.Type == SegmentType.Checksum);
        byte checksum = 0;
        for (int i = 0; i < checksumSeg.ComputedOffset; i++)
            checksum ^= frame[i];
        frame[checksumSeg.ComputedOffset] = checksum;

        // Tail
        frame[^1] = 0x0D;

        return frame;
    }

    #endregion
}
