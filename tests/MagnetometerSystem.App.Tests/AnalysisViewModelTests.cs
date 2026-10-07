using System.Globalization;
using System.IO;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;
using MagnetometerSystem.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

public class AnalysisViewModelTests
{
    // 65 分钟、1 Hz：按 30 分钟分块读取时有两处块边界（1800 s、3600 s），边界点会被两块同时读到。
    private const int Seconds = 3900;
    private const int GapStart = 2000, GapEnd = 2100; // [2000, 2100) 秒没有数据
    private const string OddName = "通道,\"A\"";

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"analysis_{Guid.NewGuid():N}.db");
        public DatabaseInitializer Database { get; private set; } = null!;
        public SqliteStorageService Storage { get; private set; } = null!;
        public DateTime Start { get; } = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Local);
        public string SessionId { get; private set; } = "";
        public int ExpectedCount => Seconds + 1 - (GapEnd - GapStart);

        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            f.Database = new DatabaseInitializer(f._path);
            await f.Database.InitializeAsync();
            f.Storage = new SqliteStorageService(f.Database, new DataBus());
            f.SessionId = await f.Storage.StartSessionAsync("长会话", new SensorConfig
            {
                Type = SensorType.Generic, SampleRate = 1, ChannelCountOverride = 2,
                ChannelNamesOverride = ["Bx", OddName], ChannelUnitsOverride = ["nT", "°C"],
            }, new ConnectionConfig());
            var readings = Enumerable.Range(0, Seconds + 1).Where(i => i < GapStart || i >= GapEnd).Select(i => new MagnetometerReading
            {
                SessionId = f.SessionId,
                Timestamp = f.Start.AddSeconds(i),
                SensorType = SensorType.Generic,
                // 校正值故意写成 999：分析必须用原始值。
                ChannelValues = [999, 999],
                OriginalChannelValues = [0.001 * i, 5],
                IsOrthogonalityCorrected = true,
            }).ToList();
            await f.Storage.SaveReadingsAsync(readings);
            await f.Storage.EndSessionAsync(f.SessionId);
            // 会话的起止时间与读数时间对齐（存储服务按当前时间记录，这里改成数据所在的时段）。
            using var conn = new SqliteConnection(f.Database.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE sessions SET started_at = $s, ended_at = $e WHERE id = $id";
            cmd.Parameters.AddWithValue("$s", f.Start.ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$e", f.Start.AddSeconds(Seconds).ToUniversalTime().ToString("O"));
            cmd.Parameters.AddWithValue("$id", f.SessionId);
            cmd.ExecuteNonQuery();
            return f;
        }

        public void Dispose()
        {
            Storage.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
        }
    }

    private static async Task<AnalysisViewModel> ReadyAsync(IDataStorageService storage)
    {
        var vm = new AnalysisViewModel(storage);
        await vm.EnsureLoadedAsync();
        Assert.NotNull(vm.SelectedSession);
        foreach (var c in vm.Channels) c.IsSelected = true;
        return vm;
    }

    [Fact]
    public Task Run_ReadsAcrossChunkBoundariesOnceAndUsesOriginalValues() => WpfTestHost.RunAsync(async () =>
    {
        using var f = await Fixture.CreateAsync();
        var vm = await ReadyAsync(f.Storage);

        await vm.RunCommand.ExecuteAsync(null);

        Assert.False(vm.IsError, vm.StatusMessage);
        Assert.Equal(2, vm.Results.Count);
        var drift = vm.Results[0];
        Assert.Equal(f.ExpectedCount, drift.Result.Quality.SampleCount);
        Assert.Equal(f.ExpectedCount, drift.Seconds.Length);
        // 块边界的读数只出现一次：时间严格递增。
        Assert.All(drift.Seconds.Zip(drift.Seconds.Skip(1)), p => Assert.True(p.Second > p.First));
        Assert.Equal(3.6, drift.Result.DriftPerHour, 6);
        Assert.Equal(1, drift.Result.Quality.GapCount);
        Assert.Equal(5, vm.Results[1].Result.Mean, 12);
        Assert.Equal("°C", vm.Results[1].Unit);
    });

    [Fact]
    public Task Run_RespectsRangeAndReportsEmptyOrInvalidRanges() => WpfTestHost.RunAsync(async () =>
    {
        using var f = await Fixture.CreateAsync();
        var vm = await ReadyAsync(f.Storage);

        vm.RangeStartText = "100"; vm.RangeEndText = "200";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(101, vm.Results[0].Result.Quality.SampleCount); // 起止都是闭区间

        vm.RangeStartText = "2010"; vm.RangeEndText = "2090";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.IsError);
        Assert.Contains("没有数据", vm.StatusMessage);
        Assert.Empty(vm.Results);

        vm.RangeStartText = "300"; vm.RangeEndText = "200";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.IsError);
        Assert.Contains("晚于", vm.StatusMessage);

        // 有限但超出 DateTime 范围的秒数按输入错误提示，命令不能异常结束。
        vm.RangeStartText = "1e300"; vm.RangeEndText = "";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.IsError);
        Assert.Contains("超出可表示的时间范围", vm.StatusMessage);
        Assert.False(vm.IsBusy);

        // 文本框输入 Infinity 会绑定为正无穷：按输入错误拒绝，不能让窗口切分卡住。
        vm.RangeStartText = vm.RangeEndText = "";
        vm.NoiseWindowSeconds = double.PositiveInfinity;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.IsError);
        Assert.Contains("有限正数", vm.StatusMessage);
        Assert.False(vm.IsBusy);
        vm.NoiseWindowSeconds = 10;

        vm.RangeStartText = "-5"; vm.RangeEndText = "";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.IsError);
        Assert.Contains("秒数", vm.StatusMessage);

        vm.RangeStartText = vm.RangeEndText = "";
        foreach (var c in vm.Channels) c.IsSelected = false;
        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.IsError);
        Assert.Contains("至少选择一个通道", vm.StatusMessage);
    });

    [Fact]
    public Task Run_RefusesRangesBeyondTheMemoryBudgetAndSharesTimeAxis() => WpfTestHost.RunAsync(async () =>
    {
        using var f = await Fixture.CreateAsync();
        var vm = await ReadyAsync(f.Storage);
        // 2 个通道 + 时间戳 = 每个时间点 3 个值；上限 3000 个值即 1000 个时间点。
        vm.MaxAnalysisValues = 3000;

        await vm.RunCommand.ExecuteAsync(null);
        Assert.True(vm.IsError);
        Assert.Contains("超过 1,000 个时间点", vm.StatusMessage);
        Assert.Empty(vm.Results);
        Assert.False(vm.IsBusy);

        vm.RangeStartText = "100"; vm.RangeEndText = "200";
        await vm.RunCommand.ExecuteAsync(null);
        Assert.False(vm.IsError, vm.StatusMessage);
        Assert.Equal(2, vm.Results.Count);
        Assert.Same(vm.Results[0].Seconds, vm.Results[1].Seconds);
    });

    [Fact]
    public Task Csv_EscapesNamesKeepsFullPrecisionAndRecordsSettings() => WpfTestHost.RunAsync(async () =>
    {
        using var f = await Fixture.CreateAsync();
        var vm = await ReadyAsync(f.Storage);
        vm.NoiseWindowSeconds = 7.5;
        await vm.RunCommand.ExecuteAsync(null);

        var lines = vm.BuildCsv().TrimEnd('\r', '\n').Split(Environment.NewLine);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("\"# 会话：长会话；时间段：", lines[0]);
        Assert.Contains("原始值", lines[0]);
        Assert.Contains("噪声窗口 7.5 s", lines[0]);

        var header = SplitCsv(lines[1]);
        var bx = SplitCsv(lines[2]);
        var odd = SplitCsv(lines[3]);
        Assert.Equal(header.Count, bx.Count);
        Assert.Equal(header.Count, odd.Count);
        Assert.Equal(OddName, odd[0]);
        Assert.Equal("°C", odd[1]);
        var meanColumn = header.IndexOf("均值");
        Assert.Equal(vm.Results[0].Result.Mean, double.Parse(bx[meanColumn], CultureInfo.InvariantCulture));
        var driftColumn = header.IndexOf("漂移(单位/h)");
        Assert.Equal(vm.Results[0].Result.DriftPerHour, double.Parse(bx[driftColumn], CultureInfo.InvariantCulture));
    });

    [Fact]
    public Task Cancel_StopsBetweenChunksAndLeavesNoResults() => WpfTestHost.RunAsync(async () =>
    {
        using var f = await Fixture.CreateAsync();
        var gated = new GatedStorage(f.Storage);
        var vm = await ReadyAsync(gated);

        var run = vm.RunCommand.ExecuteAsync(null);
        await gated.FirstChunkRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsBusy);
        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        gated.Release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, gated.ChunkCalls); // 取消后不再读下一块
        Assert.Empty(vm.Results);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsError);
        Assert.Contains("取消", vm.StatusMessage);
        Assert.False(vm.ExportCommand.CanExecute(null));
    });

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    /// <summary>真实存储外包一层：第一次按时间段读取时暂停，直到测试放行。</summary>
    private sealed class GatedStorage(IDataStorageService inner) : IDataStorageService
    {
        public TaskCompletionSource FirstChunkRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ChunkCalls { get; private set; }

        public async Task<IReadOnlyList<MagnetometerReading>> GetReadingsAsync(string sessionId, DateTime? startTime = null, DateTime? endTime = null)
        {
            if (startTime != null && ++ChunkCalls == 1)
            {
                FirstChunkRequested.TrySetResult();
                await Release.Task;
            }
            return await inner.GetReadingsAsync(sessionId, startTime, endTime);
        }

        public StorageWriteStatus WriteStatus => inner.WriteStatus;
        public event Action<StorageWriteStatus>? WriteStatusChanged { add => inner.WriteStatusChanged += value; remove => inner.WriteStatusChanged -= value; }
        public Task<string> StartSessionAsync(string name, SensorConfig sensorConfig, ConnectionConfig connectionConfig) => inner.StartSessionAsync(name, sensorConfig, connectionConfig);
        public Task EndSessionAsync(string sessionId) => inner.EndSessionAsync(sessionId);
        public Task SaveReadingsAsync(IEnumerable<MagnetometerReading> readings) => inner.SaveReadingsAsync(readings);
        public Task WaitForPendingWritesAsync(int timeoutMs = 5000) => inner.WaitForPendingWritesAsync(timeoutMs);
        public Task RetryPendingWritesAsync() => inner.RetryPendingWritesAsync();
        public Task<IReadOnlyList<SessionInfo>> GetSessionsAsync() => inner.GetSessionsAsync();
        public Task DeleteSessionAsync(string sessionId) => inner.DeleteSessionAsync(sessionId);
        public Task UpdateSessionAsync(string sessionId, string name, string? notes) => inner.UpdateSessionAsync(sessionId, name, notes);
        public Task SaveCorrectedReadingsAsync(IEnumerable<CorrectedReading> readings) => inner.SaveCorrectedReadingsAsync(readings);
        public Task<IReadOnlyList<CorrectedReading>> GetCorrectedReadingsAsync(string sessionId, string? correctionProfileId = null) => inner.GetCorrectedReadingsAsync(sessionId, correctionProfileId);
        public Task DeleteCorrectedReadingsAsync(string sessionId, string? correctionProfileId = null) => inner.DeleteCorrectedReadingsAsync(sessionId, correctionProfileId);
        public Task<bool> HasCorrectedReadingsAsync(string sessionId) => inner.HasCorrectedReadingsAsync(sessionId);
        public Task<IReadOnlyList<string>> GetCorrectionVersionIdsAsync(string sessionId) => inner.GetCorrectionVersionIdsAsync(sessionId);
    }
}
