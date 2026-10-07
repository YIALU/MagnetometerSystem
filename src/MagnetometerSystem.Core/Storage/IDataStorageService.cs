using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Storage;

/// <summary>
/// 数据存储服务接口
/// </summary>
public interface IDataStorageService
{
    StorageWriteStatus WriteStatus { get; }
    event Action<StorageWriteStatus>? WriteStatusChanged;

    /// <summary>开始新的采集会话</summary>
    Task<string> StartSessionAsync(string name, SensorConfig sensorConfig, ConnectionConfig connectionConfig);

    /// <summary>结束采集会话</summary>
    Task EndSessionAsync(string sessionId);

    /// <summary>批量入队；返回任务在事务成功提交后完成，失败时抛出并保留待写数据。</summary>
    Task SaveReadingsAsync(IEnumerable<MagnetometerReading> readings);

    /// <summary>
    /// 等待后台写入队列把当前已入队的读数全部落库（用于结束会话前确保计数准确）。
    /// 超时或写入失败时抛出，不能将未保存数据视为已完成。
    /// </summary>
    Task WaitForPendingWritesAsync(int timeoutMs = 5000);

    /// <summary>修复存储问题后重试内存中保留的失败批次。</summary>
    Task RetryPendingWritesAsync();

    /// <summary>获取所有会话列表</summary>
    Task<IReadOnlyList<SessionInfo>> GetSessionsAsync();

    /// <summary>获取指定会话的读数</summary>
    Task<IReadOnlyList<MagnetometerReading>> GetReadingsAsync(
        string sessionId, DateTime? startTime = null, DateTime? endTime = null);

    /// <summary>
    /// 有界分页读取 [startTime, endTime] 内的读数：每次至多 <paramref name="limit"/> 条，从 <paramref name="after"/> 之后继续。
    /// 按存储键（时间戳、ID）顺序返回，每条只出现一次；没有更多数据时 <see cref="ReadingPage.Next"/> 为 null。
    /// 用于长会话读取时控制内存；需要严格的时间顺序时由调用方排序。
    /// </summary>
    Task<ReadingPage> GetReadingsPageAsync(
        string sessionId, DateTime startTime, DateTime endTime, ReadingPageCursor? after, int limit);

    /// <summary>删除会话及其数据</summary>
    Task DeleteSessionAsync(string sessionId);

    /// <summary>更新会话名称和备注</summary>
    Task UpdateSessionAsync(string sessionId, string name, string? notes);

    // === 校正数据（独立存储，不覆盖原始数据） ===

    /// <summary>批量保存校正后的读数</summary>
    Task SaveCorrectedReadingsAsync(IEnumerable<CorrectedReading> readings);

    /// <summary>获取指定会话的校正读数，可按校正配置 ID 筛选</summary>
    Task<IReadOnlyList<CorrectedReading>> GetCorrectedReadingsAsync(
        string sessionId, string? correctionProfileId = null);

    /// <summary>列出会话已保存的改正版本，不加载每条改正数值。</summary>
    Task<IReadOnlyList<string>> GetCorrectionVersionIdsAsync(string sessionId);

    /// <summary>删除指定会话的校正读数，可按校正配置 ID 筛选</summary>
    Task DeleteCorrectedReadingsAsync(string sessionId, string? correctionProfileId = null);

    /// <summary>检查指定会话是否存在校正数据</summary>
    Task<bool> HasCorrectedReadingsAsync(string sessionId);
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
    /// <summary>连接时记录的标称采样率，不代表设备实际输出频率；回放使用读数时间戳。</summary>
    public double SampleRate { get; set; }
    public int ChannelCount { get; set; }
    public string[] ChannelNames { get; set; } = [];
    public string[] ChannelUnits { get; set; } = [];
    public string? LegacyDataTable { get; set; }
    public string? DeviceInfo { get; set; }
    public ConnectionType ConnectionType { get; set; }
    public string? Notes { get; set; }
    public long TotalReadings { get; set; }
}

public sealed record StorageWriteStatus(long SavedReadings, long PendingReadings, string? LastError);

/// <summary>分页读取的续读位置：上一页最后一条的存储时间戳与 ID。</summary>
public sealed record ReadingPageCursor(string Timestamp, long Id);

/// <summary>一页读数及续读位置；<see cref="Next"/> 为 null 表示已读完。</summary>
public sealed record ReadingPage(IReadOnlyList<MagnetometerReading> Readings, ReadingPageCursor? Next);
