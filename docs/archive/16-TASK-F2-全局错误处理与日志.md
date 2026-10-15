# TASK-F2: 全局错误处理与日志

**文档版本**: v1.0
**更新日期**: 2026-03-21
**优先级**: P0
**阶段**: Phase 5
**流**: Stream E (Agent 5)
**依赖**: 无

---

## 一、基本信息

### 背景

当前系统缺少统一的错误处理和日志机制。未捕获的异常会导致应用崩溃，调试信息通过 `System.Diagnostics.Debug.WriteLine` 输出，仅在调试器附加时可见，无法用于生产环境的问题排查。

### 目标

引入 Serilog 结构化日志框架，建立全局异常捕获机制。通信错误以非阻塞通知栏形式提示用户（而非 MessageBox），非致命异常不导致应用崩溃。所有日志写入按日滚动的文件。

---

## 二、功能需求

1. **全局异常捕获** — 捕获 UI 线程未处理异常（`DispatcherUnhandledException`）和后台任务未观察异常（`UnobservedTaskException`）。
2. **日志文件输出** — 使用 Serilog 写入 `logs/app-{date}.log`，按天滚动，保留 30 天。
3. **非阻塞通知** — 通信错误等可恢复异常在界面顶部/底部显示通知栏，不弹出模态对话框。
4. **断线状态提示** — 通信断开时在 `ConnectionView` 显示断线状态和重连按钮。
5. **容错运行** — 非致命异常标记为已处理，阻止应用崩溃。
6. **替换 Debug.WriteLine** — 全项目范围内将 `System.Diagnostics.Debug.WriteLine` 替换为 Serilog 的 `Log.Warning` / `Log.Error`。

---

## 三、接口契约

### GlobalErrorHandler 静态类

```csharp
namespace MagnetometerSystem.App.Services;

/// <summary>
/// 全局错误处理器，负责初始化异常捕获和日志系统。
/// </summary>
public static class GlobalErrorHandler
{
    /// <summary>
    /// 初始化全局错误处理。在 App.OnStartup 中调用。
    /// 注册 DispatcherUnhandledException 和 UnobservedTaskException 处理器。
    /// </summary>
    public static void Initialize(Application app);

    /// <summary>
    /// UI 线程未处理异常回调。
    /// </summary>
    public static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e);

    /// <summary>
    /// 后台 Task 未观察异常回调。
    /// </summary>
    public static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e);
}
```

### Serilog 初始化（App.xaml.cs）

```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.File(
        path: "logs/app-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
```

---

## 四、文件清单

| 操作 | 文件路径 | 说明 |
|------|---------|------|
| 新建 | `App/Services/GlobalErrorHandler.cs` | 全局错误处理器 |
| 修改 | `App/App.xaml.cs` | 添加 Serilog 初始化和 GlobalErrorHandler.Initialize 调用 |
| 修改 | `App/App.csproj` | 添加 Serilog 和 Serilog.Sinks.File NuGet 引用 |
| 修改 | `App/Views/ConnectionView.xaml` | 添加断线状态栏和重连按钮 |
| 修改 | `App/ViewModels/ConnectionViewModel.cs` | 添加断线通知逻辑 |
| 修改 | 全部含 `Debug.WriteLine` 的文件 | 替换为 Serilog 调用 |

---

## 五、实现指南

### 5.1 NuGet 包安装

在 App 项目的 `.csproj` 中添加：

```xml
<PackageReference Include="Serilog" Version="4.*" />
<PackageReference Include="Serilog.Sinks.File" Version="6.*" />
```

### 5.2 App.xaml.cs 修改

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    // Serilog 初始化
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.File(
            path: "logs/app-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    // 全局错误处理
    GlobalErrorHandler.Initialize(this);

    Log.Information("应用程序启动");

    base.OnStartup(e);
    // ... 现有 DI 注册代码 ...
}

protected override void OnExit(ExitEventArgs e)
{
    Log.Information("应用程序退出");
    Log.CloseAndFlush();
    base.OnExit(e);
}
```

### 5.3 GlobalErrorHandler 实现

```csharp
public static class GlobalErrorHandler
{
    public static void Initialize(Application app)
    {
        app.DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
    }

    public static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI线程未处理异常");

