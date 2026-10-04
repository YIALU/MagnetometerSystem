using System.IO;
using System.Windows;
using System.Windows.Threading;
using Serilog;

namespace MagnetometerSystem.App.Services;

/// <summary>
/// 全局错误处理器，负责初始化异常捕获和日志系统。
/// </summary>
public static class GlobalErrorHandler
{
    /// <summary>
    /// 初始化全局错误处理。在 App.OnStartup 中调用。
    /// 配置 Serilog 日志，注册全局异常处理器。
    /// </summary>
    public static void Initialize(Application app)
    {
        // 初始化 Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(ResolveLogDirectory(), "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // 注册全局异常处理器
        app.DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        Log.Information("应用程序启动");
    }

    /// <summary>
    /// 决定日志写在哪。
    /// 优先程序目录下的 logs\（便携版就地留日志，方便用户打包发回来排查）；
    /// 目录不可写时回退到 %LOCALAPPDATA%\MagnetometerSystem\logs——
    /// 万一程序被放进 Program Files，写同目录会被 UAC 虚拟化或直接失败。
    /// </summary>
    private static string ResolveLogDirectory()
    {
        var appDir = Path.Combine(AppContext.BaseDirectory, "logs");
        try
        {
            Directory.CreateDirectory(appDir);

            var probe = Path.Combine(appDir, $".write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return appDir;
        }
        catch
        {
            var fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MagnetometerSystem", "logs");
            Directory.CreateDirectory(fallback);
            return fallback;
        }
    }

    /// <summary>
    /// UI 线程未处理异常回调。
    /// </summary>
    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "未处理的UI线程异常");

        // 非致命异常标记为已处理，阻止崩溃
        if (IsNonFatal(e.Exception))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// 后台 Task 未观察异常回调。
    /// </summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "未观察的Task异常");
        e.SetObserved();
    }

    /// <summary>
    /// 应用程序域未处理异常回调。
    /// </summary>
    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Log.Fatal(ex, "应用程序域未处理异常");
    }

    /// <summary>
    /// 判断异常是否为非致命异常。
    /// </summary>
    private static bool IsNonFatal(Exception ex)
    {
        return ex is not (OutOfMemoryException or StackOverflowException);
    }

    /// <summary>
    /// 关闭日志系统。在应用退出时调用。
    /// </summary>
    public static void Shutdown()
    {
        Log.Information("应用程序关闭");
        Log.CloseAndFlush();
    }
}
