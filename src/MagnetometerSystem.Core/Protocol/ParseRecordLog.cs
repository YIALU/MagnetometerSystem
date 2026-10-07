using System.Text;

namespace MagnetometerSystem.Core.Protocol;

/// <summary>一条解析记录的结论。</summary>
public enum ParseOutcome
{
    /// <summary>帧完整且校验通过，已产生读数。</summary>
    Accepted,
    /// <summary>候选帧未通过长度、帧尾、固定值、校验或数值检查，已重新同步。</summary>
    Rejected,
    /// <summary>不属于任何帧的字节（噪声、帧起点前的残留、表头 / 注释 / 设备响应）被丢弃。</summary>
    Skipped,
}

/// <summary>
/// 一条解析记录。连续通过的帧、连续丢弃的字节合并为一条（FrameCount / ByteCount 累加），
/// 避免高频数据把记录刷满；被拒绝的帧每帧一条。
/// Preview 是这一条第一帧（或被拒绝的候选帧）的开头若干字节：二进制为 HEX，文本协议为原文。
/// </summary>
public sealed record ParseRecord(
    ParseOutcome Outcome,
    DateTime FirstTime,
    DateTime LastTime,
    long FrameCount,
    long ByteCount,
    string Detail,
    string? Preview);

/// <summary>
/// 解析器内部的有界记录缓冲。解析器在 Feed / TryParse 中写入，调用方在访问解析器的同一把锁内 <see cref="Drain"/>。
/// 两次 Drain 之间最多保留 <see cref="Capacity"/> 条，超出时丢弃最旧的并计入 <see cref="DroppedCount"/>，不会无界增长。
/// 连续通过的帧只更新计数，不为每帧分配对象。非线程安全。
/// </summary>
public sealed class ParseRecordLog(int capacity = ParseRecordLog.DefaultCapacity)
{
    public const int DefaultCapacity = 200;
    private const int PreviewBytes = 24;

    private readonly Queue<ParseRecord> _pending = new();

    // 当前正在合并的一段（通过或丢弃）；结论或说明变化、或 Drain 时收尾成记录。
    private ParseOutcome _runOutcome;
    private bool _runOpen;
    private DateTime _runFirst, _runLast;
    private long _runFrames, _runBytes;
    private string _runDetail = "";
    private string? _runPreview;

    public int Capacity { get; } = Math.Max(1, capacity);

    /// <summary>因调用方未及时 Drain 而丢弃的记录数。</summary>
    public long DroppedCount { get; private set; }

    /// <summary>一帧通过。只在一段的第一帧生成预览。</summary>
    public void Accepted(int bytes, int channels, ReadOnlySpan<byte> frame, bool hex)
    {
        var detail = $"{channels} 通道";
        if (_runOpen && _runOutcome == ParseOutcome.Accepted && _runDetail == detail)
        {
            _runFrames++;
            _runBytes += bytes;
            _runLast = DateTime.Now;
            return;
        }
        OpenRun(ParseOutcome.Accepted, bytes, detail, Preview(frame, hex), frames: 1);
    }

    /// <summary>一个候选帧被拒绝。<paramref name="bytes"/> 是候选帧长度（可能大于实际丢弃的字节数）。</summary>
    public void Rejected(int bytes, string reason, ReadOnlySpan<byte> candidate, bool hex)
    {
        CloseRun();
        var now = DateTime.Now;
        Add(new ParseRecord(ParseOutcome.Rejected, now, now, 1, bytes, reason, Preview(candidate, hex)));
    }

    /// <summary>丢弃不属于任何帧的字节；说明相同的连续丢弃合并为一条。</summary>
    public void Skipped(int bytes, string detail)
    {
        if (bytes <= 0) return;
        if (_runOpen && _runOutcome == ParseOutcome.Skipped && _runDetail == detail)
        {
            _runBytes += bytes;
            _runLast = DateTime.Now;
            return;
        }
        OpenRun(ParseOutcome.Skipped, bytes, detail, preview: null, frames: 0);
    }

    /// <summary>取出自上次以来的记录（包括正在合并的一段）。</summary>
    public IReadOnlyList<ParseRecord> Drain()
    {
        CloseRun();
        if (_pending.Count == 0) return [];
        var records = _pending.ToArray();
        _pending.Clear();
        return records;
    }

    public void Clear()
    {
        _runOpen = false;
        _pending.Clear();
    }

    private void OpenRun(ParseOutcome outcome, int bytes, string detail, string? preview, long frames)
    {
        CloseRun();
        _runOpen = true;
        _runOutcome = outcome;
        _runFirst = _runLast = DateTime.Now;
        _runFrames = frames;
        _runBytes = bytes;
        _runDetail = detail;
        _runPreview = preview;
    }

    private void CloseRun()
    {
        if (!_runOpen) return;
        _runOpen = false;
        Add(new ParseRecord(_runOutcome, _runFirst, _runLast, _runFrames, _runBytes, _runDetail, _runPreview));
    }

    private void Add(ParseRecord record)
    {
        _pending.Enqueue(record);
        while (_pending.Count > Capacity)
        {
            _pending.Dequeue();
            DroppedCount++;
        }
    }

    /// <summary>预览：HEX 取前 16 字节 … 末 4 字节；文本取可打印字符，最多 80 个。</summary>
    public static string? Preview(ReadOnlySpan<byte> bytes, bool hex)
    {
        if (bytes.IsEmpty) return null;
        if (hex)
        {
            return bytes.Length <= PreviewBytes ? Spaced(bytes) : $"{Spaced(bytes[..16])} … {Spaced(bytes[^4..])}";
        }
        var text = Encoding.ASCII.GetString(bytes).TrimEnd('\r', '\n');
        var sbText = new StringBuilder(Math.Min(text.Length, 80));
        foreach (var ch in text)
        {
            if (sbText.Length >= 80) { sbText.Append('…'); break; }
            sbText.Append(ch switch { '\r' => '␍', '\n' => '␊', '\t' => ' ', < ' ' or > '~' => '·', _ => ch });
        }
        return sbText.ToString();
    }

    private static string Spaced(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }
}
