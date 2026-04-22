using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

public class CommandFrameBuilderTests
{
    [Fact]
    public void RenderAsciiTemplate_SubstitutesPlaceholders()
    {
        var cmd = new DeviceCommand { Template = "SET_RATE {rate} CHAN {ch}" };
        var values = new Dictionary<string, string> { ["rate"] = "100", ["ch"] = "X" };

        var result = CommandFrameBuilder.RenderAsciiTemplate(cmd, values);

        Assert.Equal("SET_RATE 100 CHAN X", result);
    }

    [Fact]
    public void BuildAsciiBytes_AppendsCrLf_WhenAppendNewline()
    {
        var cmd = new DeviceCommand { Template = "GET_SN", AppendNewline = true };
        var bytes = CommandFrameBuilder.BuildAsciiBytes(cmd, new Dictionary<string, string>());

        Assert.Equal(System.Text.Encoding.UTF8.GetBytes("GET_SN\r\n"), bytes);
    }

    [Fact]
    public void BuildBinaryFrame_EmptyHeaderTail_ProducesDataOnly()
    {
        var cmd = new DeviceCommand
        {
            Encoding = CommandEncoding.BinaryFrame,
            Parameters = { new() { Key = "v", Type = CommandParameterType.U8, DefaultValue = "42" } }
        };
        var preview = CommandFrameBuilder.BuildBinaryFrame(cmd,
            new Dictionary<string, string> { ["v"] = "42" });

        Assert.Empty(preview.HeaderBytes);
        Assert.Empty(preview.TailBytes);
        Assert.Empty(preview.ChecksumBytes);
        Assert.Equal(new byte[] { 42 }, preview.DataBytes);
        Assert.Equal(new byte[] { 42 }, preview.FullBytes);
    }

    [Fact]
    public void BuildBinaryFrame_Float32LittleEndian()
    {
        var cmd = new DeviceCommand
        {
            Encoding = CommandEncoding.BinaryFrame,
            Parameters = { new() { Key = "f", Type = CommandParameterType.Float32, Endian = Endianness.LittleEndian } }
        };
        var preview = CommandFrameBuilder.BuildBinaryFrame(cmd,
            new Dictionary<string, string> { ["f"] = "1.0" });

        // 1.0f IEEE754 = 0x3F800000，LE = 00 00 80 3F
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, preview.DataBytes);
    }

    [Fact]
    public void BuildBinaryFrame_Float32BigEndian()
    {
        var cmd = new DeviceCommand
        {
            Encoding = CommandEncoding.BinaryFrame,
            Parameters = { new() { Key = "f", Type = CommandParameterType.Float32, Endian = Endianness.BigEndian } }
        };
        var preview = CommandFrameBuilder.BuildBinaryFrame(cmd,
            new Dictionary<string, string> { ["f"] = "1.0" });

        Assert.Equal(new byte[] { 0x3F, 0x80, 0x00, 0x00 }, preview.DataBytes);
    }

    [Fact]
    public void BuildBinaryFrame_FullPipeline_HeaderDataCrcTail()
    {
        var cmd = new DeviceCommand
        {
            Encoding = CommandEncoding.BinaryFrame,
            FrameHeader = "AA 55",
            FrameTail = "55 AA",
            Checksum = ChecksumKind.Crc16,
            Parameters =
            {
                new() { Key = "x", Type = CommandParameterType.U16, Endian = Endianness.LittleEndian }
            }
        };
        var preview = CommandFrameBuilder.BuildBinaryFrame(cmd,
            new Dictionary<string, string> { ["x"] = "0x1234".StartsWith("0x") ? "4660" : "4660" });

        Assert.Equal(new byte[] { 0xAA, 0x55 }, preview.HeaderBytes);
        Assert.Equal(new byte[] { 0x34, 0x12 }, preview.DataBytes);
        Assert.Equal(2, preview.ChecksumBytes.Length); // CRC16 = 2 bytes
        Assert.Equal(new byte[] { 0x55, 0xAA }, preview.TailBytes);

        var full = preview.FullBytes;
        Assert.Equal(2 + 2 + 2 + 2, full.Length);
    }

    [Fact]
    public void BuildBinaryFrame_HexBytesRespectsByteLength()
    {
        var cmd = new DeviceCommand
        {
            Encoding = CommandEncoding.BinaryFrame,
            Parameters =
            {
                new() { Key = "raw", Type = CommandParameterType.HexBytes, ByteLength = 3 }
            }
        };

        var ok = CommandFrameBuilder.BuildBinaryFrame(cmd,
            new Dictionary<string, string> { ["raw"] = "01 02 03" });
        Assert.Equal(new byte[] { 1, 2, 3 }, ok.DataBytes);

        Assert.Throws<FormatException>(() =>
            CommandFrameBuilder.BuildBinaryFrame(cmd,
                new Dictionary<string, string> { ["raw"] = "01 02" }));
    }

    [Fact]
    public void BuildBinaryFrame_Sum8Checksum_ComputedOverDataOnly()
    {
        var cmd = new DeviceCommand
        {
            Encoding = CommandEncoding.BinaryFrame,
            FrameHeader = "FF",   // 帧头不应被计入校验
            Checksum = ChecksumKind.Sum8,
            Parameters =
            {
                new() { Key = "a", Type = CommandParameterType.U8 },
                new() { Key = "b", Type = CommandParameterType.U8 },
            }
        };
        var preview = CommandFrameBuilder.BuildBinaryFrame(cmd,
            new Dictionary<string, string> { ["a"] = "10", ["b"] = "20" });

        Assert.Equal(new byte[] { 30 }, preview.ChecksumBytes); // 10+20=30，未把 0xFF 帧头算入
    }

    [Fact]
    public void EncodeParameter_UnsignedOverflow_Throws()
    {
        var p = new CommandParameter { Type = CommandParameterType.U8 };
        Assert.Throws<OverflowException>(() => CommandFrameBuilder.EncodeParameter(p, "300"));
    }
}
