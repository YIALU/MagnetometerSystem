using System.Collections.Concurrent;
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

public class CtmbsAcquisitionFlowTests
{
    [Fact]
    public Task RealTcp_CommandResponsesAndDamagedLengthCannotPolluteOrBlockSavedMeasurements() =>
        WpfTestHost.RunAsync(async () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"magnetometer_ctmbs_{Guid.NewGuid():N}.db");
            var database = new DatabaseInitializer(path);
            await database.InitializeAsync();
            var bus = new DataBus();
            var storage = new SqliteStorageService(database, bus);
            var corrector = new OrthogonalityCorrector();
            var profiles = new SqliteCalibrationRepository(database);
            var sessions = new SessionListViewModel(storage, new CsvExporter(storage), bus, corrector, profiles);
            var display = new ConcurrentQueue<MagnetometerReading>();
            bus.ProcessedReadingReceived += display.Enqueue;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var vm = new ConnectionViewModel(new ConnectionFactory(), bus, corrector, profiles)
            {
                SelectedConnectionType = ConnectionType.Tcp,
                IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
                ProtocolConfig = ProtocolConfig.CreateCtmbs3X2000(),
            };
            try
            {
                var accept = listener.AcceptTcpClientAsync();
                await vm.ConnectCommand.ExecuteAsync(null);
                Assert.True(vm.IsConnected, vm.LastError);
                using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
                // ste and parameter responses share the telemetry envelope and may arrive
                // between measurements. A false long header must not hold later frames.
                byte[] bytes =
                [
                    .. Frame(" 120000 SC01 X122PWZK0000 07 4 3125 3124 3123 3129 1 2 3 4"),
                    .. Frame(" 20261004120000 1 0 1 1 0 0 0 0 00 25.50"),
                    .. Encoding.ASCII.GetBytes("$99999\n99"),
                    .. Frame(" 07 04 8 1 0 1 0 1 0 1 0"),
                    .. Frame(" 120001 SC01 X122PWZK0000 07 4 3125 3124 3123 3129 5 6 7 8"),
                ];
                await peer.GetStream().WriteAsync(bytes);
                var until = DateTime.UtcNow.AddSeconds(3);
                while (vm.ReceivedByteCount < bytes.Length && DateTime.UtcNow < until) await Task.Delay(10);
                Assert.Equal(bytes.Length, vm.ReceivedByteCount);
                await vm.StopAcquisitionAsync();

                var session = Assert.Single(await storage.GetSessionsAsync());
                var saved = await storage.GetReadingsAsync(session.Id);
                Assert.Equal(2, session.TotalReadings);
                Assert.Equal(2, saved.Count);
                Assert.Equal(new[] { 1d, 2, 3, 4 }, saved[0].ChannelValues);
                Assert.Equal(new[] { 5d, 6, 7, 8 }, saved[1].ChannelValues);
                Assert.Equal(2, vm.ParsedReadingCount);
                Assert.Equal(2, display.Count);
                Assert.Equal(saved[1].ChannelValues, display.Last().ChannelValues);
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

    private static byte[] Frame(string payload)
    {
        int length = payload.Length + 1;
        while (length != payload.Length + length.ToString().Length)
            length = payload.Length + length.ToString().Length;
        return Encoding.ASCII.GetBytes($"${length}\n{length}{payload}\nack\n");
    }
}
