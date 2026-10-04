using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;

namespace MagnetometerSystem.Core.Tests;

public class VariableLengthSegmentParserTests
{
    [Theory]
    [InlineData(1, false, ChecksumAlgorithm.Xor, false)]
    [InlineData(2, false, ChecksumAlgorithm.Sum8, false)]
    [InlineData(2, true, ChecksumAlgorithm.CRC16, false)]
    [InlineData(2, true, ChecksumAlgorithm.CRC16, true)]
    public void PayloadPaddingAndUnmappedSuffix_ParseWithDynamicChecksumAndTail(
        int lengthBytes, bool lengthBigEndian, ChecksumAlgorithm checksum, bool crcBigEndian)
    {
        var config = CreateConfig(lengthBytes, lengthBigEndian, checksum, crcBigEndian);
        var parser = new ConfigurableBinaryParser(config);
        // Layout describes six payload bytes; four is the minimum mapped range.
        // Different lengths must neither double-count padding nor pin the checksum.
        int[] payloadLengths = [4, 6, lengthBytes == 1 ? 13 : 260];
        byte[] bytes = payloadLengths.SelectMany(n => Frame(config, n)).ToArray();
        parser.Feed(bytes, 0, bytes.Length - 1);
        for (int i = 0; i < 2; i++)
        {
            Assert.True(parser.TryParse(out var reading));
            Assert.Equal(new[] { 4660d }, reading!.ChannelValues);
        }
        Assert.False(parser.TryParse(out _));
        parser.Feed(bytes, bytes.Length - 1, 1);
        Assert.True(parser.TryParse(out var last));
        Assert.Equal(new[] { 4660d }, last!.ChannelValues);
        Assert.False(parser.TryParse(out _));
        Assert.Equal(0, parser.RejectedFrameCount);
    }

