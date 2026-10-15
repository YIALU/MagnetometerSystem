using System.Windows;
using System.Net.Http;
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
using MagnetometerSystem.App.Views.Dialogs;

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
    private static readonly CancellationTokenSource UpdateLoopCts = new();

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
            services.AddTransient<AnalysisViewModel>();

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
            services.AddSingleton<IUpdateService, MultiPlatformUpdateService>();
            services.AddSingleton<UpdateCoordinator>();
            services.AddSingleton<Core.Feedback.IFeedbackClient>(_ => new Infrastructure.Feedback.FeedbackClient(
                new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, FeedbackCoordinator.Endpoint));
            services.AddSingleton(new Infrastructure.Feedback.FeedbackDraftStore(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MagnetometerSystem", "feedback", "draft.json")));
            services.AddSingleton<Core.Feedback.IFeedbackLogSource>(_ => new Infrastructure.Feedback.FeedbackLogCollector(() => GlobalErrorHandler.LogDirectory));
            services.AddSingleton<FeedbackCoordinator>();

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
            Serilog.Log.Error(ex, "应用启动失败");
            new StartupErrorDialog(ex).ShowDialog();
            Shutdown();
        }
    }

    private async Task InitializeAsync(MainViewModel mainVm)
    {
        try
        {
            var dbInit = Services.GetRequiredService<DatabaseInitializer>();
            await dbInit.InitializeAsync();
            Serilog.Log.Information("数据库就绪: {DatabasePath}", dbInit.DatabasePath);

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
                    mainVm.WorkspaceLayout.Restore(loadedSettings.WorkbenchPanels);
                    if (loadedSettings.ChartRefreshRate > 0)
                        mainVm.RealtimeChartVM.RefreshRate = mainVm.SettingsVM.ChartRefreshRate = loadedSettings.ChartRefreshRate;
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
            Serilog.Log.Error(ex, "应用初始化失败");
            await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show("软件未能完成准备，请重新启动。若仍无法使用，请通过反馈与建议联系维护者并提供日志。",
                    "暂时无法使用", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    /// <summary>
    /// 启动后在后台检查更新，之后在运行期间每 24 小时再查一次。发现新版本时弹一次提示窗，
    /// 同时在状态栏挂上常驻角标，用户点"稍后提醒"关掉后仍能随时点角标回来；
    /// 当天重启不再联网，但角标会按上次发现的版本恢复。
    /// </summary>
    private static async Task RunStartupUpdateCheckAsync(MainViewModel mainVm)
    {
        var coordinator = Services.GetRequiredService<UpdateCoordinator>();
        coordinator.KnownUpdateCleared += () =>
            Current?.Dispatcher.InvokeAsync(() => mainVm.AvailableUpdateVersion = null);

        await coordinator.RunAutoCheckLoopAsync(
            async info => await Current.Dispatcher.Invoke(() =>
            {
                // 回调排到 UI 线程时，这个版本可能已被更新的检查或切换平台作废。
                if (!coordinator.IsCurrentUpdate(info))
                {
                    coordinator.ReleasePromptReservation(info);
                    return Task.CompletedTask;
                }
                mainVm.AvailableUpdateVersion = info.Version;
                return coordinator.ShowUpdateDialogAsync(Current.MainWindow, info);
            }),
            async info => await Current.Dispatcher.InvokeAsync(() =>
            {
                if (coordinator.IsCurrentUpdate(info)) mainVm.AvailableUpdateVersion = info.Version;
            }),
            UpdateLoopCts.Token);
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

    internal static async Task SaveCurrentSettingsAsync(MainViewModel mainVm, IAppConfigService? configService = null)
    {
        configService ??= Services.GetRequiredService<IAppConfigService>();
        var settings = await configService.LoadSettingsAsync();
        settings.ChartRefreshRate = mainVm.RealtimeChartVM.RefreshRate;
        settings.DefaultPortName = mainVm.ConnectionVM.SelectedPort;
        settings.DefaultBaudRate = mainVm.ConnectionVM.BaudRate;
        settings.DefaultIpAddress = mainVm.ConnectionVM.IpAddress;
        settings.DefaultPort = mainVm.ConnectionVM.Port;
        settings.WorkbenchPanels = mainVm.WorkspaceLayout.GetPersistedPanels();
        await configService.SaveSettingsAsync(settings);
    }

    /// <summary>所有正常退出入口都先等待物理连接停止、会话提交和设置保存。</summary>
    public static async Task PrepareForExitAsync(MainViewModel mainVm, IAppConfigService? configService = null)
    {
        await mainVm.ConnectionVM.StopAcquisitionAsync();
        // Stop 已等待真实存储任务；异步刷新的错误文字/计数不能作为提交凭据。
        if (mainVm.SessionListVM.ActiveSessionId != null)
            throw new InvalidOperationException("当前会话尚未完成保存，请重试写入后再退出。");
        await SaveCurrentSettingsAsync(mainVm, configService);
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        UpdateLoopCts.Cancel();

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        GlobalErrorHandler.Shutdown();
    }
}
