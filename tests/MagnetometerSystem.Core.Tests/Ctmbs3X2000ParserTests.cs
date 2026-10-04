using System.Globalization;
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

    private static byte[] Frame(string payload)
    {
        int length = payload.Length + 1;
        while (length != payload.Length + length.ToString().Length)
            length = payload.Length + length.ToString().Length;
        return Ascii($"${length}\n{length}{payload}\nack\n");
    }

    [Theory]
    [InlineData(" 20261004120000 1 0 1 1 0 0 0 0 00 25.50")] // ste (§7.1)
    [InlineData(" 07 04 8 1 0 1 0 1 0 1 0")] // pmr+m (§5.6)
    [InlineData(" 127.0.0.1 255.255.255.0 127.0.0.2 3 8080 21 81 10.13.64.1 1024 10.5.67.14")]
    [InlineData(" 1 2 3 4")]
    [InlineData(" 07 3125 1 0 3124 1 0 3123 1 0 3129 1 0")] // pmr+1m has 13 fields too.
    public void Parse_CommandDataResponses_DoNotBecomeMeasurements(string payload)
    {
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        byte[] response = Frame(payload);
        foreach (byte value in response)
        {
            parser.Feed([value], 0, 1);
            Assert.False(parser.TryParse(out var reading));
            Assert.Null(reading);
            Assert.Equal(0, parser.RejectedFrameCount);
        }
        Assert.Null(parser.LastError);

        byte[] stream = [.. Frame(), .. response, .. Frame()];
        parser.Feed(stream, 0, stream.Length);
        Assert.True(parser.TryParse(out _));
        Assert.True(parser.TryParse(out var second));
        Assert.Equal(new[] { 1.23, 2.34, 3.45, 4.56 }, second!.ChannelValues);
        Assert.False(parser.TryParse(out _));
        Assert.Equal(0, parser.RejectedFrameCount);
        Assert.Null(parser.LastError);
    }

    [Fact]
    public void Parse_DamagedCommandEnvelopeStillReportsFailureAndRecovers()
    {
        byte[] response = Frame(" 20261004120000 1 0 1 1 0 0 0 0 00 25.50");
        response[^2] = (byte)'X'; // Even a nonmeasurement response must have an intact ack tail.
        byte[] stream = [.. response, .. Frame()];
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        parser.Feed(stream, 0, stream.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(1.23, reading!.ChannelValues[0]);
        Assert.Equal(1, parser.RejectedFrameCount);
        Assert.NotNull(parser.LastError);
        Assert.False(parser.TryParse(out _));
    }

    [Theory]
    [InlineData(0, "12000")]
    [InlineData(0, "240000")]
    [InlineData(0, "126000")]
    [InlineData(0, "120060")]
    [InlineData(0, "-10000")]
    [InlineData(0, "20261004120000")]
    [InlineData(1, "SC\n01")]
    [InlineData(2, "device\ncode")]
    [InlineData(3, "rate")]
    [InlineData(4, "3")]
    [InlineData(5, "ch1")]
    [InlineData(8, "3129.0")]
    [InlineData(9, "NaN")]
    [InlineData(12, "Infinity")]
    public void Parse_InvalidRealtimeSchema_IsRejectedBeforeFollowingMeasurement(int index, string value)
    {
        string[] fields = Payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        fields[index] = value;
        byte[] stream = [.. Frame(" " + string.Join(' ', fields)), .. Frame()];
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        parser.Feed(stream, 0, stream.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(new[] { 1.23, 2.34, 3.45, 4.56 }, reading!.ChannelValues);
        Assert.False(parser.TryParse(out _));
        Assert.True(parser.RejectedFrameCount > 0);
    }

    [Theory]
    [InlineData(" 1.23 2.34 3.45 4.56", 0)] // dat+5 is a valid batch response, not one live reading.
    [InlineData(" extra", 1)]
    [InlineData(" NaN 2.34 3.45 4.56", 1)]
    public void Parse_ExtraRealtimeFields_DoNotSilentlyPublishLastSample(string suffix, int rejected)
    {
        byte[] bytes = Frame(Payload + suffix);
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        foreach (byte value in bytes)
        {
            parser.Feed([value], 0, 1);
            Assert.False(parser.TryParse(out _));
        }
        Assert.Equal(rejected, parser.RejectedFrameCount);
        byte[] live = Frame();
        parser.Feed(live, 0, live.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(1.23, reading!.ChannelValues[0]);
        Assert.Equal(rejected, parser.RejectedFrameCount);
    }

    [Theory]
    [InlineData("Site-device_A")]
    [InlineData("123450001")]
    public void Parse_ValidConfiguredIdentifiersRateCodesAndNumericNotation_AreNotHardCoded(string deviceCode)
    {
        byte[] bytes = Frame($" 235959 51001 {deviceCode} 04 04 4001 4002 4003 4004 -1.2e3 +2.5 0 2E-2");
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        parser.Feed(bytes, 0, bytes.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(new[] { -1200d, 2.5, 0, 0.02 }, reading!.ChannelValues);
        Assert.Equal(new TimeSpan(23, 59, 59), reading.Timestamp.TimeOfDay);
    }

    [Theory]
    [InlineData("$99999\n")]
    [InlineData("$99999\n99")]
    [InlineData("$72\n73")]
    [InlineData("$1\n1")]
    public void Parse_BadRepeatedLength_ImmediatelyResynchronizes(string badPrefix)
    {
        byte[] stream = [.. Ascii(badPrefix), .. Frame()];
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        parser.Feed(stream, 0, stream.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(1.23, reading!.ChannelValues[0]);
        Assert.False(parser.TryParse(out _));
        Assert.True(parser.RejectedFrameCount > 0);
    }

    [Fact]
    public void Parse_SplitRepeatedLength_DetectsFirstMismatchingByteWithoutWaitingForRest()
    {
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        byte[] prefix = Ascii("$99999\n99");
        parser.Feed(prefix, 0, prefix.Length);
        Assert.False(parser.TryParse(out _));
        Assert.Equal(0, parser.RejectedFrameCount);
        byte[] valid = Frame();
        parser.Feed(valid, 0, 1); // '$' already disproves the third repeated digit.
        Assert.False(parser.TryParse(out _));
        Assert.Equal(1, parser.RejectedFrameCount);
        parser.Feed(valid, 1, valid.Length - 1);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(1.23, reading!.ChannelValues[0]);
    }

    [Theory]
    [InlineData("$99999\n99999")]
    [InlineData("$99999\n99999 interrupted payload")]
    public void Parse_MatchingOversizedLength_ResynchronizesToFollowingMeasurement(string damagedPrefix)
    {
        byte[] stream = [.. Ascii(damagedPrefix), .. Frame(), .. Frame()];
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        parser.Feed(stream, 0, stream.Length);
        Assert.True(parser.TryParse(out var first));
        Assert.True(parser.TryParse(out var second));
        Assert.Equal(new[] { 1.23, 2.34, 3.45, 4.56 }, first!.ChannelValues);
        Assert.Equal(first.ChannelValues, second!.ChannelValues);
        Assert.False(parser.TryParse(out _));
        Assert.Equal(1, parser.RejectedFrameCount);
    }

    [Fact]
    public void Parse_MatchingOversizedLengthAcrossFeeds_RejectsWhenNewHeaderFirstArrives()
    {
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        byte[] prefix = Ascii("$99999\n99999 partial");
        parser.Feed(prefix, 0, prefix.Length);
        Assert.False(parser.TryParse(out _));
        Assert.Equal(0, parser.RejectedFrameCount);
        byte[] frame = Frame();
        parser.Feed(frame, 0, 1);
        Assert.False(parser.TryParse(out _));
        Assert.Equal(1, parser.RejectedFrameCount);
        parser.Feed(frame, 1, frame.Length - 2);
        Assert.False(parser.TryParse(out _));
        parser.Feed(frame, frame.Length - 1, 1);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(1.23, reading!.ChannelValues[0]);
    }

    [Fact]
    public void Parse_DamagedLengthWhoseDeclaredSizeIsAvailable_DoesNotConsumeNestedValidFrame()
    {
        // A complete bad candidate must not swallow a valid frame merely because
        // enough unrelated trailing bytes happened to satisfy its length claim.
        byte[] prefix = Ascii("$200\n200");
        byte[] frame = Frame();
        byte[] stream = [.. prefix, .. frame, .. Enumerable.Repeat((byte)' ', 210 - prefix.Length - frame.Length)];
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        parser.Feed(stream, 0, stream.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(1.23, reading!.ChannelValues[0]);
        Assert.False(parser.TryParse(out _));
        Assert.Equal(1, parser.RejectedFrameCount);
    }

    [Theory]
    [InlineData(" 07 04 8 1 0 1 0 1 0 1 0")]
    [InlineData(" 127.0.0.1 255.255.255.0 127.0.0.2 3 8080 21 81 10.13.64.1 1024 10.5.67.14")]
    public void Parse_SplitParameterResponse_IsNotMistakenForNewFrame(string payload)
    {
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        byte[] response = Frame(payload);
        for (int i = 0; i < response.Length - 1; i++)
        {
            parser.Feed(response, i, 1);
            Assert.False(parser.TryParse(out _));
            Assert.Equal(0, parser.RejectedFrameCount);
        }
        byte[] remainder = [response[^1], .. Frame()];
        parser.Feed(remainder, 0, remainder.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(1.23, reading!.ChannelValues[0]);
        Assert.False(parser.TryParse(out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void Parse_DollarInsideAnyRealtimeField_IsNotAValidMeasurement(int fieldIndex)
    {
        string[] fields = Payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        fields[fieldIndex] += "$";
        byte[] frame = Frame(" " + string.Join(' ', fields));
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        parser.Feed(frame, 0, frame.Length);
        Assert.False(parser.TryParse(out var reading));
        Assert.Null(reading);
        Assert.True(parser.RejectedFrameCount > 0);
    }

    [Fact]
    public void Parse_ValidFrameSplitAtEveryByte_PreservesPartialRepeatedLength()
    {
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        byte[] bytes = Frame();
        for (int i = 0; i < bytes.Length - 1; i++)
        {
            parser.Feed(bytes, i, 1);
            Assert.False(parser.TryParse(out _));
        }
        parser.Feed(bytes, bytes.Length - 1, 1);
        Assert.True(parser.TryParse(out _));
        Assert.Equal(0, parser.RejectedFrameCount);
    }

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

    [Theory]
    [InlineData("$bad")]
    [InlineData("$ack")]
    [InlineData("$err")]
    [InlineData("$$bad")]
    public void Parse_UnterminatedSimpleResponse_PreservesFollowingDataFrame(string damagedResponse)
    {
        byte[] stream = [.. Ascii(damagedResponse), .. Frame(), .. Frame()];
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        parser.Feed(stream, 0, stream.Length);
        Assert.True(parser.TryParse(out var first));
        Assert.True(parser.TryParse(out var second));
        Assert.Equal(new[] { 1.23, 2.34, 3.45, 4.56 }, first!.ChannelValues);
        Assert.Equal(first.ChannelValues, second!.ChannelValues);
        Assert.False(parser.TryParse(out _));
        Assert.True(parser.RejectedFrameCount > 0);
    }

    [Theory]
    [InlineData("$bad")]
    [InlineData("$ack")]
    public void Parse_UnterminatedResponseAcrossFeeds_ResyncsAsSoonAsNewDollarArrives(string damagedResponse)
    {
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        byte[] prefix = Ascii(damagedResponse);
        parser.Feed(prefix, 0, prefix.Length);
        Assert.False(parser.TryParse(out _));
        byte[] frame = Frame();
        parser.Feed(frame, 0, 1);
        Assert.False(parser.TryParse(out _));
        Assert.Equal(1, parser.RejectedFrameCount);
        for (int i = 1; i < frame.Length - 1; i++)
        {
            parser.Feed(frame, i, 1);
            Assert.False(parser.TryParse(out _));
        }
        parser.Feed(frame, frame.Length - 1, 1);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(1.23, reading!.ChannelValues[0]);
    }

    [Theory]
    [InlineData("$ack\n")]
    [InlineData("$err\n")]
    public void Parse_ValidSimpleResponseSplitAtEveryByte_DoesNotRejectNextMeasurement(string response)
    {
        byte[] stream = [.. Ascii(response), .. Frame()];
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000());
        for (int i = 0; i < stream.Length - 1; i++)
        {
            parser.Feed(stream, i, 1);
            Assert.False(parser.TryParse(out _));
        }
        parser.Feed(stream, stream.Length - 1, 1);
        Assert.True(parser.TryParse(out _));
        Assert.Equal(0, parser.RejectedFrameCount);
    }

    [Theory]
    [InlineData("2026-10-05T00:00:01Z", "235959", "2026-10-04T23:59:59Z")]
    [InlineData("2027-01-01T00:00:02Z", "235958", "2026-12-31T23:59:58Z")]
    [InlineData("2026-10-04T23:59:59Z", "000001", "2026-10-05T00:00:01Z")]
    [InlineData("2026-10-04T12:00:01Z", "120000", "2026-10-04T12:00:00Z")]
    public void Parse_HhmmssUsesDateNearestReceipt(string receivedAt, string hhmmss, string expectedTimestamp)
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse(receivedAt, CultureInfo.InvariantCulture));
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000(), clock);
        byte[] frame = Frame(Payload.Replace("120000", hhmmss));
        parser.Feed(frame, 0, frame.Length);
        // Decode scheduling does not replace the packet's actual receipt date.
        clock.UtcNow = clock.UtcNow.AddDays(1);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(DateTimeOffset.Parse(expectedTimestamp, CultureInfo.InvariantCulture).UtcDateTime, reading!.Timestamp);
        Assert.Equal(DateTimeKind.Utc, reading.Timestamp.Kind);
    }

    [Fact]
    public void Parse_ConsecutiveFramesAcrossUtcMidnight_StayInTimeOrder()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 5, 0, 0, 1, TimeSpan.Zero));
        var parser = new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000(), clock);
        byte[] delayedPrevious = Frame(Payload.Replace("120000", "235959"));
        byte[] current = Frame(Payload.Replace("120000", "000000"));
        byte[] stream = [.. delayedPrevious, .. current];
        parser.Feed(stream, 0, stream.Length);
        Assert.True(parser.TryParse(out var previous));
        Assert.True(parser.TryParse(out var next));
        Assert.Equal(TimeSpan.FromSeconds(1), next!.Timestamp - previous!.Timestamp);
        Assert.Equal(new DateTime(2026, 10, 4, 23, 59, 59, DateTimeKind.Utc), previous.Timestamp);
        Assert.Equal(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), next.Timestamp);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
