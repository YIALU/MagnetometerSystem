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
    /// <summary>手动正交度采集状态</summary>
    public ManualOrthoState ManualOrthoState { get; } = new();

    /// <summary>记录按钮点击请求（从导航栏触发到 ViewModel）</summary>
    public event Action? ManualOrthoRecordRequested;

    public void RaiseManualOrthoRecord() => ManualOrthoRecordRequested?.Invoke();

    /// <summary>新的读数到达时触发</summary>
    public event Action<MagnetometerReading>? ReadingReceived;

    /// <summary>
    /// 采集即将开始（连接打开之前触发）。存储等关键消费者在此 await 完成准备工作
    /// （如创建会话、就绪 ActiveSessionId），确保连接打开后第一条数据到达时下游已就绪，不丢数据。
    /// </summary>
    public event Func<SensorConfig, Task>? AcquisitionStarting;

    /// <summary>采集开始（连接打开之后触发，供图表等非关键消费者初始化）</summary>
    public event Action<SensorConfig>? AcquisitionStarted;

    /// <summary>采集停止</summary>
    public event Action? AcquisitionStopped;

    /// <summary>会话开始时触发，参数为 sessionId</summary>
    public event Action<string>? SessionStarted;

    /// <summary>会话结束时触发，参数为 sessionId</summary>
    public event Action<string>? SessionEnded;

    /// <summary>连接变化事件</summary>
    public event Action<IDeviceConnection?>? ConnectionChanged;

    /// <summary>当前活跃连接</summary>
    public IDeviceConnection? CurrentConnection { get; private set; }

    /// <summary>是否处于回放模式（回放时不写入数据库）</summary>
    public bool IsPlaybackMode { get; set; }

    public void PublishReading(MagnetometerReading reading)
    {
        var handlers = ReadingReceived;
        if (handlers == null) return;

        // 逐订阅者隔离：任一订阅者（如实时图表）抛异常，不影响其余订阅者（尤其是存储）被调用。
        foreach (Action<MagnetometerReading> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(reading);
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
    public async Task PublishAcquisitionStartingAsync(SensorConfig config)
    {
        var handlers = AcquisitionStarting;
        if (handlers == null) return;
        foreach (Func<SensorConfig, Task> handler in handlers.GetInvocationList())
            await handler(config);
    }

    public void PublishAcquisitionStarted(SensorConfig config)
    {
        AcquisitionStarted?.Invoke(config);
    }

    public void PublishAcquisitionStopped()
    {
        AcquisitionStopped?.Invoke();
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
