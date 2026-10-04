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
using MagnetometerSystem.Infrastructure.Update;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.App.Helpers;
using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// 供 Inno Setup 安装包检测运行中实例（installer\MagnetometerSystem.iss 的 AppMutex）。
    /// 改名必须两边同步，否则安装时不会提示关闭程序，会出现文件占用。
    /// 这里只是持有 mutex 让安装器看得见，不阻止多开。
    /// </summary>
    private const string SingleInstanceMutexName = "MagnetometerSystem.SingleInstance";

    private Mutex? _singleInstanceMutex;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            ChartFontHelper.ApplyToAll();
            GlobalErrorHandler.Initialize(this);

            TryCreateSingleInstanceMutex();

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

            services.AddSingleton(new UpdateOptions
            {
                CurrentVersion = AppVersion.Number,
                PackageKind = AppVersion.PackageKind
            });
            services.AddSingleton<IUpdateService, GiteeUpdateService>();
            services.AddSingleton<UpdateCoordinator>();

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

            // 检查更新完全独立于主流程，失败也不影响使用
            _ = RunStartupUpdateCheckAsync(mainVm);
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show($"初始化失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    /// <summary>
    /// 启动后在后台检查更新。发现新版本时弹一次提示窗，同时在状态栏挂上常驻角标，
    /// 用户点"稍后提醒"关掉后仍能随时点角标回来。
    /// </summary>
    private static async Task RunStartupUpdateCheckAsync(MainViewModel mainVm)
    {
        var coordinator = Services.GetRequiredService<UpdateCoordinator>();

        await coordinator.RunStartupCheckAsync(async info =>
        {
            await Current.Dispatcher.InvokeAsync(() => mainVm.AvailableUpdateVersion = info.Version);
            await Current.Dispatcher.Invoke(() => coordinator.ShowUpdateDialogAsync(Current.MainWindow, info));
        });
    }

    /// <summary>
    /// 创建供安装包检测的命名 mutex。失败不影响运行，只是安装时可能提示不到关闭程序。
    /// </summary>
    private void TryCreateSingleInstanceMutex()
    {
        try
        {
            _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName, out _);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"创建单实例 mutex 失败: {ex.Message}");
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

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        GlobalErrorHandler.Shutdown();
    }
}
