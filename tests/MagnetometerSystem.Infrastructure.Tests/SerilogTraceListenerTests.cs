using System.Diagnostics;
using MagnetometerSystem.Infrastructure.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MagnetometerSystem.Infrastructure.Tests;

public sealed class SerilogTraceListenerTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
        public string[] Messages() { lock (Events) return Events.Select(e => e.RenderMessage()).ToArray(); }
    }

    private static (SerilogTraceListener Listener, CollectingSink Sink, Action<TimeSpan> Advance) Create(
        LogEventLevel minimum = LogEventLevel.Information)
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration().MinimumLevel.Is(minimum).WriteTo.Sink(sink).CreateLogger();
        var now = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        var listener = new SerilogTraceListener(logger, () => now);
        return (listener, sink, delta => now += delta);
    }

    private static void Emit(TraceListener listener, TraceEventType type, string message)
        => listener.TraceEvent(null, "test", type, 0, message, null);

    [Fact]
    public void TraceErrorFromCoreReachesLogFile()
    {
        var (listener, sink, _) = Create();
        var marker = "保存失败-" + Guid.NewGuid().ToString("N");
        Trace.Listeners.Add(listener);
        try { Trace.TraceError($"{marker}: disk full"); Trace.TraceWarning($"{marker}: busy"); }
        finally { Trace.Listeners.Remove(listener); }

        var mine = sink.Events.Where(e => e.RenderMessage().Contains(marker)).ToList();
        Assert.Equal([LogEventLevel.Error, LogEventLevel.Warning], mine.Select(e => e.Level));
        Assert.Equal($"{marker}: disk full", mine[0].RenderMessage());
    }

    [Fact]
    public void VerboseAndWriteLineStayOutOfInformationLog()
    {
        var (listener, sink, _) = Create();
        Emit(listener, TraceEventType.Verbose, "调试细节");
        listener.WriteLine("Trace.WriteLine 输出");
        Emit(listener, TraceEventType.Information, "连接状态: 已连接");
        Assert.Equal(["连接状态: 已连接"], sink.Messages());
    }

    [Fact]
    public void RepeatedMessagesAreCappedPerMinuteAndSummarized()
    {
        var (listener, sink, advance) = Create();
        for (int i = 0; i < 20; i++)
        {
            Emit(listener, TraceEventType.Error, $"[ReadSegmentValue] ch={i} type=Float bytes=4 ex=bad");
            advance(TimeSpan.FromSeconds(1));
        }
        Assert.Equal(SerilogTraceListener.MaxPerWindow, sink.Events.Count);

        advance(SerilogTraceListener.Window);
        Emit(listener, TraceEventType.Error, "[ReadSegmentValue] ch=99 type=Float bytes=4 ex=bad");
        var messages = sink.Messages();
        Assert.Equal(SerilogTraceListener.MaxPerWindow + 2, messages.Length);
        Assert.Contains("另有 15 条", messages[^2]);
        Assert.Contains("[ReadSegmentValue]", messages[^2]);
        Assert.Equal("[ReadSegmentValue] ch=99 type=Float bytes=4 ex=bad", messages[^1]);
        Assert.Equal(LogEventLevel.Error, sink.Events[^2].Level);
    }

    [Fact]
    public void FloodOfOneKindDoesNotHideOtherErrors()
    {
        var (listener, sink, _) = Create();
        for (int i = 0; i < 50; i++) Emit(listener, TraceEventType.Error, $"[PublishReading] 订阅者异常已隔离: {i}");
        Emit(listener, TraceEventType.Error, "保存失败，保留 100 条待重试");
        Assert.Equal("保存失败，保留 100 条待重试", sink.Messages()[^1]);
    }

    [Fact]
    public void DisposeWritesPendingSummary()
    {
        var (listener, sink, _) = Create();
        for (int i = 0; i < 8; i++) Emit(listener, TraceEventType.Warning, "busy/locked，50ms 后重试");
        listener.Dispose();
        var messages = sink.Messages();
        Assert.Equal(SerilogTraceListener.MaxPerWindow + 1, messages.Length);
        Assert.Contains("另有 3 条", messages[^1]);
    }

    [Fact]
    public void MessageWithBracesIsWrittenVerbatim()
    {
        var (listener, sink, _) = Create();
        listener.TraceEvent(null, "test", TraceEventType.Error, 0, "协议 {\"a\":1} 解析失败", null);
        listener.TraceEvent(null, "test", TraceEventType.Error, 0, "第 {0} 帧", 7);
        Assert.Equal(["协议 {\"a\":1} 解析失败", "第 7 帧"], sink.Messages());
    }
}
