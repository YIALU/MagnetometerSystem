using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Storage;

/// <summary>
/// 数据存储服务接口
/// </summary>
public interface IDataStorageService
{
    /// <summary>开始新的采集会话</summary>
    Task<string> StartSessionAsync(string name, SensorConfig sensorConfig, ConnectionConfig connectionConfig);

    /// <summary>结束采集会话</summary>
    Task EndSessionAsync(string sessionId);

    /// <summary>批量保存读数</summary>
    Task SaveReadingsAsync(IEnumerable<MagnetometerReading> readings);

    /// <summary>获取所有会话列表</summary>
    Task<IReadOnlyList<SessionInfo>> GetSessionsAsync();

    /// <summary>获取指定会话的读数</summary>
    Task<IReadOnlyList<MagnetometerReading>> GetReadingsAsync(
        string sessionId, DateTime? startTime = null, DateTime? endTime = null);

    /// <summary>删除会话及其数据</summary>
    Task DeleteSessionAsync(string sessionId);
}

/// <summary>
/// 采集会话信息
/// </summary>
public class SessionInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public SensorType SensorType { get; set; }
    public double SampleRate { get; set; }
    public int ChannelCount { get; set; }
    public string[] ChannelNames { get; set; } = [];
    public string? DeviceInfo { get; set; }
    public ConnectionType ConnectionType { get; set; }
    public string? Notes { get; set; }
    public long TotalReadings { get; set; }
}
