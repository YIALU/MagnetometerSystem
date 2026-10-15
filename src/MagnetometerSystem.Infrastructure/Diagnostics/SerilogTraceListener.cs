using System.Diagnostics;
using System.Globalization;
using Serilog;
using Serilog.Events;

namespace MagnetometerSystem.Infrastructure.Diagnostics;

/// <summary>
/// 把 <see cref="Trace"/> 输出转写进日志文件。Core 与 Infrastructure 不依赖 Serilog，
/// 连接、解析、分发和保存的错误都经 <c>Trace.TraceError/TraceWarning</c> 报出；
/// 不接这个监听器时，发布版里这些消息只到调试器，日志文件里看不到。
/// <para>
/// 同一类消息（首行中的数字忽略后相同）每分钟最多写 <see cref="MaxPerWindow"/> 条，
/// 其余只计数，下一次同类消息或关闭时补写一行汇总，避免逐帧错误把日志撑满。
/// </para>
/// </summary>
public sealed class SerilogTraceListener : TraceListener
{
    public const int MaxPerWindow = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private const int MaxKeys = 256;
    private const int MaxKeyLength = 120;

    private readonly ILogger _logger;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    private sealed class Bucket(DateTime start, LogEventLevel level, string sample)
    {
        public DateTime Start { get; } = start;
        public LogEventLevel Level { get; } = level;
        public string Sample { get; } = sample;
        public int Written { get; set; } = 1;
        public int Suppressed { get; set; }
    }

    public SerilogTraceListener(ILogger logger, Func<DateTime>? utcNow = null)
    {
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        Name = "Serilog";
    }

    public override bool IsThreadSafe => true;

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        => Forward(eventType, message ?? "");

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id,
        string? format, params object?[]? args)
        => Forward(eventType, args is { Length: > 0 } && format is not null
            ? string.Format(CultureInfo.InvariantCulture, format, args)
            : format ?? "");

    public override void Fail(string? message, string? detailMessage)
        => Forward(TraceEventType.Error, string.IsNullOrEmpty(detailMessage) ? $"断言失败: {message}" : $"断言失败: {message} {detailMessage}");

    // Trace.Write/WriteLine 与 Debug.* 只用于调试输出，不进日志文件。
    public override void Write(string? message) { }
    public override void WriteLine(string? message) { }

    /// <summary>把仍在计数中的省略条数写成汇总行。关闭日志前调用，免得最后一段洪泛只留下前几条。</summary>
    public void FlushSuppressed()
    {
        List<Bucket> pending;
        lock (_gate)
        {
            pending = _buckets.Values.Where(b => b.Suppressed > 0).ToList();
            _buckets.Clear();
        }
        foreach (var bucket in pending) WriteSummary(bucket);
    }

    public override void Flush() => FlushSuppressed();

    protected override void Dispose(bool disposing)
    {
        if (disposing) FlushSuppressed();
        base.Dispose(disposing);
    }

    private void Forward(TraceEventType eventType, string message)
    {
        var level = eventType switch
        {
            TraceEventType.Critical => LogEventLevel.Fatal,
            TraceEventType.Error => LogEventLevel.Error,
            TraceEventType.Warning => LogEventLevel.Warning,
            TraceEventType.Information => LogEventLevel.Information,
            _ => LogEventLevel.Debug
        };
        if (!_logger.IsEnabled(level)) return;

        var now = _utcNow();
        var key = KeyOf(message);
        List<Bucket>? summaries = null;
        lock (_gate)
        {
            if (_buckets.TryGetValue(key, out var bucket))
            {
                var elapsed = now - bucket.Start;
                if (elapsed >= TimeSpan.Zero && elapsed < Window)
                {
                    if (bucket.Written >= MaxPerWindow) { bucket.Suppressed++; return; }
                    bucket.Written++;
                }
                else
                {
                    if (bucket.Suppressed > 0) (summaries ??= []).Add(bucket);
                    _buckets[key] = new Bucket(now, level, FirstLine(message));
                }
            }
            else
            {
                if (_buckets.Count >= MaxKeys)
                {
                    // 种类过多时整体清空，已省略的条数先写成汇总，不让字典无限增长。
                    (summaries ??= []).AddRange(_buckets.Values.Where(b => b.Suppressed > 0));
                    _buckets.Clear();
                }
                _buckets[key] = new Bucket(now, level, FirstLine(message));
            }
        }
        if (summaries is not null) foreach (var s in summaries) WriteSummary(s);
        _logger.Write(level, "{TraceMessage:l}", message);
    }

    private void WriteSummary(Bucket bucket) =>
        _logger.Write(bucket.Level, "上一分钟内另有 {Suppressed} 条同类消息未写入: {Sample:l}", bucket.Suppressed, bucket.Sample);

    private static string FirstLine(string message)
    {
        var end = message.IndexOfAny(['\r', '\n']);
        var line = end < 0 ? message : message[..end];
        return line.Length > MaxKeyLength ? line[..MaxKeyLength] : line;
    }

    internal static string KeyOf(string message)
    {
        // 连续数字合成一个 #，让 "ch=9" 与 "ch=10"、不同条数的同类消息归为一类。
        var line = FirstLine(message);
        var key = new System.Text.StringBuilder(line.Length);
        foreach (var c in line)
        {
            if (!char.IsAsciiDigit(c)) key.Append(c);
            else if (key.Length == 0 || key[^1] != '#') key.Append('#');
        }
        return key.ToString();
    }
}
