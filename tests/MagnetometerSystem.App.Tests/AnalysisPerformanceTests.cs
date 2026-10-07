using System.Diagnostics;
using System.IO;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Database;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MagnetometerSystem.App.Tests;

/// <summary>只在设置 MAGNETOMETER_TEST_PERF=1 时运行；默认跳过，避免拖慢日常测试。</summary>
public sealed class PerfFactAttribute : FactAttribute
{
    public PerfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_PERF") != "1")
            Skip = "性能实测：设置 MAGNETOMETER_TEST_PERF=1 后运行（生成约百万条读数的临时数据库）。";
    }
}

public class AnalysisPerformanceTests(ITestOutputHelper output)
{
    /// <summary>
    /// 长会话：10 Hz × 8 通道 × 1,000,000 条（约 27.8 小时，约 56 个读取块），分析 1 个通道。
    /// 记录耗时和内存，供 REQ-005 的性能结论引用；断言只检查结果完整。
    /// </summary>
    [PerfFact]
    public async Task LongSession_OneChannel_TimeAndMemory()
    {
        const int count = 1_000_000, channels = 8;
        var path = Path.Combine(Path.GetTempPath(), $"analysis_perf_{Guid.NewGuid():N}.db");
        var db = new DatabaseInitializer(path); await db.InitializeAsync();
        var storage = new SqliteStorageService(db, new DataBus());
        try
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
            var id = await storage.StartSessionAsync("perf", new SensorConfig
            {
                Type = SensorType.Generic, SampleRate = 10, ChannelCountOverride = channels,
                ChannelNamesOverride = Enumerable.Range(0, channels).Select(i => $"CH{i}").ToArray(),
                ChannelUnitsOverride = Enumerable.Repeat("nT", channels).ToArray(),
            }, new ConnectionConfig());
            var write = Stopwatch.StartNew();
            for (int offset = 0; offset < count; offset += 20_000)
                await storage.SaveReadingsAsync(Enumerable.Range(offset, Math.Min(20_000, count - offset)).Select(i => new MagnetometerReading
                {
                    SessionId = id, Timestamp = start.AddMilliseconds(i * 100.0), SensorType = SensorType.Generic,
                    ChannelValues = Enumerable.Range(0, channels).Select(c => 50000 + c + Math.Sin(i * .01) + i * 1e-6).ToArray(),
                }).ToList());
            await storage.EndSessionAsync(id);
            using (var conn = new SqliteConnection(db.ConnectionString))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE sessions SET started_at = $s, ended_at = $e WHERE id = $id";
                cmd.Parameters.AddWithValue("$s", start.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$e", start.AddMilliseconds((count - 1) * 100.0).ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            output.WriteLine($"写入 {count:N0} 条：{write.Elapsed.TotalSeconds:F1} s，数据库 {new FileInfo(path).Length / 1024.0 / 1024:F0} MB");

            var vm = new AnalysisViewModel(storage);
            await vm.EnsureLoadedAsync();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var process = Process.GetCurrentProcess();
            process.Refresh();
            var workingBefore = process.WorkingSet64;
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var run = Stopwatch.StartNew();
            await vm.RunCommand.ExecuteAsync(null);
            run.Stop();
            process.Refresh();
            var info = GC.GetGCMemoryInfo();

            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(count, Assert.Single(vm.Results).Result.Quality.SampleCount);
            output.WriteLine($"分析 1 通道：{run.Elapsed.TotalSeconds:F1} s");
            output.WriteLine($"累计分配 {(GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1024.0 / 1024:F0} MB；"
                + $"工作集 {workingBefore / 1024.0 / 1024:F0} → {process.WorkingSet64 / 1024.0 / 1024:F0} MB（峰值 {process.PeakWorkingSet64 / 1024.0 / 1024:F0} MB）；"
                + $"GC 堆 {info.HeapSizeBytes / 1024.0 / 1024:F0} MB");
        }
        finally
        {
            storage.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}
