using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using MagnetometerSystem.Infrastructure.Diagnostics;
using Serilog;

namespace MagnetometerSystem.App.Services;

/// <summary>
/// 全局错误处理器，负责初始化异常捕获和日志系统。
/// </summary>
public static class GlobalErrorHandler
{
    /// <summary>单个日志文件上限；超过后当天滚动到新文件，总数仍受保留个数限制。</summary>
    private const long LogFileSizeLimitBytes = 10 * 1024 * 1024;

    private static SerilogTraceListener? _traceListener;

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
                Path.Combine(LogDirectory = ResolveLogDirectory(), "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                fileSizeLimitBytes: LogFileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Core / Infrastructure 通过 Trace 报告连接、解析和保存错误，转写进同一个日志文件。
        _traceListener = new SerilogTraceListener(Log.Logger);
        Trace.Listeners.Add(_traceListener);

        // 注册全局异常处理器
        app.DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        Log.Information("应用程序启动 {Version}（{Package}），{OS}，.NET {Runtime} {Arch}，日志目录 {LogDirectory}",
            AppVersion.DiagnosticVersion, AppVersion.PackageKindDisplay, RuntimeInformation.OSDescription,
            Environment.Version, RuntimeInformation.ProcessArchitecture, LogDirectory);
    }

    /// <summary>实际使用的日志目录；初始化前为 null。</summary>
    public static string? LogDirectory { get; private set; }

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
        if (_traceListener is { } listener)
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose(); // 补写仍在计数中的省略条数
            _traceListener = null;
        }
        Log.CloseAndFlush();
    }
}
