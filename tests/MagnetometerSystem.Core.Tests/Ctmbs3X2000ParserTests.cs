using System.Text;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;

namespace MagnetometerSystem.Core.Tests;

/// <summary>
/// CTMBS-3-X2000 实时数据解析器测试。
/// 帧格式见《Windows 与 Linux 板通信协议》§3.2(a) 带数据响应 + §6 实时推送。
/// </summary>
public class Ctmbs3X2000ParserTests
{
    // payload = " HHMMSS 台站码 仪器ID 采样率 4 ch1..ch4码 ch1..ch4值"
    // 首字节为空格（与文档一致，便于和长度字段视觉分隔）。
    private const string Payload =
        " 120000 SC01 X122PWZK0000 07 4 3125 3124 3123 3129 1.23 2.34 3.45 4.56";

    // L = digits(L) + payload.Length；70 + 2 = 72
    private const int ExpectedLength = 72;

    /// <summary>$&lt;L&gt;\n&lt;L digits&gt;&lt;payload&gt;\nack\n</summary>
    private static byte[] Frame()
    {
        // 锁定 payload 长度，避免手抄字面量时数错字节
        Assert.Equal(70, Payload.Length);
        var lenStr = ExpectedLength.ToString();
        var frame = "$" + lenStr + "\n" + lenStr + Payload + "\nack\n";
        return Encoding.ASCII.GetBytes(frame);
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public void Parse_DataFrame_DecodesFourChannels()
    {
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        var frame = Frame();
        parser.Feed(frame, 0, frame.Length);

        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);
        Assert.Equal(4, reading.ChannelValues.Length);
        Assert.Equal(1.23, reading.ChannelValues[0]);   // D
        Assert.Equal(2.34, reading.ChannelValues[1]);   // H
        Assert.Equal(3.45, reading.ChannelValues[2]);   // Z
        Assert.Equal(4.56, reading.ChannelValues[3]);   // T
    }

    [Fact]
    public void Parse_DataFrame_TimestampFromHhmmss()
    {
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        var frame = Frame();
        parser.Feed(frame, 0, frame.Length);

        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);
        Assert.Equal(12, reading.Timestamp.Hour);
        Assert.Equal(0, reading.Timestamp.Minute);
        Assert.Equal(0, reading.Timestamp.Second);
    }

    [Fact]
    public void Parse_IncompleteFrame_ReturnsFalseAndKeepsBuffer()
    {
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        var frame = Frame();
        var half = frame[..(frame.Length / 2)];

        parser.Feed(half, 0, half.Length);
        Assert.False(parser.TryParse(out _));

        // 补齐剩余，应能解析
        parser.Feed(frame, half.Length, frame.Length - half.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);
        Assert.Equal(1.23, reading.ChannelValues[0]);
    }

    [Fact]
    public void Parse_TwoBackToBackFrames_BothDecode()
    {
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        var frame = Frame();
        var stream = frame.Concat(frame).ToArray();
        parser.Feed(stream, 0, stream.Length);

        Assert.True(parser.TryParse(out var first));
        Assert.True(parser.TryParse(out var second));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.ChannelValues[3], second.ChannelValues[3]);
    }

    [Fact]
    public void Parse_AckFrame_SkippedWithoutReading()
    {
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        parser.Feed(Ascii("$ack\n"), 0, 5);

        Assert.False(parser.TryParse(out var reading));
        Assert.Null(reading);
    }

    [Fact]
    public void Parse_ErrFrame_SkippedWithoutReading()
    {
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        parser.Feed(Ascii("$err\n"), 0, 5);

        Assert.False(parser.TryParse(out _));
    }

    [Fact]
    public void Parse_AckThenData_SkipsAckAndDecodesDataInOneCall()
    {
        // §6：推送期间普通命令的响应穿插在数据帧之间。解析器应在单次 TryParse
        // 内跳过 $ack\n 并直接给出其后紧跟的数据帧，而非把数据帧挂起到下次 Feed。
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        var stream = Ascii("$ack\n").Concat(Frame()).ToArray();
        parser.Feed(stream, 0, stream.Length);

        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);
        Assert.Equal(1.23, reading.ChannelValues[0]);
        Assert.False(parser.TryParse(out _)); // 缓冲已空
    }

    [Fact]
    public void Parse_LoginResponseAckThenInstruId_DoesNotDerailFollowingData()
    {
        // 登录成功响应 $ack\n<仪器ID> 无尾部换行，仪器 ID 会作为"垃圾"留在缓冲。
        // 解析器须能扫过这段垃圾，锁上后续真正以 $ 起始的数据帧。
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        var stream = Ascii("$ack\nX122PWZK0000").Concat(Frame()).ToArray();
        parser.Feed(stream, 0, stream.Length);

        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);
        Assert.Equal(1.23, reading.ChannelValues[0]);
    }

    [Fact]
    public void Parse_LeadingGarbage_ResyncsToDollar()
    {
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        var stream = Ascii("garbage!!").Concat(Frame()).ToArray();
        parser.Feed(stream, 0, stream.Length);

        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);
        Assert.Equal(4.56, reading.ChannelValues[3]);
    }

    [Fact]
    public void Parse_BadLengthDigits_ResyncsToNextDollar()
    {
        // $XY\n... 非法长度字段，跳过该 '$' 后继续锁帧
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        var stream = Ascii("$XY\njunk").Concat(Frame()).ToArray();
        parser.Feed(stream, 0, stream.Length);

        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);
        Assert.Equal(1.23, reading.ChannelValues[0]);
    }
}
