using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

/// <summary>
/// 真实 TCP → ConnectionViewModel → SessionListViewModel → SQLite 的两条用户链路：
/// 用配置库读回的拟合参数做实时改正；以及对端断开、自动重连后数据继续落入同一会话。
/// </summary>
public class LiveChainContinuityTests
{
    private static readonly double[] Distortion = [1.03, 0.04, -0.02, 0.01, 0.97, 0.05, -0.03, 0.02, 1.01];
    private static readonly double[] TrueOffset = [350, -120, 80];

    private static double[] Distorted(int i, int count)
    {
        double z = 1 - 2.0 * (i + .5) / count, r = Math.Sqrt(1 - z * z), phi = i * Math.PI * (3 - Math.Sqrt(5));
        double[] b = [r * Math.Cos(phi) * 50000, r * Math.Sin(phi) * 50000, z * 50000];
        return
        [
            Distortion[0] * b[0] + Distortion[1] * b[1] + Distortion[2] * b[2] + TrueOffset[0],
            Distortion[3] * b[0] + Distortion[4] * b[1] + Distortion[5] * b[2] + TrueOffset[1],
            Distortion[6] * b[0] + Distortion[7] * b[1] + Distortion[8] * b[2] + TrueOffset[2],
        ];
    }

    private static string Csv(params double[] values) =>
        string.Join(",", values.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "\n";

    [Fact]
    public Task LiveCorrection_WithFittedProfileReloadedFromSqlite_DisplaysCorrectedAndSavesRaw() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        // 拟合并保存，再用新的仓储实例读回，相当于在校正页保存后到连接页选用。
        var fitData = new double[300, 3];
        for (int i = 0; i < 300; i++)
        {
            var v = Distorted(i, 300);
            for (int axis = 0; axis < 3; axis++) fitData[i, axis] = v[axis];
        }
        var fit = new OrthogonalityCalculator().Calculate(fitData, referenceFieldStrength: 50000, unit: "nT");
        Assert.True(fit.Success, fit.ErrorMessage);
        await fixture.Profiles.SaveOrthogonalityProfileAsync(fit.Parameters);
        var profile = await new SqliteCalibrationRepository(fixture.Database).GetOrthogonalityProfileAsync(fit.Parameters.Id);
        Assert.NotNull(profile);

        var sessions = new SessionListViewModel(fixture.Storage, new CsvExporter(fixture.Storage), fixture.Bus,
            new OrthogonalityCorrector(), fixture.Profiles);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        // 通道：温度 0、X 1、Y 2、辅助 3、Z 4；改正只作用于 1,2,4。
        var vm = new ConnectionViewModel(new ConnectionFactory(), fixture.Bus, new OrthogonalityCorrector(), fixture.Profiles)
        {
            SelectedConnectionType = ConnectionType.Tcp, IpAddress = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            IsOrthogonalityCorrectionEnabled = true,
            ActiveOrthogonalityProfile = profile,
            FirstOrthogonalityChannelsText = "1,2,4",
            ProtocolConfig = new ProtocolConfig
            {
                FieldMappings = new[] { ("T", "°C"), ("X", "nT"), ("Y", "nT"), ("Aux", "V"), ("Z", "nT") }
                    .Select((c, i) => new FieldMapping { Name = c.Item1, Unit = c.Item2, ChannelIndex = i, ByteOffset = i }).ToList(),
            },
        };
        var displayed = new System.Collections.Concurrent.ConcurrentQueue<MagnetometerReading>();
        fixture.Bus.ProcessedReadingReceived += displayed.Enqueue;
        const int frames = 12;
        var sent = Enumerable.Range(0, frames).Select(i =>
        {
            var m = Distorted(i * 5 % frames, frames);
            return new[] { 21.5 + i, m[0], m[1], 3.3, m[2] };
        }).ToArray();
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.True(vm.IsConnected, vm.LastError);
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes(string.Concat(sent.Select(Csv))));
            await WaitForAsync(() => displayed.Count == frames);
            await vm.StopAcquisitionAsync();
            await WpfTestHost.PumpAsync();
            Assert.DoesNotContain("改正未应用", vm.LastError ?? "");

