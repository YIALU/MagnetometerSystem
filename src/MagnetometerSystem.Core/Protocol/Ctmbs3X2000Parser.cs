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
/// 非数据帧（$ack / $err / 登录返回的仪器 ID 尾巴 / 非法长度字段）静默跳过，不产读数。
/// </summary>
public class Ctmbs3X2000Parser : IDataParser, IParserDiagnostics
{
    public long RejectedFrameCount { get; private set; }
    public string? LastError { get; private set; }
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
                _ring.Skip(_ring.Count);
                return false;
            }
            if (dollar > 0)
                _ring.Skip(dollar); // 丢弃 '$' 之前的垃圾（仪器 ID 尾巴等）

            // '$' 之后至少要有 "L\n" + 帧体。先尝试识别简单响应。
            if (ConsumeSimpleResponse())
                continue; // 跳过 $ack/$err 等，继续找下一帧

            var beforeLength = _ring.Count;
            if (!TryReadLength(out int len, out int digitCount))
            {
                if (_ring.Count < beforeLength) { Reject("CTMBS 长度字段无效"); continue; }
                return false;
            }

            // 重复长度是帧体的首个锚点。已到达的每个字节立即核验，不能被
            // "$99999\n" 等坏头拖住，等满其声明的大帧后再吞掉后续合法数据。
            int bodyStart = 1 + digitCount + 1;
            bool prefixMatches = len > digitCount;
            for (int i = 0; prefixMatches && i < digitCount && bodyStart + i < _ring.Count; i++)
                prefixMatches = _ring.Peek(bodyStart + i) == _ring.Peek(1 + i);
            if (!prefixMatches)
            {
                _ring.Skip(1);
                Reject("CTMBS 帧体重复长度无效");
                continue;
            }

            // 完整数据帧字节数：'$' + digits + '\n' + L + '\nack\n'
            int frameSize = 1 + digitCount + 1 + len + AckTail.Length;
            // 重复长度也可能一起损坏。该文本协议的载荷及尾部不含 '$'，
            // 新帧头可以直接否定尚未结束的旧候选，无须等到它声称的巨大长度。
            // 只检查候选范围；合法帧之后粘连的下一 '$' 必须留给下一次解析。
            int nextHeader = -1;
            for (int i = bodyStart + digitCount; i < Math.Min(_ring.Count, frameSize); i++)
            {
                if (_ring.Peek(i) != (byte)'$') continue;
                nextHeader = i;
                break;
            }
            if (nextHeader >= 0)
            {
                _ring.Skip(nextHeader);
                Reject("CTMBS 帧长度与下一帧头冲突，已重新同步");
                continue;
            }
            if (_ring.Count < frameSize)
                return false; // 帧体未到齐

            // 取出完整帧（消费掉），解析 payload
            var frame = _ring.ReadBytes(frameSize);
            if (TryDecodePayload(frame, digitCount, len, out reading))
                return true; // 命中数据帧
            Reject("CTMBS 数据帧载荷或帧尾无效");
            // 不是数据帧（长度合法但载荷非数据）——已消费，继续找下一帧
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
        // 第二字节非数字 → 不是 $<L> 帧，按简单响应处理
        if (second is >= (byte)'0' and <= (byte)'9') return false;

        // 简单响应中不会包含 '$'。先遇到新的帧头说明前一响应损坏，
        // 必须保留新帧，不能把它的长度行当成旧响应的换行一起消费。
        for (int i = 1; i < _ring.Count; i++)
        {
            if (_ring.Peek(i) == (byte)'$')
            {
                _ring.Skip(i);
                Reject("CTMBS 简单响应缺少结束符，已重新同步");
                return true;
            }
            if (_ring.Peek(i) == (byte)'\n')
            {
                _ring.Skip(i + 1);
                return true;
            }
        }
        return false; // 不完整，等更多数据
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

    private bool TryDecodePayload(byte[] frame, int digitCount, int len, out MagnetometerReading? reading)
    {
        reading = null;
        // frame = '$' + digits + '\n' + (len 字节 = digits+payload) + '\nack\n'
        int bodyStart = 1 + digitCount + 1;
        int bodyEnd = bodyStart + len;
        // 校验尾部 \nack\n
        if (bodyEnd + AckTail.Length > frame.Length) return false;
        for (int i = 0; i < AckTail.Length; i++)
            if (frame[bodyEnd + i] != AckTail[i]) return false;

        // len 字节 = digits(ASCII) + payload；剥离开头 digits
        int payloadStart = bodyStart + digitCount;
        if (!frame.AsSpan(1, digitCount).SequenceEqual(frame.AsSpan(bodyStart, digitCount))) return false;
        int payloadLen = len - digitCount;
        if (payloadLen <= 0) return false;
        var payload = Encoding.ASCII.GetString(frame, payloadStart, payloadLen);

        // 普通命令响应与推送共用此包裹格式；状态、网络/通道参数等响应也可能
        // 以四个数字结尾。只有 §6 的完整单帧结构才属于实时测量，不能按尾字段猜测。
        var tokens = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 13
            || TryParseHhmmss(tokens[0], _receivedAtUtc) is not { } timestamp
            || tokens[1].Any(c => char.IsControl(c) || c == '$')
            || tokens[2].Any(c => char.IsControl(c) || c == '$')
            || !IsAsciiDigits(tokens[3])
            || !int.TryParse(tokens[4], NumberStyles.None, CultureInfo.InvariantCulture, out int channelCount)
            || channelCount != 4
            || tokens.Skip(5).Take(4).Any(code => !IsAsciiDigits(code)))
            return false;
        var values = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(tokens[9 + i], NumberStyles.Float,
                CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
                return false;
        }

        reading = new MagnetometerReading
        {
            Timestamp = timestamp,
            ChannelValues = values,
        };
        return true;
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
