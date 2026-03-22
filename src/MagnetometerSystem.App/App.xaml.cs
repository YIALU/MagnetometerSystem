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

        Services = services.BuildServiceProvider();

        // 初始化数据库
        var dbInit = Services.GetRequiredService<DatabaseInitializer>();
        dbInit.InitializeAsync().GetAwaiter().GetResult();

        var mainWindow = new MainWindow
        {
            DataContext = Services.GetRequiredService<MainViewModel>()
        };
        mainWindow.Show();
    }

    private void OnExit(object sender, ExitEventArgs e)
    {
        GlobalErrorHandler.Shutdown();
    }
}
