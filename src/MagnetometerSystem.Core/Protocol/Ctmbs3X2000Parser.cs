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
/// payload = " HHMMSS 台站码 仪器ID 采样率 4 ch1..ch4码 ch1..ch4值"，取末 4 数值作 D/H/Z/T。
/// 非数据帧（$ack / $err / 登录返回的仪器 ID 尾巴 / 非法长度字段）静默跳过，不产读数。
/// </summary>
public class Ctmbs3X2000Parser : IDataParser, IParserDiagnostics
{
    public long RejectedFrameCount { get; private set; }
    public string? LastError { get; private set; }
    private readonly ByteRingBuffer _ring = new(131072);

    private static readonly byte[] AckTail = Encoding.ASCII.GetBytes("\nack\n");

    public Ctmbs3X2000Parser(ProtocolConfig config)
    {
        // 配置保留以备将来按通道命名/缩放；当前帧格式由协议固定，无须读取 Segments。
        _ = config;
    }

    public void Feed(byte[] data, int offset, int count)
    {
        if (count <= 0) return;
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

            // 完整数据帧字节数：'$' + digits + '\n' + L + '\nack\n'
            int frameSize = 1 + digitCount + 1 + len + AckTail.Length;
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

        // 找到 '$' 之后的第一个 '\n'
        int nl = _ring.IndexOf((byte)'\n');
        if (nl < 0) return false; // 不完整，等更多数据

        // 消费 '$'..'\n'（含）
        _ring.Skip(nl + 1);
        return true;
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

    private static bool TryDecodePayload(byte[] frame, int digitCount, int len, out MagnetometerReading? reading)
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

        // 取末 4 个数值字段作 D/H/Z/T
        var tokens = payload.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4) return false;
        var values = new double[4];
        int base0 = tokens.Length - 4;
        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(tokens[base0 + i], NumberStyles.Float,
                CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
                return false;
        }

        // 时间戳取首个字段 HHMMSS（UTC）
        var ts = TryParseHhmmss(tokens[0]) ?? DateTime.UtcNow;

        reading = new MagnetometerReading
        {
            Timestamp = ts,
            ChannelValues = values,
        };
        return true;
    }

    private static DateTime? TryParseHhmmss(string tok)
    {
        if (tok.Length != 6 || !int.TryParse(tok, out int n)) return null;
        int hh = n / 10000, mm = n / 100 % 100, ss = n % 100;
        if (hh > 23 || mm > 59 || ss > 59) return null;
        return DateTime.UtcNow.Date + new TimeSpan(hh, mm, ss);
    }

    private void Reject(string message) { RejectedFrameCount++; LastError = message; }
}
