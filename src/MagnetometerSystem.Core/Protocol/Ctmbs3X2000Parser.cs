using System.Globalization;
using System.Text;
using MagnetometerSystem.Core.Helpers;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Protocol;

/// <summary>
/// CTMBS-3-X2000 台站式三分量实时数据解析器。
/// 帧格式（§3.2(a) 带数据响应 + §6 实时推送）：
/// <code>$&lt;L&gt;\n&lt;L digits&gt;&lt;payload&gt;\nack\n</code>
/// 其中 &lt;L&gt; = digits(L) + payload 字节数（自参考，§3.3）。
/// payload = " HHMMSS 台站码 仪器ID 采样率 4 ch1..ch4码 ch1..ch4值"，完整匹配实时数据结构后读取 D/H/Z/T。
/// 合法命令响应（$ack / $err / 带数据响应）不产读数；损坏封装和测量字段仍提供诊断。
/// </summary>
public class Ctmbs3X2000Parser : IDataParser, IParserDiagnostics
{
    public long RejectedFrameCount { get; private set; }
    public string? LastError { get; private set; }
    public ParseRecordLog Records { get; } = new();
    private readonly ByteRingBuffer _ring = new(131072);
    private readonly TimeProvider _timeProvider;
    private DateTime _receivedAtUtc;

    private static readonly byte[] AckTail = Encoding.ASCII.GetBytes("\nack\n");