        if (IsNonFatal(e.Exception))
        {
            e.Handled = true;  // 阻止崩溃
            ShowNotification(e.Exception.Message);
        }
        // 致命异常不标记 Handled，让应用正常终止
    }

    public static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "后台Task未观察异常");
        e.SetObserved();  // 阻止进程终止
    }

    private static bool IsNonFatal(Exception ex)
    {
        return ex is not (OutOfMemoryException or StackOverflowException);
    }

    private static void ShowNotification(string message)
    {
        // 通过 Dispatcher 在 UI 线程显示通知栏
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // 触发通知栏显示（具体实现见 5.4）
        });
    }
}
```

### 5.4 非阻塞通知栏

建议在 `MainWindow.xaml` 顶部添加通知栏区域：

```xml
<!-- 通知栏 -->
<Border x:Name="NotificationBar" Background="#FFF3CD" Padding="8,4"
        Visibility="Collapsed" DockPanel.Dock="Top">
    <DockPanel>
        <Button Content="X" DockPanel.Dock="Right" Click="DismissNotification"
                Background="Transparent" BorderThickness="0" />
        <TextBlock x:Name="NotificationText" VerticalAlignment="Center"
                   TextWrapping="Wrap" Foreground="#856404" />
    </DockPanel>
</Border>
```

通知栏可设置自动 5 秒后隐藏，或由用户手动关闭。

### 5.5 通信断线处理

在 `ConnectionViewModel` 中：

```csharp
// 当连接断开时
public bool IsDisconnected { get; set; }
public string DisconnectMessage { get; set; } = "";

// 连接状态变化回调中
private void OnConnectionLost()
{
    IsDisconnected = true;
    DisconnectMessage = "设备连接已断开";
    Log.Warning("设备连接断开: {Reason}", reason);
}
```

在 `ConnectionView.xaml` 中添加：

```xml
<Border Background="#F8D7DA" Padding="8,4"
        Visibility="{Binding IsDisconnected, Converter={StaticResource BoolToVisibility}}">
    <StackPanel Orientation="Horizontal">
        <TextBlock Text="{Binding DisconnectMessage}" Foreground="#721C24"
                   VerticalAlignment="Center" Margin="0,0,8,0" />
        <Button Content="重新连接" Command="{Binding ReconnectCommand}" />
    </StackPanel>
</Border>
```

### 5.6 替换 Debug.WriteLine

在全项目中搜索 `System.Diagnostics.Debug.WriteLine` 和 `Debug.WriteLine`，按以下规则替换：

| 原始调用 | 替换为 |
|---------|--------|
| `Debug.WriteLine("信息...")` | `Log.Debug("信息...")` |
| `Debug.WriteLine($"错误: {ex}")` | `Log.Error(ex, "错误描述")` |
| `Debug.WriteLine($"警告: {msg}")` | `Log.Warning("警告: {Message}", msg)` |

确保添加 `using Serilog;` 引用。App 项目中可直接使用静态 `Log` 类。Core 项目不应直接依赖 Serilog（如有 Core 中的 Debug 输出，可暂留或通过抽象日志接口处理）。

---

## 六、验收标准

1. 应用启动后 `logs/` 目录下生成当天日志文件。
2. 日志文件包含 "应用程序启动" 信息行。
3. 模拟通信异常时，界面显示非阻塞通知栏而非 MessageBox。
4. 通信断开时，`ConnectionView` 显示断线状态和"重新连接"按钮。
5. 后台 Task 抛出异常后，应用不崩溃，异常被记录到日志。
6. UI 线程的非致命异常不导致应用崩溃。
7. 项目中不再有 `Debug.WriteLine` 调用（App 项目范围）。
8. 日志文件按天滚动，最多保留 30 个文件。

---

## 七、单元测试要求

> 注意：全局错误处理以集成验证为主，纯单元测试覆盖有限。

| 验证项 | 方式 |
|--------|------|
| `GlobalErrorHandler.Initialize` 注册事件 | 手动验证或集成测试 |
| `IsNonFatal` 逻辑 | 如提取为可测试方法，验证 OOM 返回 false、IOException 返回 true |
| Serilog 日志输出 | 运行应用后检查 `logs/` 目录 |
| 通知栏显示/隐藏 | UI 手动测试或 UI 自动化测试 |
| 断线后重连按钮可用 | 拔掉串口线或断开 TCP 后验证 |
| Debug.WriteLine 替换完整性 | 全项目搜索确认无残留 |
