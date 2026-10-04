using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows.Threading;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Configuration;
using AppSettings = MagnetometerSystem.Infrastructure.Configuration.AppSettings;

namespace MagnetometerSystem.App.Tests;

public class DownloadIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PresetDownloadIsNotWrittenButOrdinaryCustomCommandsRemainAvailable(bool magneticOnly) =>
        WpfTestHost.RunAsync(async () =>
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            await using var connection = new TcpDeviceConnection(new ConnectionConfig
            { IpAddress = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port, AutoReconnect = false });
            var bus = new DataBus();
            var vm = new DeviceCommandViewModel(bus, new EmptyConfig());
            try
            {
                var accept = listener.AcceptTcpClientAsync();
                bus.PublishConnectionChanged(connection);
                await connection.ConnectAsync();
                using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(3));
                var preset = magneticOnly ? ProtocolConfig.CreateZdzC08MagneticOnly() : ProtocolConfig.CreateZdzC08();
                vm.SetProtocolCommands(preset.Commands);
                vm.SelectedCommand = preset.Commands.SelectMany(g => g.Commands).Single(c => c.RequiresIsolatedTransfer);
                await vm.SendSelectedCommandCommand.ExecuteAsync(null);
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (!vm.CommunicationLog.Contains("未发送") && DateTime.UtcNow < deadline)
                    await Task.Delay(10);
                Assert.Contains("未发送", vm.CommunicationLog);
                var custom = new DeviceCommand { Name = "读取存储数据", Template = "CUSTOM_STATUS", AppendNewline = false };
                vm.SetProtocolCommands([new CommandGroup { Name = "Custom", Commands = [custom] }]);
                vm.SelectedCommand = custom;
                await vm.SendSelectedCommandCommand.ExecuteAsync(null);
                var received = new byte["CUSTOM_STATUS"u8.Length];
                await peer.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                // TCP preserves byte order, so a forbidden download prefix cannot
                // hide behind the ordinary command that follows it.
                Assert.Equal("CUSTOM_STATUS"u8.ToArray(), received);
            }
            finally
            {
                bus.PublishConnectionChanged(null);
                listener.Stop();
                ((DispatcherTimer)typeof(DeviceCommandViewModel).GetField("_logFlushTimer",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!).Stop();
            }
        });

    private sealed class EmptyConfig : IAppConfigService
    {
        public Task<T?> GetAsync<T>(string key) => Task.FromResult(default(T));
        public Task SetAsync<T>(string key, T value) => Task.CompletedTask;
        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(new AppSettings());
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
    }
}