    public Ctmbs3X2000Parser(ProtocolConfig config, TimeProvider? timeProvider = null)
    {
        // 配置保留以备将来按通道命名/缩放；当前帧格式由协议固定，无须读取 Segments。
        _ = config;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Feed(byte[] data, int offset, int count)
    {
        if (count <= 0) return;
        _receivedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        if (count > _ring.FreeSpace)
        {
            Records.Skipped(_ring.Count, "接收缓冲区溢出，丢弃未成帧的数据");
            _ring.Clear();
            Reject("CTMBS 接收缓冲区溢出");
            if (count > _ring.Capacity) { offset += count - _ring.Capacity; count = _ring.Capacity; }
        }
        _ring.Write(data, offset, count);
    }

    public void Reset() => _ring.Clear();

    public bool TryParse(out MagnetometerReading? reading)
    {
        reading = null;

        while (true)
        {
            // 跳到下一个 '$'
            int dollar = _ring.IndexOf((byte)'$');
            if (dollar < 0)
            {
                // 缓冲里没有帧起始：全部丢弃（每帧必以 '$' 开头，无残留半帧风险）
                Records.Skipped(_ring.Count, "未找到帧起点 '$'，丢弃");
                _ring.Skip(_ring.Count);
                return false;
            }
            if (dollar > 0)
            {
                Records.Skipped(dollar, "丢弃 '$' 之前的字节");
                _ring.Skip(dollar);
            } // 丢弃 '$' 之前的垃圾（仪器 ID 尾巴等）

            // '$' 之后至少要有 "L\n" + 帧体。先尝试识别简单响应。
            if (ConsumeSimpleResponse())
                continue; // 跳过 $ack/$err 等，继续找下一帧

            var beforeLength = _ring.Count;
            var lengthLine = PeekBytes(LineLength(32));
            if (!TryReadLength(out int len, out int digitCount))
            {
                if (_ring.Count < beforeLength)
                {
                    Reject("CTMBS 长度字段无效");
                    Records.Rejected(lengthLine.Length, "长度字段无效", lengthLine, hex: false);
                    continue;
                }
                return false;
            }

            // 重复长度是帧体的首个锚点。已到达的每个字节立即核验，不能被
            // "$99999\n" 等坏头拖住，等满其声明的大帧后再吞掉后续合法数据。
            int bodyStart = 1 + digitCount + 1;
            // §3.2 pmr+clock is the sole exception: $14\nYYYYMMDDHHMMSS\nack\n,
            // with no repeated 14. Its total size still matches an ordinary L=14 response.
            bool mayBeClockResponse = len == 14 && digitCount == 2;
            bool prefixMatches = len > digitCount;
            for (int i = 0; prefixMatches && i < digitCount && bodyStart + i < _ring.Count; i++)
                prefixMatches = _ring.Peek(bodyStart + i) == _ring.Peek(1 + i);
            if (!prefixMatches && !mayBeClockResponse)
            {
                var head = PeekBytes(Math.Min(_ring.Count, bodyStart + digitCount));
                _ring.Skip(1);
                Reject("CTMBS 帧体重复长度无效");
                Records.Rejected(head.Length, "帧体重复长度无效", head, hex: false);
                continue;
            }

            // 完整数据帧字节数：'$' + digits + '\n' + L + '\nack\n'
            int frameSize = 1 + digitCount + 1 + len + AckTail.Length;
            // 重复长度也可能一起损坏。该文本协议的载荷及尾部不含 '$'，
            // 新帧头可以直接否定尚未结束的旧候选，无须等到它声称的巨大长度。
            // 只检查候选范围；合法帧之后粘连的下一 '$' 必须留给下一次解析。
            int nextHeader = -1;
            for (int i = bodyStart; i < Math.Min(_ring.Count, frameSize); i++)
            {
                if (_ring.Peek(i) != (byte)'$') continue;
                nextHeader = i;
                break;
            }
            if (nextHeader >= 0)
            {
                var broken = PeekBytes(nextHeader);
                Records.Rejected(broken.Length, "帧长度与下一帧头冲突", broken, hex: false);
                _ring.Skip(nextHeader);
                Reject("CTMBS 帧长度与下一帧头冲突，已重新同步");
                continue;
            }
            if (_ring.Count < frameSize)
                return false; // 帧体未到齐

            // 取出完整帧（消费掉），解析 payload
            var frame = _ring.ReadBytes(frameSize);
            var kind = DecodePayload(frame, digitCount, len, out reading);
            if (kind == PayloadKind.Measurement)
            {
                Records.Accepted(frame.Length, reading!.ChannelValues.Length, frame, hex: false);
                return true;
            }
            if (kind == PayloadKind.Invalid)
            {
                Reject("CTMBS 数据帧载荷或帧尾无效");
                Records.Rejected(frame.Length, "载荷或帧尾无效", frame, hex: false);
            }
            else
            {
                Records.Skipped(frame.Length, "设备带数据响应（非测量帧）");
            }
            // 合法命令响应不产测量、不计解析错误，继续寻找下一帧。
        }
    }

    // ---- 简单响应跳过 ----

    private bool ConsumeSimpleResponse()
    {
        // 缓冲首字节为 '$'。按固定长度消费 $ack\n / $err\n / $start_push\n / $stop_push\n。
        // §3.2 特殊响应：$ack/$err 为 5 字节；start_push/stop_push 为内部标识（通常不外发，
        // 防御性消费）。登录成功 $ack\n<仪器ID> 无尾部换行：这里只消费 $ack\n，
        // 仪器 ID 作为 '$' 前垃圾在下一轮被 Skip 丢弃。
        if (_ring.Count < 2) return false;
        byte second = _ring.Peek(1);
        // 第二字节非数字 → 不是 $<L> 帧，但仍须完整匹配已定义的简单响应。
        if (second is >= (byte)'0' and <= (byte)'9') return false;

        // 简单响应中不会包含 '$'。先遇到新的帧头说明前一响应损坏，
        // 必须保留新帧，不能把它的长度行当成旧响应的换行一起消费。
        for (int i = 1; i < _ring.Count; i++)
        {
            if (_ring.Peek(i) == (byte)'$')
            {
                var broken = PeekBytes(i);
                Records.Rejected(broken.Length, "简单响应缺少结束符", broken, hex: false);
                _ring.Skip(i);
                Reject("CTMBS 简单响应缺少结束符，已重新同步");
                return true;
            }
            if (_ring.Peek(i) == (byte)'\n')
            {
                var response = _ring.PeekString(i + 1);
                var bytes = PeekBytes(i + 1);
                _ring.Skip(i + 1);
                if (response is ("$ack\n" or "$err\n" or "$start_push\n" or "$stop_push\n"))
                    Records.Skipped(bytes.Length, "设备响应（$ack / $err 等，非数据帧）");
                else
                    Records.Rejected(bytes.Length, "未知简单响应", bytes, hex: false);
                if (response is not ("$ack\n" or "$err\n" or "$start_push\n" or "$stop_push\n"))
                    Reject("CTMBS 未知简单响应，已重新同步");
                return true;
            }
        }
        return false; // 不完整，等更多数据
    }

    /// <summary>预览用：从读指针起取 n 字节（不消费）。</summary>
    private byte[] PeekBytes(int n)
    {
        n = Math.Clamp(n, 0, _ring.Count);
        var bytes = new byte[n];
        for (int i = 0; i < n; i++) bytes[i] = _ring.Peek(i);
        return bytes;
    }

    /// <summary>从读指针到第一个换行（含）的长度，最多 max 字节。</summary>
    private int LineLength(int max)
    {
        int nl = _ring.IndexOf((byte)'\n');
        return Math.Min(nl < 0 ? _ring.Count : nl + 1, max);
    }

    // ---- 长度行 ----

    private bool TryReadLength(out int len, out int digitCount)
    {
        len = 0;
        digitCount = 0;
        // 首字节 '$'，其后到第一个 '\n' 之间应为十进制数字
        int nl = _ring.IndexOf((byte)'\n');
        if (nl < 1) return false; // 没有换行或 '$' 紧跟换行（非法）

        // 读 '$' 之后、'\n' 之前的数字（PeekString 从读指针起算会包含 '$'，故逐字节 Peek）
        var sb = new StringBuilder();
        for (int i = 1; i < nl; i++)
        {
            byte b = _ring.Peek(i);
            if (b < (byte)'0' || b > (byte)'9')
            {
                _ring.Skip(1); // 非法长度字段：消费 '$'，外层重同步
                return false;
            }
            sb.Append((char)b);
        }
        var digits = sb.ToString();

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out len) || len <= 0 || len > _ring.Capacity - 32)
        {
            _ring.Skip(1); // 跳过 '$'，重同步
            len = 0;
            return false;
        }

