using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;

namespace MagnetometerSystem.Core.Tests;

public class ProtocolParseTesterTests
{
    private static ProtocolConfig BinaryConfig()
    {
        var config = new ProtocolConfig
        {
            Name = "测试三轴",
            Category = ProtocolCategory.Binary,
            Segments =
            [
                new() { Type = SegmentType.Header, Name = "帧头", ByteCount = 2, FixedHexValue = "AA55" },
                new() { Type = SegmentType.DataField, Name = "X", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 0 },
                new() { Type = SegmentType.DataField, Name = "Y", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 1 },
                new() { Type = SegmentType.DataField, Name = "Z", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 2 },
                new() { Type = SegmentType.Checksum, Name = "校验", ByteCount = 1, ChecksumAlgorithm = ChecksumAlgorithm.Xor },
                new() { Type = SegmentType.Tail, Name = "帧尾", ByteCount = 1, FixedHexValue = "0D" },
            ],
        };
        config.ComputeSegmentOffsets();
        return config;
    }

    private static byte[] Frame(float x, float y, float z)
    {
        var f = new byte[16];
        f[0] = 0xAA; f[1] = 0x55;
        BitConverter.GetBytes(x).CopyTo(f, 2);
        BitConverter.GetBytes(y).CopyTo(f, 6);
        BitConverter.GetBytes(z).CopyTo(f, 10);
        byte c = 0;
        for (int i = 0; i < 14; i++) c ^= f[i];
        f[14] = c; f[15] = 0x0D;
        return f;
    }

    private static string Hex(IEnumerable<byte> bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));

    [Fact]
    public void BinarySampleWithNoiseAndBadChecksumResynchronizes()
    {
        var bad = Frame(4, 5, 6); bad[14] ^= 0xFF;
        var input = new byte[] { 0x3F, 0x0D }.Concat(Frame(1, 2, 3)).Concat(bad).Concat(Frame(7, 8, 9)).ToArray();
        var bytes = ProtocolParseTester.ReadInput(Hex(input), ParseTestInputKind.Hex);
        var result = ProtocolParseTester.Run(BinaryConfig(), bytes);

        Assert.Equal(input.Length, result.InputByteCount);
        Assert.Equal(2, result.Frames.Count);
        Assert.Equal([1d, 2d, 3d], result.Frames[0].Values);
        Assert.Equal([7d, 8d, 9d], result.Frames[1].Values);
        Assert.True(result.RejectedCount >= 1);
        Assert.NotEmpty(result.Errors);
        Assert.Equal(["X", "Y", "Z"], result.ChannelNames);
        // 逐帧记录：噪声、通过、校验失败（带计算值与帧内值）、重新同步丢弃、通过。
        Assert.Equal([ParseOutcome.Skipped, ParseOutcome.Accepted, ParseOutcome.Rejected, ParseOutcome.Skipped, ParseOutcome.Accepted],
            result.Records.Select(r => r.Outcome));
        Assert.StartsWith("XOR 校验失败：计算", result.Records[2].Detail);
    }

    [Fact]
    public void AsciiSampleUsesEscapes()
    {
        var bytes = ProtocolParseTester.ReadInput(@"1.5,2,3\n4,5,6\n", ParseTestInputKind.Text);
        var result = ProtocolParseTester.Run(ProtocolConfig.CreateDefaultAsciiTriaxial(), bytes);
        Assert.Equal(2, result.Frames.Count);
        Assert.Equal(1.5, result.Frames[0].Values[0]);
        Assert.Equal(6, result.Frames[1].Values[2]);
    }

    [Fact]
    public void TextInputKeepsExactBytesAndDecodesEscapesInOnePass()
    {
        // 转义 \r\n 与文本框里的直接换行（CRLF）都原样保留；"\\n" 是反斜杠加 n，不是换行。
        Assert.Equal("a\r\nb\r\nc\\n"u8.ToArray(), ProtocolParseTester.ReadInput("a\\r\\nb\r\nc\\\\n", ParseTestInputKind.Text));
    }

    [Fact]
    public void CrTerminatedProtocolParsesPastedCrlfAndEscapedCr()
    {
        var protocol = ProtocolConfig.CreateDefaultAsciiTriaxial();
        protocol.AsciiLineEnding = "\r";
        var bytes = ProtocolParseTester.ReadInput("1,2,3\r\n4,5,6\\r", ParseTestInputKind.Text);
        var result = ProtocolParseTester.Run(protocol, bytes);
        Assert.Equal(2, result.Frames.Count);
        Assert.Equal(6, result.Frames[1].Values[2]);
    }

    [Fact]
    public void HexInputAcceptsPrefixesAndRejectsGarbage()
    {
        Assert.Equal(new byte[] { 0xAA, 0x55, 0x0D }, ProtocolParseTester.ReadInput("0xAA,0x55 0d", ParseTestInputKind.Hex));
        Assert.Throws<FormatException>(() => ProtocolParseTester.ReadInput("AA 5G", ParseTestInputKind.Hex));
        Assert.Throws<FormatException>(() => ProtocolParseTester.ReadInput("AA 5", ParseTestInputKind.Hex));
    }

    [Fact]
    public void TestDoesNotModifyEditedProtocol()
    {
        var config = BinaryConfig();
        var json = config.ToJson();
        ProtocolParseTester.Run(config, Frame(1, 2, 3));
        Assert.Equal(json, config.ToJson());
    }
}
