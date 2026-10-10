using System.Text;
using MagnetometerSystem.Core.Feedback;
using MagnetometerSystem.Infrastructure.Feedback;

namespace MagnetometerSystem.Infrastructure.Tests;

public sealed class FeedbackLogCollectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "feedback-logs-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

    public FeedbackLogCollectorTests() => Directory.CreateDirectory(_directory);
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private string Write(string name, string text, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private FeedbackLogCollector Collector(int maxBytes = FeedbackLogPayload.MaxTextBytes) => new(() => _directory, maxBytes,
        utcNow: () => Now, userName: "zhangsan", userProfile: @"C:\Users\zhangsan", machineName: "LAB-PC07");

    [Fact]
    public async Task CollectsRecentFilesOldestFirstAndSkipsOlderThanThreeDays()
    {
        Write("app-20261001.log", "很早的日志\n", Now.AddDays(-9));
        Write("app-20261009.log", "昨天的日志\n", Now.AddDays(-1));
        Write("app-20261010.log", "今天的日志\n", Now.AddMinutes(-1));
        Write("other.txt", "不是日志\n", Now);

        var text = await Collector().CollectAsync();

        Assert.NotNull(text);
        Assert.DoesNotContain("很早的日志", text);
        Assert.DoesNotContain("不是日志", text);
        Assert.True(text!.IndexOf("昨天的日志", StringComparison.Ordinal) < text.IndexOf("今天的日志", StringComparison.Ordinal));
        Assert.Contains("==== app-20261010.log ====", text);
    }

    [Fact]
    public async Task ReadsFileThatLoggerStillHoldsOpen()
    {
        var path = Path.Combine(_directory, "app-20261010.log");
        await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        var bytes = Encoding.UTF8.GetBytes("2026-10-10 12:00:00.000 [INF] 应用程序启动\n");
        await writer.WriteAsync(bytes); await writer.FlushAsync();
        File.SetLastWriteTimeUtc(path, Now);

        var text = await Collector().CollectAsync();

        Assert.Contains("应用程序启动", text);
    }

    [Fact]
    public async Task RedactsUserAndMachineNamesButKeepsConnectionDetails()
    {
        Write("app-20261010.log", string.Join('\n',
            @"[INF] 应用程序启动 日志目录 C:\Users\zhangsan\AppData\Local\MagnetometerSystem\logs",
            @"[INF] 数据库就绪: c:\users\ZhangSan\Documents\data.db",
            @"[WRN] 其他账户 D:\Users\lisi\x.json",
            "[INF] 当前用户 zhangsan 在 LAB-PC07 上运行，zhangsanfeng 不是同一个人",
            "[INF] 开始采集: TCP 192.168.1.100:5000，协议 磁梯度") + "\n", Now);

        var text = (await Collector().CollectAsync())!;

        Assert.DoesNotContain("zhangsan\\", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lisi", text);
        Assert.DoesNotContain("LAB-PC07", text);
        Assert.Contains(@"%USERPROFILE%\AppData\Local\MagnetometerSystem\logs", text);
        Assert.Contains(@"D:\Users\<用户>\x.json", text);
        Assert.Contains("当前用户 <用户> 在 <计算机> 上运行", text);
        Assert.Contains("zhangsanfeng", text);
        Assert.Contains("TCP 192.168.1.100:5000", text);
    }

    [Fact]
    public async Task OversizedLogsKeepNewestLinesWithinLimit()
    {
        var old = string.Concat(Enumerable.Range(0, 2000).Select(i => $"[INF] 较早的第 {i} 行\n"));
        var recent = string.Concat(Enumerable.Range(0, 2000).Select(i => $"[INF] 最新的第 {i} 行\n"));
        Write("app-20261009.log", old, Now.AddDays(-1));
        Write("app-20261010.log", recent, Now);

        var text = (await Collector(maxBytes: 20_000).CollectAsync())!;

        Assert.True(Encoding.UTF8.GetByteCount(text) <= 20_000);
        Assert.StartsWith("（更早的日志超过大小上限，已省略）", text);
        Assert.EndsWith("[INF] 最新的第 1999 行\n", text);
        Assert.DoesNotContain("较早的第", text);
        // 截断处从完整的一行开始，不留半行。
        var firstLog = text.Split('\n')[1];
        Assert.StartsWith("[INF] 最新的第 ", firstLog);
    }

    [Fact]
    public async Task MissingDirectoryOrNoRecentLogsReturnsNull()
    {
        Assert.Null(await new FeedbackLogCollector(() => null).CollectAsync());
        Assert.Null(await new FeedbackLogCollector(() => Path.Combine(_directory, "missing")).CollectAsync());
        Write("app-20261001.log", "很早的日志\n", Now.AddDays(-9));
        Assert.Null(await Collector().CollectAsync());
    }
}
