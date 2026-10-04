using System.ComponentModel;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Services;

/// <summary>
/// 手动正交度采集状态，供 MainWindow 导航栏卡片绑定
/// </summary>
public sealed class ManualOrthoState : INotifyPropertyChanged
{
    public bool IsActive { get; private set; }
    public int PointsRecorded { get; private set; }
    public string? RawFilePath { get; private set; }
    public string StatusMessage { get; private set; } = "";
    public bool HasEnoughBuffer { get; private set; }  // 缓冲队列里 ≥10 条

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(bool active, int points, string? rawPath, string status, bool enoughBuf)
    {
        IsActive = active;
        PointsRecorded = points;
        RawFilePath = rawPath;
        StatusMessage = status;
        HasEnoughBuffer = enoughBuf;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>
/// 实时数据总线：发布-订阅模式，解耦数据源与消费者
/// </summary>
public class DataBus
{
    private readonly object _acquisitionAcceptanceGate = new();
    private Func<MagnetometerReading, bool>? _acquisitionReadingAcceptor;
    // Standalone parsers/components may publish without a storage/session lifecycle.
    private bool _acceptAcquisitionReadings = true;
    private long _acquisitionAcceptanceGeneration;
    private Exception? _acquisitionAcceptanceFault;

    /// <summary>注册唯一的关键保存消费者。回调只能同步接纳到受保护的内存缓冲，不得写库或等待 UI。</summary>
    public void RegisterAcquisitionReadingAcceptor(Func<MagnetometerReading, bool> acceptor)
    {
        ArgumentNullException.ThrowIfNull(acceptor);
        lock (_acquisitionAcceptanceGate)
        {
            if (_acquisitionReadingAcceptor != null)
                throw new InvalidOperationException("采集只能注册一个关键保存接纳者。");
            _acquisitionReadingAcceptor = acceptor;
        }
    }

    private void CloseAcquisitionAcceptance()
    {
        lock (_acquisitionAcceptanceGate)
        {
            _acceptAcquisitionReadings = false;
            _acquisitionAcceptanceGeneration++;
        }
    }

    /// <summary>手动正交度采集状态</summary>
    public ManualOrthoState ManualOrthoState { get; } = new();

    /// <summary>记录按钮点击请求（从导航栏触发到 ViewModel）</summary>
    public event Action? ManualOrthoRecordRequested;

    public void RaiseManualOrthoRecord() => ManualOrthoRecordRequested?.Invoke();

    /// <summary>新的读数到达时触发</summary>
    public event Action<MagnetometerReading>? ReadingReceived;

    /// <summary>仅供显示的处理结果；原始存储消费者不订阅此事件。</summary>
    public event Action<MagnetometerReading>? ProcessedReadingReceived;

    /// <summary>
    /// 采集即将开始（连接打开之前触发）。存储等关键消费者在此 await 完成准备工作
    /// （如创建会话、就绪 ActiveSessionId），确保连接打开后第一条数据到达时下游已就绪，不丢数据。
    /// </summary>
    public event Func<SensorConfig, Task>? AcquisitionStarting;

    /// <summary>采集开始（连接打开之后触发，供图表等非关键消费者初始化）</summary>
    public event Action<SensorConfig>? AcquisitionStarted;

    /// <summary>采集停止</summary>
    public event Action? AcquisitionStopped;

    /// <summary>采集关键消费者失败；同步通知生产端停止接收，再异步清理连接及尾批。</summary>
    public event Action<Exception>? AcquisitionFaulted;

    public void PublishAcquisitionFault(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_acquisitionAcceptanceGate)
        {
            _acceptAcquisitionReadings = false;
            _acquisitionAcceptanceFault ??= error;
        }
        // Fault handlers may stop transports or schedule UI work. Never invoke them under the acceptance gate.
        if (AcquisitionFaulted is not { } handlers) return;
        foreach (Action<Exception> handler in handlers.GetInvocationList())
        {
            try { handler(error); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError($"采集故障订阅者异常: {ex}"); }
        }
    }

    /// <summary>数据源停止后，等待存储消费者将尾批落库并结束会话。</summary>
    public event Func<Task>? AcquisitionStopping;

    /// <summary>显式重试已保存并结束指定会话；在会话生命周期锁外等待连接完成停止。</summary>
    public event Func<string, Task>? AcquisitionRecoveryCompleted;

    public ConnectionConfig? AcquisitionConnectionConfig { get; private set; }

    /// <summary>会话开始时触发，参数为 sessionId</summary>
    public event Action<string>? SessionStarted;

    /// <summary>会话结束时触发，参数为 sessionId</summary>
    public event Action<string>? SessionEnded;

    /// <summary>连接变化事件</summary>
    public event Action<IDeviceConnection?>? ConnectionChanged;

    /// <summary>当前活跃连接</summary>
    public IDeviceConnection? CurrentConnection { get; private set; }

    private IReadOnlyList<string> _acquisitionChannelUnits = Array.AsReadOnly(Array.Empty<string>());
    /// <summary>连接准备时冻结的采集通道单位；只读，不包含回放配置。</summary>
    public IReadOnlyList<string> AcquisitionChannelUnits => _acquisitionChannelUnits;

    /// <summary>是否处于回放模式（回放时不写入数据库）</summary>
    public bool IsPlaybackMode { get; set; }

    public void PublishReading(MagnetometerReading reading) => TryPublishAcquisitionReading(reading);

    /// <summary>只有关键保存消费者已接纳的读数才通知观察者。false 不得计入采集或绘图。</summary>
    public bool TryPublishAcquisitionReading(MagnetometerReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        Exception? acceptanceError = null;
        lock (_acquisitionAcceptanceGate)
        {
            if (!_acceptAcquisitionReadings) return false;
            try
            {
                if (_acquisitionReadingAcceptor != null && !_acquisitionReadingAcceptor(reading.DeepClone()))
                    return false;
            }
            catch (Exception ex)
            {
                _acceptAcquisitionReadings = false;
                _acquisitionAcceptanceFault ??= ex;
                acceptanceError = ex;
            }
        }
        if (acceptanceError != null)
        {
            PublishAcquisitionFault(acceptanceError);
            return false;
        }
        // The snapshot is now owned by storage. A fault raised by an observer cannot undo this acceptance.
        PublishToSubscribers(ReadingReceived, reading);
        return true;
    }

    public void PublishProcessedReading(MagnetometerReading reading)
    {
        PublishToSubscribers(ProcessedReadingReceived, reading);
    }

    private static void PublishToSubscribers(Action<MagnetometerReading>? handlers, MagnetometerReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (handlers == null) return;

        // 逐订阅者隔离：任一订阅者（如实时图表）抛异常，不影响其余订阅者（尤其是存储）被调用。
        foreach (Action<MagnetometerReading> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(reading.DeepClone());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"[PublishReading] 订阅者异常已隔离: {ex}");
            }
        }
    }

    /// <summary>
    /// 触发"采集即将开始"，按订阅顺序逐个 await。调用方应在连接打开前 await 本方法，
    /// 使会话等准备工作先于数据到达完成。
    /// </summary>
    public async Task PublishAcquisitionStartingAsync(SensorConfig config, ConnectionConfig? connectionConfig = null)
    {
        long generation;
        lock (_acquisitionAcceptanceGate)
        {
            generation = ++_acquisitionAcceptanceGeneration;
            _acceptAcquisitionReadings = false;
            _acquisitionAcceptanceFault = null;
        }
        _acquisitionChannelUnits = Array.AsReadOnly(config.ChannelUnits.ToArray());
        AcquisitionConnectionConfig = connectionConfig;
        var handlers = AcquisitionStarting;
        if (handlers != null)
            foreach (Func<SensorConfig, Task> handler in handlers.GetInvocationList())
                await handler(config);
        lock (_acquisitionAcceptanceGate)
        {
            if (generation != _acquisitionAcceptanceGeneration || _acquisitionAcceptanceFault != null)
                throw new InvalidOperationException("采集准备期间已停止或发生保存故障，不能开始接收。", _acquisitionAcceptanceFault);
            _acceptAcquisitionReadings = true;
        }
    }

    public void PublishAcquisitionStarted(SensorConfig config)
    {
        AcquisitionStarted?.Invoke(config);
    }

    /// <summary>回放只结束显示，不改变正在准备的实时采集接纳门或通道单位。</summary>
    public void PublishPlaybackStopped() => AcquisitionStopped?.Invoke();

    public void PublishAcquisitionStopped()
    {
        CloseAcquisitionAcceptance();
        _acquisitionChannelUnits = Array.AsReadOnly(Array.Empty<string>());
        AcquisitionStopped?.Invoke();
    }

    public async Task PublishAcquisitionStoppingAsync()
    {
        CloseAcquisitionAcceptance();
        var handlers = AcquisitionStopping;
        if (handlers == null) return;
        foreach (Func<Task> handler in handlers.GetInvocationList())
            await handler();
    }

    public async Task PublishAcquisitionRecoveryCompletedAsync(string sessionId)
    {
        var handlers = AcquisitionRecoveryCompleted;
        if (handlers == null) return;
        foreach (Func<string, Task> handler in handlers.GetInvocationList())
            await handler(sessionId);
    }

    public void PublishSessionStarted(string sessionId)
    {
        SessionStarted?.Invoke(sessionId);
    }

    public void PublishSessionEnded(string sessionId)
    {
        SessionEnded?.Invoke(sessionId);
    }

    public void PublishConnectionChanged(IDeviceConnection? connection)
    {
        CurrentConnection = connection;
        ConnectionChanged?.Invoke(connection);
    }
}
