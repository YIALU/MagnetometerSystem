using System.Text;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;

namespace MagnetometerSystem.Core.Tests;

public class ParseRecordLogTests
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

    private static int ParseAll(IDataParser parser)
    {
        int n = 0;
        while (parser.TryParse(out var reading)) if (reading != null) n++;
        return n;
    }

    [Fact]
    public void Binary_RecordsNoiseAcceptedFramesAndChecksumFailureWithBothValues()
    {
        var parser = (ConfigurableBinaryParser)ParserFactory.Create(BinaryConfig());
        var bad = Frame(4, 5, 6);
        var expectedXor = bad[14];
        bad[14] ^= 0xFF;
        var input = new byte[] { 0x3F, 0x0D }.Concat(Frame(1, 2, 3)).Concat(Frame(1, 2, 3)).Concat(bad).Concat(Frame(7, 8, 9)).ToArray();

        // 分两次送入，跨越帧边界。
        parser.Feed(input, 0, 21);
        int frames = ParseAll(parser);
        parser.Feed(input, 21, input.Length - 21);
        frames += ParseAll(parser);
        Assert.Equal(3, frames);

        var records = parser.Records.Drain();
        Assert.Equal([ParseOutcome.Skipped, ParseOutcome.Accepted, ParseOutcome.Rejected, ParseOutcome.Skipped, ParseOutcome.Accepted],
            records.Select(r => r.Outcome));
        Assert.Equal(2, records[0].ByteCount);                 // 帧头前的 3F 0D
        Assert.Equal(2, records[1].FrameCount);                // 连续通过的两帧合并
        Assert.Equal(32, records[1].ByteCount);
        Assert.Equal("3 通道", records[1].Detail);
        Assert.StartsWith("AA 55", records[1].Preview);
        Assert.Equal($"XOR 校验失败：计算 {expectedXor:X2}，帧内 {bad[14]:X2}", records[2].Detail);
        Assert.Equal(16, records[2].ByteCount);                // 候选帧长度
        Assert.StartsWith("AA 55", records[2].Preview);
        Assert.Equal(15, records[3].ByteCount);                // 坏帧其余字节作为噪声丢弃后重新同步
        Assert.Equal(1, records[4].FrameCount);
        Assert.Contains("XOR 校验失败", parser.LastError);
        Assert.Empty(parser.Records.Drain());
    }

    [Fact]
    public void Binary_SteadyStreamMergesIntoOneRecordAcrossDrains()
    {
        var parser = (ConfigurableBinaryParser)ParserFactory.Create(BinaryConfig());
        var frame = Frame(1, 2, 3);
        for (int i = 0; i < 1000; i++) { parser.Feed(frame, 0, frame.Length); ParseAll(parser); }
        var record = Assert.Single(parser.Records.Drain());
        Assert.Equal(ParseOutcome.Accepted, record.Outcome);
        Assert.Equal(1000, record.FrameCount);
        Assert.Equal(16000, record.ByteCount);
        Assert.True(record.LastTime >= record.FirstTime);
    }

    [Fact]
    public void Log_IsBoundedBetweenDrains()
    {
        var log = new ParseRecordLog(capacity: 5);
        for (int i = 0; i < 20; i++) log.Rejected(4, $"坏帧 {i}", new byte[] { 1, 2, 3, 4 }, hex: true);
        var records = log.Drain();
        Assert.Equal(5, records.Count);
        Assert.Equal("坏帧 15", records[0].Detail);
        Assert.Equal(15, log.DroppedCount);
        Assert.Equal("01 02 03 04", records[0].Preview);
    }

    [Fact]
    public void Preview_ShortensLongHexAndEscapesText()
    {
        var bytes = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        Assert.Equal("00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F … 24 25 26 27", ParseRecordLog.Preview(bytes, hex: true));
        Assert.Equal("1,2\t3·", ParseRecordLog.Preview(Encoding.ASCII.GetBytes("1,2\t3\u0001\r\n"), hex: false)?.Replace(' ', '\t'));
        Assert.Null(ParseRecordLog.Preview([], hex: true));
    }

    [Fact]
    public void Ascii_RecordsHeaderCommentRejectedColumnAndAcceptedLines()
    {
        var config = new ProtocolConfig
        {
            Name = "ascii", Category = ProtocolCategory.Ascii, AsciiDelimiter = ",", AsciiHasHeader = true,
            FieldMappings = [new() { Name = "X", ByteOffset = 0, ChannelIndex = 0 }, new() { Name = "Y", ByteOffset = 1, ChannelIndex = 1 }],
        };
        var parser = (ConfigurableAsciiParser)ParserFactory.Create(config);
        var text = Encoding.ASCII.GetBytes("X,Y\n# comment\n1,2\n3,abc\n4\n5,6\n");
        parser.Feed(text, 0, text.Length);
        Assert.Equal(2, ParseAll(parser));

        var records = parser.Records.Drain();
        Assert.Equal([ParseOutcome.Skipped, ParseOutcome.Skipped, ParseOutcome.Accepted, ParseOutcome.Rejected, ParseOutcome.Rejected, ParseOutcome.Accepted],
            records.Select(r => r.Outcome));
        Assert.Equal("跳过表头行", records[0].Detail);
        Assert.Equal("1,2", records[2].Preview);
        Assert.Equal("列号 1 的“abc”不是有限数值", records[3].Detail);
        Assert.Equal("3,abc", records[3].Preview);
        Assert.StartsWith("缺少列号 1", records[4].Detail);
        Assert.Equal(2, parser.RejectedFrameCount);
    }

    [Fact]
    public void Ctmbs_RecordsDeviceResponsesAndDataFrames()
    {
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        // L = digits(L) + payload 长度：70 + 2 = 72（与 Ctmbs3X2000ParserTests 相同的样例帧）
        const string payload = " 120000 SC01 X122PWZK0000 07 4 3125 3124 3123 3129 1.23 2.34 3.45 4.56";
        var input = Encoding.ASCII.GetBytes("xx$ack\n$72\n72" + payload + "\nack\n");
        parser.Feed(input, 0, input.Length);
        Assert.Equal(1, ParseAll(parser));

        var records = parser.Records.Drain();
        Assert.Equal([ParseOutcome.Skipped, ParseOutcome.Skipped, ParseOutcome.Accepted], records.Select(r => r.Outcome));
        Assert.Equal(2, records[0].ByteCount);
        Assert.Contains("设备响应", records[1].Detail);
        Assert.Equal("4 通道", records[2].Detail);
    }
}
