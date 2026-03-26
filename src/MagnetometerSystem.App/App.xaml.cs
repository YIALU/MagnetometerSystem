using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;
using MagnetometerSystem.Infrastructure.Configuration;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
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
            // 全局错误处理与日志初始化
            GlobalErrorHandler.Initialize(this);

        var services = new ServiceCollection();

        // 注册服务
        services.AddSingleton<IConnectionFactory, ConnectionFactory>();
        services.AddSingleton<DataBus>();

        // 注册 ViewModels
        services.AddTransient<ConnectionViewModel>();
        services.AddTransient<RealtimeChartViewModel>();
        services.AddTransient<MainViewModel>();

        // ---- Stream A: 数据存储 (TASK-D1, D2, D4) ----
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<IDataStorageService, SqliteStorageService>();
        services.AddTransient<IDataExporter, CsvExporter>();
        services.AddTransient<SessionListViewModel>();

        // ---- Stream B: 历史回放 (TASK-D3) ----
        services.AddTransient<HistoryPlaybackViewModel>();

        // ---- Stream C: 校正 (TASK-C2, C3, C5) ----
        services.AddSingleton<OrthogonalityCorrector>();
        services.AddSingleton<IOrthogonalityService, OrthogonalityCalculator>();
        services.AddSingleton<ICalibrationRepository, SqliteCalibrationRepository>();
        services.AddTransient<OrthogonalityCalibrationViewModel>();

        // ---- Stream E: 配置持久化 (TASK-F3) ----
        services.AddSingleton<IAppConfigService, AppConfigService>();

        // ---- 新增页面 ----
        services.AddTransient<SensorCalibrationViewModel>();
        services.AddTransient<SettingsViewModel>();

        Services = services.BuildServiceProvider();

        // 初始化数据库
        var dbInit = Services.GetRequiredService<DatabaseInitializer>();
        dbInit.InitializeAsync().GetAwaiter().GetResult();

        // 加载持久化配置并应用到 ViewModel
        AppSettings? loadedSettings = null;
        try
        {
            var configService = Services.GetRequiredService<IAppConfigService>();
            loadedSettings = configService.LoadSettingsAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"加载配置失败，使用默认值: {ex.Message}");
        }

        var mainVm = Services.GetRequiredService<MainViewModel>();

        // 将加载的配置应用到 ViewModel 实例
        if (loadedSettings != null)
        {
            if (loadedSettings.ChartRefreshRate > 0)
            {
                mainVm.RealtimeChartVM.RefreshRate = loadedSettings.ChartRefreshRate;
            }

            // 恢复连接配置
            if (!string.IsNullOrEmpty(loadedSettings.DefaultPortName))
            {
                mainVm.ConnectionVM.SelectedPort = loadedSettings.DefaultPortName;
            }
            if (loadedSettings.DefaultBaudRate > 0)
            {
                mainVm.ConnectionVM.BaudRate = loadedSettings.DefaultBaudRate;
            }
            if (!string.IsNullOrEmpty(loadedSettings.DefaultIpAddress))
            {
                mainVm.ConnectionVM.IpAddress = loadedSettings.DefaultIpAddress;
            }
            if (loadedSettings.DefaultPort > 0)
            {
                mainVm.ConnectionVM.Port = loadedSettings.DefaultPort;
            }
        }

        var mainWindow = new MainWindow
        {
            DataContext = mainVm
        };
        mainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败: {ex.Message}\n\n{ex.StackTrace}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        // 保存当前配置（尽力而为）
        try
        {
            var configService = Services.GetRequiredService<IAppConfigService>();

            // 从 MainWindow 获取当前 ViewModel 实例
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