        digitCount = digits.Length;
        return true;
    }

    // ---- 载荷解析 ----

    private enum PayloadKind { Invalid, OtherResponse, Measurement }

    private PayloadKind DecodePayload(byte[] frame, int digitCount, int len, out MagnetometerReading? reading)
    {
        reading = null;
        int bodyStart = 1 + digitCount + 1;
        int bodyEnd = bodyStart + len;
        if (bodyEnd + AckTail.Length > frame.Length) return PayloadKind.Invalid;
        for (int i = 0; i < AckTail.Length; i++)
            if (frame[bodyEnd + i] != AckTail[i]) return PayloadKind.Invalid;

        if (len == 14 && digitCount == 2)
        {
            var clock = Encoding.ASCII.GetString(frame, bodyStart, len);
            if (IsAsciiDigits(clock) && DateTime.TryParseExact(clock, "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return PayloadKind.OtherResponse;
            // Invalid/non-clock content may still be a standard repeated-length response.
            // In particular, a body beginning with 14 is not uniquely identifiable as a clock.
        }

        int payloadStart = bodyStart + digitCount;
        if (!frame.AsSpan(1, digitCount).SequenceEqual(frame.AsSpan(bodyStart, digitCount))) return PayloadKind.Invalid;
        int payloadLen = len - digitCount;
        if (payloadLen <= 0) return PayloadKind.Invalid;
        var payload = Encoding.ASCII.GetString(frame, payloadStart, payloadLen);
        var tokens = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // §3.2 的合法封装也承载 ste、pmr 和扩展命令响应（pmr+1m 同样有13字段）。
        // 没有请求标识，不能把未知响应的业务字段当成坏测量帧；只严格诊断带测量结构锚点的载荷。
        if (!LooksLikeMeasurement(tokens)) return PayloadKind.OtherResponse;
        if (tokens.Length < 13 || (tokens.Length - 9) % 4 != 0
            || TryParseHhmmss(tokens[0], _receivedAtUtc) is not { } timestamp
            || tokens[1].Any(c => char.IsControl(c) || c == '$')
            || tokens[2].Any(c => char.IsControl(c) || c == '$')
            || !IsAsciiDigits(tokens[3])
            || !int.TryParse(tokens[4], NumberStyles.None, CultureInfo.InvariantCulture, out int channelCount)
            || channelCount != 4
            || tokens.Skip(5).Take(4).Any(code => !IsAsciiDigits(code)))
            return PayloadKind.Invalid;
        // §6 identifies D/H/Z/T by measurement code, not wire position.
        // Require each known component exactly once before associating any value with a channel.
        Span<int> channelIndices = stackalloc int[4];
        int seenChannels = 0;
        for (int i = 0; i < channelIndices.Length; i++)
        {
            int channel = tokens[5 + i] switch
            {
                "3125" => 0, // D
                "3124" => 1, // H
                "3123" => 2, // Z
                "3129" => 3, // T
                _ => -1
            };
            if (channel < 0 || (seenChannels & (1 << channel)) != 0) return PayloadKind.Invalid;
            seenChannels |= 1 << channel;
            channelIndices[i] = channel;
        }
        var values = new double[4];
        for (int i = 9; i < tokens.Length; i++)
        {
            if (!double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value)) return PayloadKind.Invalid;
            if (i < 13) values[channelIndices[i - 9]] = value;
        }
        // §5.1 dat+5 可以在同一头部后包含多组四通道值。这是合法批量响应，不是实时单帧。
        if (tokens.Length > 13) return PayloadKind.OtherResponse;
        reading = new MagnetometerReading { Timestamp = timestamp, ChannelValues = values };
        return PayloadKind.Measurement;
    }

    private static bool LooksLikeMeasurement(string[] tokens)
    {
        if (tokens.Length < 5) return false;
        bool timeShape = tokens[0].Length == 6;
        bool channelCount = int.TryParse(tokens[4], NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count == 4;
        bool channelCodes = tokens.Length >= 9 && tokens.Skip(5).Take(4).All(IsAsciiDigits);
        // 两个独立锚点保留单字段损坏的诊断：时刻、通道数或某个通道码损坏仍会进入严格校验。
        return (timeShape && channelCount) || (timeShape && channelCodes) || (channelCount && channelCodes);
    }

    private static DateTime? TryParseHhmmss(string tok, DateTime receivedAtUtc)
    {
        if (tok.Length != 6 || !IsAsciiDigits(tok)
            || !int.TryParse(tok, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return null;
        int hh = n / 10000, mm = n / 100 % 100, ss = n % 100;
        if (hh > 23 || mm > 59 || ss > 59) return null;
        // 帧只含 UTC 时分秒；按接收时刻选择最近的日期，避免午夜附近的
        // 传输延迟或小量时钟偏差把 23:59:59 / 00:00:00 错配到相隔一天。
        var timestamp = receivedAtUtc.Date + new TimeSpan(hh, mm, ss);
        var delta = timestamp - receivedAtUtc;
        if (delta > TimeSpan.FromHours(12)) timestamp = timestamp.AddDays(-1);
        else if (delta < TimeSpan.FromHours(-12)) timestamp = timestamp.AddDays(1);
        return timestamp;
    }

    private static bool IsAsciiDigits(string value) =>
        value.Length > 0 && value.All(c => c is >= '0' and <= '9');

    private void Reject(string message) { RejectedFrameCount++; LastError = message; }
}
