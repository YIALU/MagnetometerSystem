using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Configuration;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

public class ProtocolFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ConnectionVm_RealTcpAndSessionVmSaveFirstLowRateAndFinalReadings(bool invalidCorrection) => WpfTestHost.RunAsync(async () =>
    {
        string path = Path.Combine(Path.GetTempPath(), $"magnetometer_vm_{Guid.NewGuid():N}.db");
        var database = new DatabaseInitializer(path);
        await database.InitializeAsync();
        var bus = new DataBus();
        var storage = new SqliteStorageService(database, bus);
        var corrector = new OrthogonalityCorrector();
        var profiles = new SqliteCalibrationRepository(database);
        var sessions = new SessionListViewModel(storage, new CsvExporter(storage), bus, corrector, profiles);
        using var chart = new RealtimeChartViewModel(bus);
        var displayReadings = new System.Collections.Concurrent.ConcurrentQueue<MagnetometerReading>();
        bus.ProcessedReadingReceived += displayReadings.Enqueue;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var vm = new ConnectionViewModel(new ConnectionFactory(), bus, corrector, profiles)
        {
            SelectedConnectionType = ConnectionType.Tcp, IpAddress = "127.0.0.1",
            Port = ((IPEndPoint)listener.LocalEndpoint).Port, SampleRate = 1000000,
            IsOrthogonalityCorrectionEnabled = invalidCorrection,
            ProtocolConfig = new ProtocolConfig
            {
                FieldMappings = Enumerable.Range(0, 21).Select(i => new FieldMapping
                { Name = $"CH{i}", Unit = i == 20 ? "°C" : "V", ByteOffset = i, ChannelIndex = i }).ToList(),
            },
        };
        static byte[] Frame(int start) => Encoding.ASCII.GetBytes(string.Join(",", Enumerable.Range(start, 21)) + "\n");
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            var connect = vm.ConnectCommand.ExecuteAsync(null);
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            // 设备在建立连接后立即发首帧，不依赖 Connect continuation 或页面加载。
            await peer.GetStream().WriteAsync(Frame(0));
            await connect;
            await peer.GetStream().WriteAsync(Frame(1));
            // 两帧不足批量阈值，之后不再接收也必须由周期提交落库。
            await WaitForAsync(() => storage.WriteStatus.SavedReadings == 2);
            string sessionId = Assert.Single(await storage.GetSessionsAsync()).Id;
            Assert.Equal(2, (await storage.GetReadingsAsync(sessionId)).Count);
            await peer.GetStream().WriteAsync(Frame(2));
            await WaitForAsync(() => vm.ParsedReadingCount == 3);
            await vm.StopAcquisitionAsync();
            var info = Assert.Single(await storage.GetSessionsAsync());
            var saved = await storage.GetReadingsAsync(sessionId);
            Assert.Equal(3, saved.Count);
            Assert.Equal(3, info.TotalReadings);
            Assert.NotNull(info.EndedAt);
            Assert.Equal(SensorType.Generic, info.SensorType);
            Assert.Equal(ConnectionType.Tcp, info.ConnectionType);
            Assert.Equal(1000000, info.SampleRate);
            Assert.Equal(21, info.ChannelCount);
            Assert.Equal("°C", info.ChannelUnits[20]);
            Assert.Equal(Enumerable.Range(0, 21).Select(i => (double)i), saved[0].ChannelValues);
            Assert.Equal(Enumerable.Range(2, 21).Select(i => (double)i), saved[2].ChannelValues);
            Assert.All(saved, reading => Assert.False(reading.IsOrthogonalityCorrected));
            await WpfTestHost.PumpAsync();
            chart.RefreshPlot();
            Assert.Equal(3, chart.DataPointCount);
            Assert.Equal(3, displayReadings.Count);
            Assert.Equal(saved[2].ChannelValues, displayReadings.Last().ChannelValues);
            Assert.All(displayReadings, reading => Assert.False(reading.IsOrthogonalityCorrected));
            if (invalidCorrection) Assert.Contains("改正未应用", vm.LastError);
            Assert.Null(sessions.ActiveSessionId);
            Assert.Equal(0, storage.WriteStatus.PendingReadings);
        }
        finally
        {
            await vm.StopAcquisitionAsync();
            listener.Stop();
            storage.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    });

    [Fact]
    public Task ConnectionVm_RealTcpKeepsRawDataAndWaitsForFinalStorage() => WpfTestHost.RunAsync(async () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var bus = new DataBus();
        var raw = new TaskCompletionSource<MagnetometerReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new TaskCompletionSource<MagnetometerReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStorage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SensorConfig? capturedConfig = null;
        bus.AcquisitionStarting += config => { capturedConfig = config; return Task.CompletedTask; };
        bus.ReadingReceived += reading => raw.TrySetResult(reading);
        bus.ProcessedReadingReceived += reading => processed.TrySetResult(reading);
        bus.AcquisitionStopping += () => { stopping.TrySetResult(); return releaseStorage.Task; };
        var vm = new ConnectionViewModel(new ConnectionFactory(), bus, new OrthogonalityCorrector(), new NoCalibrationRepository())
        {
            SelectedConnectionType = ConnectionType.Tcp,
            IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            SelectedSensorType = SensorType.ProtonMagnetometer, SampleRate = 1000,
            IsOrthogonalityCorrectionEnabled = true,
            ActiveOrthogonalityProfile = new OrthogonalityParams { Unit = "nT", Offset = [1, 1, 1] },
            FirstOrthogonalityChannelsText = "0,1,2",
        };
        vm.ProtocolConfig.FieldMappings.Add(new FieldMapping { Name = "T", Unit = "°C", ChannelIndex = 3, ByteOffset = 3 });
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            await vm.ConnectCommand.ExecuteAsync(null);
            Assert.True(vm.IsConnected, vm.LastError);
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(SensorType.Generic, capturedConfig!.Type);
            Assert.Equal(4, capturedConfig.ChannelCount);
            Assert.Equal("°C", capturedConfig.ChannelUnits[3]);
            // 修改编辑草稿不影响当前会话已冻结的解析规则。
            vm.ProtocolConfig.FieldMappings[0].Scale = 100;
            await peer.GetStream().WriteAsync("0,,0,0\n10,20,"u8.ToArray());
            await peer.GetStream().WriteAsync("30,24\n"u8.ToArray());
            var original = await raw.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var corrected = await processed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(new[] { 10d, 20d, 30d, 24d }, original.ChannelValues);
            Assert.Null(original.OriginalChannelValues);
            Assert.Equal(new[] { 9d, 19d, 29d, 24d }, corrected.ChannelValues);
            Assert.Equal(original.ChannelValues, corrected.OriginalChannelValues);
            await WpfTestHost.PumpAsync();
            Assert.Equal(1, vm.ParseErrorCount);
            var stop = vm.StopAcquisitionAsync();
            await stopping.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(stop.IsCompleted);
            Assert.True(vm.IsAcquiring);
            releaseStorage.TrySetResult();
            await stop.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(vm.IsAcquiring);
            Assert.Null(bus.CurrentConnection);
        }
        finally
        {
            releaseStorage.TrySetResult();
            await vm.StopAcquisitionAsync();
            listener.Stop();
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task DeviceCommandVm_DownloadPresetDoesNotWriteToTcpAndCustomCommandsStillWork(bool magneticOnly) => WpfTestHost.RunAsync(async () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var connection = new TcpDeviceConnection(new ConnectionConfig
        { IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, AutoReconnect = false });
        var bus = new DataBus();
        using var vm = new DeviceCommandViewModel(bus, new EmptyCommandConfig());
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            bus.PublishConnectionChanged(connection);
            await connection.ConnectAsync();
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            var preset = magneticOnly ? ProtocolConfig.CreateZdzC08MagneticOnly() : ProtocolConfig.CreateZdzC08();
            vm.SetProtocolCommands(preset.Commands);
            vm.SelectedCommand = preset.Commands.SelectMany(g => g.Commands).Single(c => c.Name == "读取存储数据");
            await vm.SendSelectedCommandCommand.ExecuteAsync(null);
            Assert.Contains("未发送", vm.WriteStatus);
            Assert.Contains("独立传输", vm.WriteStatus);
            Assert.Equal(0, vm.LastSendByteCount);

            // TCP preserves order: any forbidden 90 9F write would precede this
            // ordinary custom command, so the actual peer's byte assertion fails.
            var custom = new DeviceCommand { Name = "读取存储数据", Template = "CUSTOM_STATUS", AppendNewline = false };
            vm.SetProtocolCommands([new CommandGroup { Name = "Custom", Commands = [custom] }]);
            vm.SelectedCommand = custom;
            var send = vm.SendSelectedCommandCommand.ExecuteAsync(null);
            byte[] received = new byte["CUSTOM_STATUS"u8.Length];
            await peer.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("CUSTOM_STATUS"u8.ToArray(), received);
            await WaitForAsync(() => vm.LastSendByteCount == received.Length);
            Assert.Equal(received.Length, vm.LastSendByteCount);
            Assert.Contains("已写出", vm.WriteStatus);
            await connection.DisconnectAsync();
            await send.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { bus.PublishConnectionChanged(null); listener.Stop(); }
    });

    [Fact]
    public Task DeviceCommandVm_WritesActualBytesAndWaitsForFragmentedAck() => WpfTestHost.RunAsync(async () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var connection = new TcpDeviceConnection(new ConnectionConfig { IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, AutoReconnect = false });
        var bus = new DataBus();
        using var vm = new DeviceCommandViewModel(bus, new EmptyCommandConfig());
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            bus.PublishConnectionChanged(connection);
            await connection.ConnectAsync();
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            var command = ProtocolConfig.CreateCtmbs3X2000().Commands[0].Commands[1];
            vm.SetProtocolCommands([new CommandGroup { Name = "CTMBS", Commands = [command] }]);
            vm.SelectedCommand = command;
            var expected = Ctmbs3X2000FrameBuilder.BuildRequestBytes(command, command.Parameters.ToDictionary(p => p.Key, p => p.DefaultValue));
            var send = vm.SendSelectedCommandCommand.ExecuteAsync(null);
            var transmitted = new byte[expected.Length];
            await peer.GetStream().ReadExactlyAsync(transmitted).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(expected, transmitted);
            await WaitForAsync(() => vm.WriteStatus.Contains("已写出"));
            Assert.Contains("不代表设备执行成功", vm.WriteStatus);
            Assert.DoesNotContain("收到设备 ACK", vm.ResponseStatus);
            await peer.GetStream().WriteAsync("$a"u8.ToArray());
            await peer.GetStream().WriteAsync("ck\n"u8.ToArray());
            await send.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitForAsync(() => vm.ResponseStatus.Contains("收到设备 ACK"));
            // 收发记录：发送行只声称“已写出”；分两段到达的 ACK 在第二段判定为 ACK。
            await WaitForAsync(() => vm.TrafficEntries.Count(e => e.Kind == TrafficKind.Rx) == 2);
            var tx = Assert.Single(vm.TrafficEntries, e => e.Kind == TrafficKind.Tx);
            Assert.Equal($"已写出 {expected.Length} 字节", tx.Result);
            Assert.Equal(command.Name, tx.Command);
            var rx = vm.TrafficEntries.Where(e => e.Kind == TrafficKind.Rx).ToArray();
            Assert.Equal("尚未匹配应答", rx[0].Result);
            Assert.StartsWith("设备返回 ACK", rx[1].Result);
            Assert.Equal("ok", rx[1].Level);
            Assert.True(vm.TrafficEntries.IndexOf(tx) < vm.TrafficEntries.IndexOf(rx[0]));
            await connection.DisconnectAsync();
            await WpfTestHost.PumpAsync();
            Assert.False(vm.IsConnected);
            await vm.SendSelectedCommandCommand.ExecuteAsync(null);
            Assert.Contains("写出失败", vm.WriteStatus);
            await WaitForAsync(() => vm.TrafficEntries.Any(e => e.Kind == TrafficKind.Note && e.Level == "err"));
        }
        finally { bus.PublishConnectionChanged(null); listener.Stop(); }
    });

    [Theory]
    [InlineData(0)] // Unsolicited ERR alone must expire as unknown.
    [InlineData(1)] // Fragmented ERR followed later by a fragmented ACK.
    [InlineData(2)] // ERR and ACK coalesced into the same receive buffer.
    public Task DeviceCommandVm_CtmbsNoDataPushDoesNotRejectPendingStop(int responseMode) => WpfTestHost.RunAsync(async () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var connection = new TcpDeviceConnection(new ConnectionConfig
        { IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, AutoReconnect = false });
        var bus = new DataBus();
        using var vm = new DeviceCommandViewModel(bus, new EmptyCommandConfig()) { ResponseTimeoutMs = 1500 };
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            bus.PublishConnectionChanged(connection);
            await connection.ConnectAsync();
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            var command = ProtocolConfig.CreateCtmbs3X2000().Commands.SelectMany(g => g.Commands).Single(c => c.Template == "stp");
            vm.SetProtocolCommands([new CommandGroup { Name = "CTMBS", Commands = [command] }]);
            vm.SelectedCommand = command;
            var expected = Ctmbs3X2000FrameBuilder.BuildRequestBytes(command, new Dictionary<string, string>());
            var send = vm.SendSelectedCommandCommand.ExecuteAsync(null);
            var transmitted = new byte[expected.Length];
            await peer.GetStream().ReadExactlyAsync(transmitted).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(expected, transmitted);
            if (responseMode == 2)
                await peer.GetStream().WriteAsync("$err\n$ack\n"u8.ToArray());
            else
            {
                await peer.GetStream().WriteAsync("$er"u8.ToArray());
                await peer.GetStream().WriteAsync("r\n"u8.ToArray());
                await WaitForAsync(() => vm.ResponseStatus.Contains("无数据推送"));
                Assert.DoesNotContain("拒绝", vm.ResponseStatus);
                if (responseMode == 1)
                {
                    await peer.GetStream().WriteAsync("$a"u8.ToArray());
                    await peer.GetStream().WriteAsync("ck\n"u8.ToArray());
                }
            }
            await WaitForAsync(() => vm.ResponseStatus.Contains(responseMode == 0 ? "超时" : "收到设备 ACK"));
            await send.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.DoesNotContain("拒绝", vm.ResponseStatus);
            if (responseMode == 0) Assert.Contains("执行结果未知", vm.ResponseStatus);
        }
        finally { bus.PublishConnectionChanged(null); listener.Stop(); }
    });

    [Theory]
    [InlineData("dat", "0", true)]
    [InlineData("dat", "5", false)]
    [InlineData("stp", "", false)]
    public Task DeviceCommandVm_OnlyDatZeroObservesCompleteRealtimeFrames(string mnemonic, string mode, bool observesRealtime) => WpfTestHost.RunAsync(async () =>
    {
        const string payload = " 120000 SC01 X122PWZK0000 07 4 3125 3124 3123 3129 1.23 2.34 3.45 4.56";
        static byte[] Frame(string content)
        {
            int length = content.Length + 1;
            while (length != content.Length + length.ToString().Length)
                length = content.Length + length.ToString().Length;
            return Encoding.ASCII.GetBytes($"${length}\n{length}{content}\nack\n");
        }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var connection = new TcpDeviceConnection(new ConnectionConfig
        { IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, AutoReconnect = false });
        var bus = new DataBus();
        using var vm = new DeviceCommandViewModel(bus, new EmptyCommandConfig()) { ResponseTimeoutMs = 1500 };
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            bus.PublishConnectionChanged(connection);
            await connection.ConnectAsync();
            using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
            var commands = ProtocolConfig.CreateCtmbs3X2000().Commands;
            var command = commands.SelectMany(g => g.Commands).Single(c => c.Template == mnemonic);
            vm.SetProtocolCommands(commands);
            vm.SelectedCommand = command;
            if (mnemonic == "dat") vm.CurrentParameters.Single(p => p.Definition.Key == "mode").Value = mode;
            var values = vm.CurrentParameters.ToDictionary(p => p.Definition.Key, p => p.Value);
            var expected = Ctmbs3X2000FrameBuilder.BuildRequestBytes(command, values);
            var send = vm.SendSelectedCommandCommand.ExecuteAsync(null);
            byte[] transmitted = new byte[expected.Length];
            await peer.GetStream().ReadExactlyAsync(transmitted).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(expected, transmitted);
            long receivedBytes = 0;
            connection.DataReceived += (_, bytes) => Interlocked.Add(ref receivedBytes, bytes.Length);
            // The pending response rule belongs to the sent request, not current UI selection.
            vm.SelectedCommand = commands.SelectMany(g => g.Commands).Single(c => c.Template == "lin");

            await peer.GetStream().WriteAsync("$er"u8.ToArray());
            await peer.GetStream().WriteAsync("r\n"u8.ToArray());
            byte[] status = Frame(" 20261004120000 1 0 1 1 0 0 0 0 00 25.50");
            byte[] invalidMeasurement = Frame(payload.Replace("1.23", "NaN"));
            await peer.GetStream().WriteAsync(status);
            await peer.GetStream().WriteAsync(invalidMeasurement);
            await WaitForAsync(() => vm.ResponseStatus.Contains("无数据推送"));
            Assert.DoesNotContain("已收到实时帧", vm.ResponseStatus);
            Assert.DoesNotContain("收到设备 ACK", vm.ResponseStatus);

            byte[] measurement = Frame(payload);
            await peer.GetStream().WriteAsync(measurement.AsMemory(0, 3));
            await peer.GetStream().WriteAsync(measurement.AsMemory(3, measurement.Length - 4));
            await WaitForAsync(() => Volatile.Read(ref receivedBytes) == 5 + status.Length + invalidMeasurement.Length + measurement.Length - 1);
            await WpfTestHost.PumpAsync();
            Assert.DoesNotContain("已收到实时帧", vm.ResponseStatus); // Final LF is still missing.
            await peer.GetStream().WriteAsync(measurement.AsMemory(measurement.Length - 1));
            if (observesRealtime)
            {
                await WaitForAsync(() => vm.ResponseStatus.Contains("已收到实时帧"));
                Assert.Contains("不能据此归因于本次命令", vm.ResponseStatus);
                Assert.DoesNotContain("执行成功", vm.ResponseStatus);
                await Task.Delay(vm.ResponseTimeoutMs + 100);
                Assert.Contains("已收到实时帧", vm.ResponseStatus); // Completion canceled the old timeout.
            }
            else
            {
                await WaitForAsync(() => vm.ResponseStatus.Contains("超时"));
                Assert.Contains("执行结果未知", vm.ResponseStatus);
                Assert.DoesNotContain("已收到实时帧", vm.ResponseStatus);
            }
            await send.WaitAsync(TimeSpan.FromSeconds(3));
            // 实时帧以 \nack\n 结尾但不是独立的 $ack\n：收发记录不能把它记成本次命令的 ACK。
            await WaitForAsync(() => vm.TrafficEntries.Where(e => e.Kind == TrafficKind.Rx).Sum(e => e.ByteCount) == Volatile.Read(ref receivedBytes));
            Assert.DoesNotContain(vm.TrafficEntries, e => e.Kind == TrafficKind.Rx && e.Result.StartsWith("设备返回 ACK"));
        }
        finally { bus.PublishConnectionChanged(null); listener.Stop(); }
    });

    [Fact]
    public Task DeviceCommandVm_UnrelatedTelemetryDoesNotConfirmCommandExecution() => WpfTestHost.RunAsync(async () =>
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        await using var connection = new TcpDeviceConnection(new ConnectionConfig { IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, AutoReconnect = false });
        var bus = new DataBus();
        using var vm = new DeviceCommandViewModel(bus, new EmptyCommandConfig()) { ResponseTimeoutMs = 150 };
        try
        {
            var accept = listener.AcceptTcpClientAsync();
            bus.PublishConnectionChanged(connection);
            await connection.ConnectAsync();
            using var peer = await accept;
            vm.FreeCommandText = "SET_RATE 1000";
            var send = vm.SendFreeCommandCommand.ExecuteAsync(null);
            var received = new byte[15];
            await peer.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("SET_RATE 1000\r\n", Encoding.ASCII.GetString(received));
            await peer.GetStream().WriteAsync("1,2,3\n"u8.ToArray());
            await WaitForAsync(() => vm.ResponseStatus.Contains("超时"));
            await send.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Contains("执行结果未知", vm.ResponseStatus);
            // 无关遥测不能被记成应答；超时说明区分“收到了数据但未配置预期应答”。
            await WaitForAsync(() => vm.TrafficEntries.Any(e => e.Kind == TrafficKind.Note));
            Assert.Equal("未配置预期应答", vm.TrafficEntries.First(e => e.Kind == TrafficKind.Rx).Result);
            var note = vm.TrafficEntries.First(e => e.Kind == TrafficKind.Note);
            Assert.StartsWith("未配置预期应答", note.Result);
            Assert.Equal("warn", note.Level);
            Assert.Equal("自由发送", note.Command);
            Assert.DoesNotContain(vm.TrafficEntries, e => e.Level == "ok");
            vm.FreeIsHexMode = true;
            Assert.Equal("None", vm.FreeLineEnding);
            vm.FreeCommandText = "01 02";
            vm.FreeLineEnding = "LF";
            var hexSend = vm.SendFreeCommandCommand.ExecuteAsync(null);
            var hexCommand = new byte[3];
            await peer.GetStream().ReadExactlyAsync(hexCommand).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(new byte[] { 1, 2, 10 }, hexCommand);
            await hexSend.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { bus.PublishConnectionChanged(null); listener.Stop(); }
    });

    private static async Task WaitForAsync(Func<bool> ready)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!ready() && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.True(ready(), "等待界面状态更新超时");
    }

    [Fact]
    public Task TrafficLog_LargeFlushesUseOneResetAndKeepTheNewestEntries() => WpfTestHost.RunAsync(() =>
    {
        using var vm = new DeviceCommandViewModel(new DataBus(), new EmptyCommandConfig());
        var enqueue = typeof(DeviceCommandViewModel).GetMethod("EnqueueEntry", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var flush = typeof(DeviceCommandViewModel).GetMethod("FlushLogBuffer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var start = DateTime.Now;
        void Receive(int from, int count)
        {
            for (int i = from; i < from + count; i++)
                enqueue.Invoke(vm, [new TrafficEntry(start.AddMilliseconds(i), TrafficKind.Rx, 1, $"#{i}", "", "", null)]);
        }
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.TrafficEntries.CollectionChanged += (_, e) => actions.Add(e.Action);

        // 少量条目逐条追加。
        Receive(0, 3);
        flush.Invoke(vm, null);
        Assert.Equal(3, actions.Count);
        Assert.All(actions, a => Assert.Equal(System.Collections.Specialized.NotifyCollectionChangedAction.Add, a));

        // 高速设备一次刷新 700 个接收块：只发一个 Reset，保留最新的 500 条。
        actions.Clear();
        Receive(3, 700);
        flush.Invoke(vm, null);
        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Reset], actions);
        Assert.Equal(500, vm.TrafficEntries.Count);
        Assert.Equal("#203", vm.TrafficEntries[0].Content);
        Assert.Equal("#702", vm.TrafficEntries[^1].Content);

        // 已满时少量新条目仍逐条追加并挤掉最旧的。
        actions.Clear();
        Receive(703, 2);
        flush.Invoke(vm, null);
        Assert.Equal(4, actions.Count); // 2 次追加 + 2 次移除
        Assert.Equal(500, vm.TrafficEntries.Count);
        Assert.Equal("#704", vm.TrafficEntries[^1].Content);
        return Task.CompletedTask;
    });

    private sealed class EmptyCommandConfig : IAppConfigService
    {
        public Task<T?> GetAsync<T>(string key) => Task.FromResult(default(T));
        public Task SetAsync<T>(string key, T value) => Task.CompletedTask;
        public Task<MagnetometerSystem.Infrastructure.Configuration.AppSettings> LoadSettingsAsync() => Task.FromResult(new MagnetometerSystem.Infrastructure.Configuration.AppSettings());
        public Task SaveSettingsAsync(MagnetometerSystem.Infrastructure.Configuration.AppSettings settings) => Task.CompletedTask;
    }

    private sealed class NoCalibrationRepository : ICalibrationRepository
    {
        public Task SaveOrthogonalityProfileAsync(OrthogonalityParams profile) => Task.CompletedTask;
        public Task<IReadOnlyList<OrthogonalityParams>> GetOrthogonalityProfilesAsync(string? sensorSerial = null) => Task.FromResult<IReadOnlyList<OrthogonalityParams>>([]);
        public Task<OrthogonalityParams?> GetOrthogonalityProfileAsync(string id) => Task.FromResult<OrthogonalityParams?>(null);
        public Task DeleteOrthogonalityProfileAsync(string id) => Task.CompletedTask;
        public Task SaveCalibrationProfileAsync(CalibrationParams profile) => Task.CompletedTask;
        public Task<IReadOnlyList<CalibrationParams>> GetCalibrationProfilesAsync(SensorType? sensorType = null) => Task.FromResult<IReadOnlyList<CalibrationParams>>([]);
        public Task DeleteCalibrationProfileAsync(string id) => Task.CompletedTask;
        public Task<int> SaveOrthogonalityCalibrationAsync(OrthogonalityCalibrationRecord record) => Task.FromResult(1);
        public Task<List<OrthogonalityCalibrationRecord>> GetOrthogonalityHistoryAsync(string deviceId) => Task.FromResult(new List<OrthogonalityCalibrationRecord>());
    }
}