    [Fact]
    public void SplitAtEveryByte_PreservesPrefixMappingAndWaitsForWholeFrame()
    {
        var config = CreateConfig(2, true, ChecksumAlgorithm.CRC16, true);
        var frame = Frame(config, 12);
        var parser = new ConfigurableBinaryParser(config);
        for (int i = 0; i < frame.Length - 1; i++)
        {
            parser.Feed(frame, i, 1);
            Assert.False(parser.TryParse(out _));
        }
        parser.Feed(frame, frame.Length - 1, 1);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(4660d, reading!.ChannelValues[0]);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("crc")]
    [InlineData("tail")]
    [InlineData("fixed")]
    public void InvalidFrameBeforeValidFrame_RecoversWithoutAnotherFeed(string damage)
    {
        var config = CreateConfig(1, false, ChecksumAlgorithm.CRC16, false);
        byte[] invalid = Frame(config, damage == "short" ? 3 : 12);
        switch (damage)
        {
            case "crc": invalid[^3] ^= 0x40; break;
            case "tail": invalid[^1] ^= 0x40; break;
            case "fixed": invalid[2] ^= 0x40; break;
        }
        byte[] stream = [.. invalid, .. Frame(config, 9)];
        var parser = new ConfigurableBinaryParser(config);
        parser.Feed(stream, 0, stream.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(new[] { 4660d }, reading!.ChannelValues);
        Assert.True(parser.RejectedFrameCount > 0);
        Assert.False(parser.TryParse(out _));
    }

    [Fact]
    public void FixedPayloadAnchor_MustFitDeclaredPayload()
    {
        var config = CreateConfig(1, false, ChecksumAlgorithm.Xor, false);
        config.Segments[5].ValidateFixedValue = true;
        config.Segments[5].FixedHexValue = "7E7E";
        config.Validate();
        byte[] stream = [.. Frame(config, 4), .. Frame(config, 9)];
        var parser = new ConfigurableBinaryParser(config);
        parser.Feed(stream, 0, stream.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(4660d, reading!.ChannelValues[0]);
        Assert.True(parser.RejectedFrameCount > 0);
        Assert.False(parser.TryParse(out _));
    }

    [Fact]
    public void ReloadedLegacyLengthProtocol_WithGapAndUnmappedSuffix_ParsesSameFrame()
    {
        var legacy = new ProtocolConfig
        {
            Category = ProtocolCategory.Binary, FrameHeader = "AA55", FrameTail = "0D0A",
            HasLengthByte = true, LengthByteCount = 1, Checksum = ChecksumType.Xor,
            FieldMappings = [new() { Name = "Value", DataType = FieldDataType.UInt16, ByteOffset = 2, ChannelIndex = 0 }],
        };
        var migrated = ProtocolConfig.FromJson(legacy.ToJson())!;
        Assert.True(migrated.UsesSegments);
        migrated.Validate();
        byte[] frame = [0xAA, 0x55, 8, 0x10, 0x20, 0x34, 0x12, 0x7E, 0x7E, 0x7E, 0x7E, 0, 0x0D, 0x0A];
        for (int i = 0; i < frame.Length - 3; i++) frame[^3] ^= frame[i];
        foreach (var config in new[] { legacy, migrated })
        {
            var parser = new ConfigurableBinaryParser(config);
            parser.Feed(frame, 0, frame.Length);
            Assert.True(parser.TryParse(out var reading));
            Assert.Equal(new[] { 4660d }, reading!.ChannelValues);
            Assert.False(parser.TryParse(out _));
        }
    }

    [Fact]
    public void NoChecksum_UsesActualLengthToLocateTail()
    {
        var config = CreateConfig(1, false, ChecksumAlgorithm.Xor, false);
        config.Segments.RemoveAll(s => s.Type == SegmentType.Checksum);
        config.Validate();
        var parser = new ConfigurableBinaryParser(config);
        byte[] frame = [0xAA, 0x55, 0x01, 8, 0x10, 0x20, 0x34, 0x12, 0x7E, 0x7E, 0x7E, 0x7E, 0x0D, 0x0A];
        parser.Feed(frame, 0, frame.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(new[] { 4660d }, reading!.ChannelValues);
    }

    [Fact]
    public void DeclaredFrameBeyondBufferCapacity_IsRejectedBeforeWaitingForPayload()
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Binary,
            Segments =
            [
                new() { Type = SegmentType.Header, ByteCount = 2, FixedHexValue = "AA55" },
                new() { Type = SegmentType.Padding, ByteCount = 66000 },
                new() { Type = SegmentType.LengthField, ByteCount = 2 },
                new() { Type = SegmentType.DataField, ByteCount = 2, DataType = FieldDataType.UInt16 },
            ],
        };
        config.Validate();
        byte[] prefix = new byte[66004];
        prefix[0] = 0xAA;
        prefix[1] = 0x55;
        prefix[^2] = 0xFF;
        prefix[^1] = 0xFF;
        var parser = new ConfigurableBinaryParser(config);
        parser.Feed(prefix, 0, prefix.Length);
        Assert.False(parser.TryParse(out _));
        Assert.True(parser.RejectedFrameCount > 0);
    }

    private static ProtocolConfig CreateConfig(int lengthBytes, bool lengthBigEndian,
        ChecksumAlgorithm checksum, bool crcBigEndian)
    {
        var config = new ProtocolConfig
        {
            Category = ProtocolCategory.Binary,
            Segments =
            [
                new() { Type = SegmentType.Header, ByteCount = 2, FixedHexValue = "AA55" },
                new() { Type = SegmentType.Padding, ByteCount = 1, FixedHexValue = "01", ValidateFixedValue = true },
                new() { Type = SegmentType.LengthField, ByteCount = lengthBytes, LengthBigEndian = lengthBigEndian },
                new() { Type = SegmentType.Padding, ByteCount = 2 },
                new() { Type = SegmentType.DataField, ByteCount = 2, DataType = FieldDataType.UInt16, ChannelIndex = 0 },
                new() { Type = SegmentType.Padding, ByteCount = 2 },
                new() { Type = SegmentType.Checksum, ByteCount = checksum == ChecksumAlgorithm.CRC16 ? 2 : 1,
                    ChecksumAlgorithm = checksum, ChecksumStartIndex = 2, ChecksumBigEndian = crcBigEndian },
                new() { Type = SegmentType.Tail, ByteCount = 2, FixedHexValue = "0D0A" },
            ],
        };
        config.Validate();
        return config;
    }

    private static byte[] Frame(ProtocolConfig config, int payloadLength)
    {
        var length = config.Segments.Single(s => s.Type == SegmentType.LengthField);
        var checksum = config.Segments.Single(s => s.Type == SegmentType.Checksum);
        var frame = new List<byte> { 0xAA, 0x55, 0x01 };
        if (length.ByteCount == 1) frame.Add((byte)payloadLength);
        else if (length.LengthBigEndian) frame.AddRange([(byte)(payloadLength >> 8), (byte)payloadLength]);
        else frame.AddRange([(byte)payloadLength, (byte)(payloadLength >> 8)]);
        byte[] payload = Enumerable.Repeat((byte)0x7E, payloadLength).ToArray();
        byte[] mapped = [0x10, 0x20, 0x34, 0x12];
        Array.Copy(mapped, payload, Math.Min(mapped.Length, payload.Length));
        frame.AddRange(payload);
        byte[] checksumData = frame.Skip(3).ToArray();
        if (checksum.ChecksumAlgorithm == ChecksumAlgorithm.CRC16)
        {
            ushort crc = Crc16.Compute(checksumData, checksum.Crc16Variant);
            if (checksum.ChecksumBigEndian) frame.AddRange([(byte)(crc >> 8), (byte)crc]);
            else frame.AddRange([(byte)crc, (byte)(crc >> 8)]);
        }
        else
        {
            byte result = 0;
            foreach (byte value in checksumData)
                result = checksum.ChecksumAlgorithm == ChecksumAlgorithm.Xor ? (byte)(result ^ value) : (byte)(result + value);
            frame.Add(result);
        }
        frame.AddRange([0x0D, 0x0A]);
        return frame.ToArray();
    }
}
