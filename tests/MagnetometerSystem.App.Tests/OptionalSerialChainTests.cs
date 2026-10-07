using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Configuration;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using MagnetometerSystem.Infrastructure.Update;

namespace MagnetometerSystem.App.Tests;

public sealed class SerialPairFactAttribute : FactAttribute
{
    public SerialPairFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_RX_PORT"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_TX_PORT")))
            Skip = "需要互连串口对：设置 MAGNETOMETER_TEST_RX_PORT（软件端）与 MAGNETOMETER_TEST_TX_PORT（模拟设备端）；未验证串口链路。";
    }
}

/// <summary>
/// 串口业务链路：模拟设备从 TX 口按“磁梯度数采卡-pt (仅磁场6通道)”发送 101 字节帧，
/// 软件从 RX 口经真实 ConnectionViewModel → 解析 → SQLite 保存 → 图表。
/// </summary>
public class OptionalSerialChainTests
{
    private const int ValidFrames = 400;

    [SerialPairFact]
    public Task ZdzC08MagneticOnly_OverSerialPair_SavesExactlyTheValidFramesAndDrivesChart() => WpfTestHost.RunAsync(async () =>
    {
        var rx = Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_RX_PORT")!;
        var tx = Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_TX_PORT")!;
        Assert.NotEqual(rx, tx);
        var shots = Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_SCREENSHOTS");
        var dbPath = Path.Combine(Path.GetTempPath(), $"serial-chain-{Guid.NewGuid():N}.db");
        var db = new DatabaseInitializer(dbPath); await db.InitializeAsync();
        var bus = new DataBus(); var storage = new SqliteStorageService(db, bus);
        var repo = new SqliteCalibrationRepository(db); var corrector = new OrthogonalityCorrector();
        var config = new AppConfigService(db);
        using var chart = new RealtimeChartViewModel(bus);
        var connection = new ConnectionViewModel(new ConnectionFactory(), bus, corrector, repo)
        {
            SelectedConnectionType = ConnectionType.Serial, SelectedPort = rx, BaudRate = 115200,
        };
        var sessions = new SessionListViewModel(storage, new CsvExporter(storage), bus, corrector, repo);
        using var history = new HistoryPlaybackViewModel(storage, bus, corrector, repo);
        var ortho = new OrthogonalityCalibrationViewModel(new OrthogonalityCalculator(), repo, bus, storage);
        var settings = new SettingsViewModel(config, new UpdateCoordinator(new GiteeUpdateService(new UpdateOptions { CurrentVersion = "1.0.0", PackageKind = AppPackageKind.Portable }), new MagnetometerSystem.Infrastructure.Services.UserPreferencesService(db)));
        var main = new MainViewModel(connection, chart, sessions, history, ortho, new SensorCalibrationViewModel(repo), settings, new DeviceCommandViewModel(bus, config), bus, new AnalysisViewModel(storage));
        main.IsInitialized = true;
        main.WorkspaceLayout.ShowDock(WorkspaceLayoutViewModel.DockRawFrames);
        var window = new MainWindow { DataContext = main, Width = 1440, Height = 920, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        var errors = new StringWriter(); var trace = new TextWriterTraceListener(errors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        using var device = new SerialPort(tx, 115200, Parity.None, 8, StopBits.One) { WriteTimeout = 3000 };
        try
        {
            window.Show(); await WpfTestHost.PumpAsync();

            // A. 固件未启用 CRC：内置预设带有校验段但默认不比对，可以直接连接。
            var builtIn = ProtocolConfig.CreateZdzC08MagneticOnly();
            builtIn.Validate();
            Assert.False(builtIn.Segments.Single(s => s.Type == SegmentType.Checksum).ChecksumEnabled);

            // B. 固件启用校验后，用户勾选“启用校验”（预置参数：CRC-16/MODBUS，从“信息ID”段算到校验段前，低字节在前）。
            var protocol = ProtocolConfig.CreateZdzC08MagneticOnly();
            var checksum = protocol.Segments.Single(s => s.Type == SegmentType.Checksum);
            checksum.ChecksumEnabled = true;
            Assert.Equal((ChecksumAlgorithm.CRC16, Crc16Variant.Modbus, 1, false),
                (checksum.ChecksumAlgorithm, checksum.Crc16Variant, checksum.ChecksumStartIndex, checksum.ChecksumBigEndian));
            Assert.Equal(101, protocol.TotalFrameLength);
            connection.ProtocolConfig = protocol;

            device.Open();
            await connection.ConnectCommand.ExecuteAsync(null);
            Assert.True(connection.IsConnected, connection.LastError);
            Assert.Equal(new[] { "X1", "Y1", "Z1", "X2", "Y2", "Z2" }, chart.ChannelConfigs.Select(c => c.Name));
            Assert.All(chart.ChannelConfigs, c => Assert.Equal("nT", c.Unit));

            var random = new Random(20261007);
            var expected = new List<float[]>();
            var stream = new MemoryStream();
            stream.Write([0x00, 0x11, 0xFF, 0x5A, 0x7E]); // 噪声，含一个假帧头
            for (int i = 0; i < ValidFrames; i++)
            {
                var values = Enumerable.Range(0, 6).Select(c => 48000f + c * 1000f + i * 0.25f + (c % 2 == 0 ? 0.5f : -0.75f)).ToArray();
                if (i == 120)
                {
                    var broken = Frame(values, random); broken[30] ^= 0x5A; // 校验失败
                    stream.Write(broken);
                }
                if (i == 250) stream.Write(Frame(values, random).AsSpan(0, 40)); // 半帧后紧跟完整帧
                stream.Write(Frame(values, random));
                expected.Add(values);
            }
            var bytes = stream.ToArray();
            // 分包、粘包：随机切块写出。
            for (int offset = 0; offset < bytes.Length;)
            {
                var count = Math.Min(bytes.Length - offset, random.Next(1, 260));
                await device.BaseStream.WriteAsync(bytes.AsMemory(offset, count));
                offset += count;
                if (random.Next(4) == 0) await Task.Delay(3);
            }
            await device.BaseStream.FlushAsync();
            for (var deadline = DateTime.UtcNow.AddSeconds(15); connection.ParsedReadingCount < ValidFrames && DateTime.UtcNow < deadline;)
                await Task.Delay(20);
            Assert.Equal(ValidFrames, connection.ParsedReadingCount);
            Assert.Equal(bytes.Length, connection.ReceivedByteCount);
            await Task.Delay(200); // 至少一次绘图刷新
            window.UpdateLayout(); await WpfTestHost.PumpAsync();
            Assert.True(chart.DataPointCount > 0);
            Assert.Equal($"{(double)expected[^1][0]:G8} nT", chart.ChannelConfigs[0].LatestValue);
            Assert.Equal($"{(double)expected[^1][5]:G8} nT", chart.ChannelConfigs[5].LatestValue);
            if (!string.IsNullOrEmpty(shots)) SaveScreenshot((FrameworkElement)window.Content, Path.Combine(shots, "serial-acquire.png"));

            await connection.StopAcquisitionAsync();
            await WpfTestHost.PumpAsync();
            Assert.False(connection.IsConnected);
            var records = connection.ParseRecords.Select(r => r.Record).ToList();
            Assert.Contains(records, r => r.Outcome == ParseOutcome.Skipped);
            Assert.Contains(records, r => r.Outcome == ParseOutcome.Rejected && r.Detail.Contains("CRC-16 校验失败"));
            Assert.Equal(ValidFrames, records.Where(r => r.Outcome == ParseOutcome.Accepted).Sum(r => r.FrameCount));

            // 断开后尾批已提交：数据库条数、通道名、单位和每个原始值都与发送一致。
            var session = Assert.Single(await storage.GetSessionsAsync());
            Assert.Equal(ValidFrames, session.TotalReadings);
            Assert.NotNull(session.EndedAt);
            Assert.Equal(new[] { "X1", "Y1", "Z1", "X2", "Y2", "Z2" }, session.ChannelNames);
            Assert.All(session.ChannelUnits, u => Assert.Equal("nT", u));
            var saved = await storage.GetReadingsAsync(session.Id);
            Assert.Equal(ValidFrames, saved.Count);
            for (int i = 0; i < ValidFrames; i++)
                Assert.Equal(expected[i].Select(v => (double)v), saved[i].ChannelValues);
            Assert.True(saved.Zip(saved.Skip(1)).All(p => p.First.Timestamp <= p.Second.Timestamp));

            // 数据页看到同一会话，条数一致。
            main.CurrentPage = AppPage.Data;
            await sessions.RefreshSessionsCommand.ExecuteAsync(null);
            sessions.SelectedSession = sessions.Sessions.Single();
            window.UpdateLayout(); await WpfTestHost.PumpAsync();
            Assert.Equal(ValidFrames, main.DataPage.Selected!.TotalReadings);
            if (!string.IsNullOrEmpty(shots)) SaveScreenshot((FrameworkElement)window.Content, Path.Combine(shots, "serial-data-page.png"));
            Assert.DoesNotContain("System.Windows.Data Error", errors.ToString());
        }
        finally
        {
            await connection.StopAcquisitionAsync();
            if (device.IsOpen) device.Close();
            window.DataContext = null; window.Close(); ortho.Cleanup();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(trace);
            storage.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(dbPath + suffix);
        }
    });

    /// <summary>独立于软件实现构造 101 字节帧：FF5A AD00 5C00 + 92 字节数据区 + CRC-16/MODBUS(信息ID..数据区末) + 33。</summary>
    private static byte[] Frame(float[] magnetic, Random random)
    {
        var frame = new byte[101];
        frame[0] = 0xFF; frame[1] = 0x5A; frame[2] = 0xAD; frame[3] = 0x00; frame[4] = 0x5C; frame[5] = 0x00;
        for (int i = 0; i < 6; i++) BitConverter.TryWriteBytes(frame.AsSpan(6 + i * 4), magnetic[i]);
        random.NextBytes(frame.AsSpan(30, 68)); // 梯度、GPS、惯导等保留区
        frame[40] = 0xFF; frame[41] = 0x5A;     // 数据区里出现帧头字节
        ushort crc = 0xFFFF;
        foreach (var b in frame.AsSpan(2, 96))
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++) crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1);
        }
        frame[98] = (byte)crc; frame[99] = (byte)(crc >> 8); frame[100] = 0x33;
        return frame;
    }

    private static void SaveScreenshot(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