            var session = Assert.Single(await fixture.Storage.GetSessionsAsync());
            var saved = await fixture.Storage.GetReadingsAsync(session.Id);
            Assert.Equal(frames, saved.Count);
            Assert.Equal(frames, session.TotalReadings);
            var shown = displayed.ToArray();
            var m = profile!.CompensationMatrix;
            var o = profile.Offset;
            var fields = new List<double>();
            for (int i = 0; i < frames; i++)
            {
                Assert.Equal(sent[i], saved[i].ChannelValues);
                Assert.False(saved[i].IsOrthogonalityCorrected);
                Assert.True(shown[i].IsOrthogonalityCorrected);
                Assert.Equal(sent[i], shown[i].OriginalChannelValues);
                double dx = sent[i][1] - o[0], dy = sent[i][2] - o[1], dz = sent[i][4] - o[2];
                Assert.Equal(m[0] * dx + m[1] * dy + m[2] * dz, shown[i].ChannelValues[1], 1e-6);
                Assert.Equal(m[3] * dx + m[4] * dy + m[5] * dz, shown[i].ChannelValues[2], 1e-6);
                Assert.Equal(m[6] * dx + m[7] * dy + m[8] * dz, shown[i].ChannelValues[4], 1e-6);
                Assert.Equal(sent[i][0], shown[i].ChannelValues[0]);
                Assert.Equal(sent[i][3], shown[i].ChannelValues[3]);
                var c = shown[i].ChannelValues;
                fields.Add(Math.Sqrt(c[1] * c[1] + c[2] * c[2] + c[4] * c[4]));
            }
            Assert.True(fields.Max() - fields.Min() < 1, $"实时改正后总场起伏 {fields.Max() - fields.Min()} nT");
            Assert.False(await fixture.Storage.HasCorrectedReadingsAsync(session.Id)); // 实时改正只显示，不写改正表。
            Assert.Null(sessions.ActiveSessionId);
        }
        finally { await vm.StopAcquisitionAsync(); listener.Stop(); }
    });

    [Fact]
    public Task PeerDisconnect_AutoReconnectKeepsSessionAndSavesEveryCompleteFrameOnce_ManualReconnectStartsNewSession() =>
        WpfTestHost.RunAsync(async () =>
    {
        using var fixture = await Fixture.CreateAsync();
        var sessions = new SessionListViewModel(fixture.Storage, new CsvExporter(fixture.Storage), fixture.Bus,
            new OrthogonalityCorrector(), fixture.Profiles);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var vm = new ConnectionViewModel(new ConnectionFactory(), fixture.Bus, new OrthogonalityCorrector(), fixture.Profiles)
        {
            SelectedConnectionType = ConnectionType.Tcp, IpAddress = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            ProtocolConfig = new ProtocolConfig
            {
                FieldMappings = new[] { ("X", "nT"), ("Y", "nT"), ("Z", "nT"), ("T", "°C") }
                    .Select((c, i) => new FieldMapping { Name = c.Item1, Unit = c.Item2, ChannelIndex = i, ByteOffset = i }).ToList(),
            },
        };
        static double[] Values(int i) => [1000 + i, 2000.5 + i, -3000 - i, 25 + i * .1];
        static byte[] Frames(int from, int to) =>
            Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(from, to - from).Select(i => Csv(Values(i)))));
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.True(vm.IsConnected, vm.LastError);
            string firstSession;
            using (var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3)))
            {
                // 断开前留下半帧：重连后必须丢弃，不能和新连接的首帧拼成一条错误读数。
                var tail = Encoding.ASCII.GetBytes("9999,88");
                await peer.GetStream().WriteAsync(Frames(0, 3).Concat(tail).ToArray());
                await WaitForAsync(() => vm.ParsedReadingCount == 3);
                firstSession = Assert.IsType<string>(sessions.ActiveSessionId);
                accept = listener.AcceptTcpClientAsync();
            } // 对端关闭连接，程序进入自动重连。

            using (var peer = await accept.WaitAsync(TimeSpan.FromSeconds(10)))
            {
                await WaitForAsync(() => vm.IsConnected, TimeSpan.FromSeconds(5));
                Assert.Equal(firstSession, sessions.ActiveSessionId); // 自动重连不新建会话。
                await peer.GetStream().WriteAsync(Frames(3, 6));
                await WaitForAsync(() => vm.ParsedReadingCount == 6);
            }
            await vm.StopAcquisitionAsync();
            await WpfTestHost.PumpAsync();
            Assert.Null(sessions.ActiveSessionId);

            var first = Assert.Single(await fixture.Storage.GetSessionsAsync());
            Assert.Equal(firstSession, first.Id);
            var saved = await fixture.Storage.GetReadingsAsync(firstSession);
            Assert.Equal(6, saved.Count);
            Assert.Equal(6, first.TotalReadings);
            Assert.NotNull(first.EndedAt);
            for (int i = 0; i < 6; i++) Assert.Equal(Values(i), saved[i].ChannelValues);
            Assert.DoesNotContain(saved, r => r.ChannelValues.Contains(9999) || r.ChannelValues[0] > 2000);
            Assert.Equal(0, fixture.Storage.WriteStatus.PendingReadings);

            // 手动停止后再次连接：开新会话，旧会话内容不变。
            accept = listener.AcceptTcpClientAsync();
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.True(vm.IsConnected, vm.LastError);
            using (var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3)))
            {
                await peer.GetStream().WriteAsync(Frames(10, 12));
                await WaitForAsync(() => vm.ParsedReadingCount == 2);
            }
            var secondSession = Assert.IsType<string>(sessions.ActiveSessionId);
            Assert.NotEqual(firstSession, secondSession);
            await vm.StopAcquisitionAsync();
            await WpfTestHost.PumpAsync();

            var all = await fixture.Storage.GetSessionsAsync();
            Assert.Equal(2, all.Count);
            Assert.Equal(6, all.Single(s => s.Id == firstSession).TotalReadings);
            Assert.Equal(2, all.Single(s => s.Id == secondSession).TotalReadings);
            Assert.Equal(saved.Select(r => r.ChannelValues), (await fixture.Storage.GetReadingsAsync(firstSession)).Select(r => r.ChannelValues));
            var second = await fixture.Storage.GetReadingsAsync(secondSession);
            Assert.Equal(new[] { Values(10), Values(11) }, second.Select(r => r.ChannelValues));
        }
        finally { await vm.StopAcquisitionAsync(); listener.Stop(); }
    });

    private static async Task WaitForAsync(Func<bool> ready, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (!ready() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(ready(), "等待界面状态更新超时");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"live-chain-{Guid.NewGuid():N}.db");
        public DatabaseInitializer Database { get; private set; } = null!;
        public DataBus Bus { get; } = new();
        public SqliteStorageService Storage { get; private set; } = null!;
        public SqliteCalibrationRepository Profiles { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.Database = new DatabaseInitializer(fixture._path);
            await fixture.Database.InitializeAsync();
            fixture.Storage = new SqliteStorageService(fixture.Database, fixture.Bus);
            fixture.Profiles = new SqliteCalibrationRepository(fixture.Database);
            return fixture;
        }

        public void Dispose()
        {
            Storage.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(_path + suffix)) File.Delete(_path + suffix);
        }
    }
}
