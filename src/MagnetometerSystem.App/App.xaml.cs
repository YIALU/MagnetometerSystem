using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;
using MagnetometerSystem.Infrastructure.Configuration;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using MagnetometerSystem.Infrastructure.Services;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            GlobalErrorHandler.Initialize(this);

            var services = new ServiceCollection();

            services.AddSingleton<IConnectionFactory, ConnectionFactory>();
            services.AddSingleton<DataBus>();

            services.AddTransient<ConnectionViewModel>();
            services.AddTransient<RealtimeChartViewModel>();
            services.AddTransient<MainViewModel>();

            services.AddSingleton<DatabaseInitializer>();
            services.AddSingleton<IDataStorageService, SqliteStorageService>();
            services.AddTransient<IDataExporter, CsvExporter>();
            services.AddTransient<SessionListViewModel>();

            services.AddTransient<HistoryPlaybackViewModel>();

            services.AddSingleton<OrthogonalityCorrector>();
            services.AddSingleton<IOrthogonalityService, OrthogonalityCalculator>();
            services.AddSingleton<ICalibrationRepository, SqliteCalibrationRepository>();
            services.AddTransient<OrthogonalityCalibrationViewModel>();

            services.AddSingleton<IAppConfigService, AppConfigService>();
            services.AddSingleton<Infrastructure.Services.IUserPreferencesService, Infrastructure.Services.UserPreferencesService>();

            services.AddTransient<SensorCalibrationViewModel>();
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<DeviceCommandViewModel>();

            Services = services.BuildServiceProvider();

            // 先显示窗口，再异步初始化
            var mainVm = Services.GetRequiredService<MainViewModel>();
            var mainWindow = new MainWindow { DataContext = mainVm };
            mainWindow.Show();

            _ = InitializeAsync(mainVm);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败: {ex.Message}\n\n{ex.StackTrace}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private async Task InitializeAsync(MainViewModel mainVm)
    {
        try
        {
            var dbInit = Services.GetRequiredService<DatabaseInitializer>();
            await dbInit.InitializeAsync();

            AppSettings? loadedSettings = null;
            try
            {
                var configService = Services.GetRequiredService<IAppConfigService>();
                loadedSettings = await configService.LoadSettingsAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"加载配置失败，使用默认值: {ex.Message}");
            }

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (loadedSettings != null)
                {
                    if (loadedSettings.ChartRefreshRate > 0)
                        mainVm.RealtimeChartVM.RefreshRate = loadedSettings.ChartRefreshRate;
                    if (!string.IsNullOrEmpty(loadedSettings.DefaultPortName))
                        mainVm.ConnectionVM.SelectedPort = loadedSettings.DefaultPortName;
                    if (loadedSettings.DefaultBaudRate > 0)
                        mainVm.ConnectionVM.BaudRate = loadedSettings.DefaultBaudRate;
                    if (!string.IsNullOrEmpty(loadedSettings.DefaultIpAddress))
                        mainVm.ConnectionVM.IpAddress = loadedSettings.DefaultIpAddress;
                    if (loadedSettings.DefaultPort > 0)
                        mainVm.ConnectionVM.Port = loadedSettings.DefaultPort;
                }
                mainVm.IsInitialized = true;
            });

            // 默认显示的连接页面数据延迟到窗口渲染完成后再加载
            _ = mainVm.ConnectionVM.EnsureLoadedAsync();
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show($"初始化失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        try
        {
            var configService = Services.GetRequiredService<IAppConfigService>();

            if (MainWindow?.DataContext is MainViewModel mainVm)
            {
                var settings = new AppSettings
                {
                    ChartRefreshRate = mainVm.RealtimeChartVM.RefreshRate,
                    DefaultPortName = mainVm.ConnectionVM.SelectedPort,
                    DefaultBaudRate = mainVm.ConnectionVM.BaudRate,
                    DefaultIpAddress = mainVm.ConnectionVM.IpAddress,
                    DefaultPort = mainVm.ConnectionVM.Port,
                };

                configService.SaveSettingsAsync(settings).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"保存配置失败: {ex.Message}");
        }

        GlobalErrorHandler.Shutdown();
    }
}
