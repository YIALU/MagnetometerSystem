using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Processing;

/// <summary>
/// 滚动窗口统计引擎，自行管理各通道的缓冲区，
/// 按 StatisticsConfig.WindowSeconds 裁剪旧数据，调用 StatisticsResultItem.Compute() 完成计算。
/// </summary>
public class RollingStatisticsEngine
{
    private const int DefaultBufferCapacity = 100_000;

    private readonly int _maxChannels;
    private readonly List<(double Timestamp, double Value)>[] _buffers;
    private readonly object _lock = new();
    private double _windowSeconds = 60;

    /// <summary>
    /// 创建统计引擎实例。
    /// </summary>
    /// <param name="maxChannels">支持的最大通道数。</param>
    public RollingStatisticsEngine(int maxChannels = 16)
    {
        if (maxChannels <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChannels), "最大通道数必须大于0。");

        _maxChannels = maxChannels;
        _buffers = new List<(double, double)>[_maxChannels];
        for (int i = 0; i < _maxChannels; i++)
            _buffers[i] = new List<(double, double)>();
    }

    /// <summary>
    /// 更新统计配置（窗口大小等）。可在运行期间多次调用。
    /// </summary>
    public void Configure(StatisticsConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_lock)
        {
            _windowSeconds = config.WindowSeconds > 0 ? config.WindowSeconds : 60;
        }
    }

    /// <summary>
    /// 添加一组采样值。channelValues.Length 必须 &lt;= maxChannels。
    /// </summary>
    /// <param name="timestamp">采样时间戳（秒）。</param>
    /// <param name="channelValues">各通道的当前值。</param>
    public void AddSample(double timestamp, double[] channelValues)
    {
        ArgumentNullException.ThrowIfNull(channelValues);
        if (channelValues.Length > _maxChannels)
            throw new ArgumentException($"通道数 {channelValues.Length} 超过最大通道数 {_maxChannels}。");

        lock (_lock)
        {
            for (int i = 0; i < channelValues.Length; i++)
            {
                var buffer = _buffers[i];
                buffer.Add((timestamp, channelValues[i]));

                // 超过容量限制时丢弃最旧的数据
                if (buffer.Count > DefaultBufferCapacity)
                {
                    int removeCount = buffer.Count - DefaultBufferCapacity;
                    buffer.RemoveRange(0, removeCount);
                }
            }
        }
    }

    /// <summary>
    /// 对所有活跃通道计算窗口化统计量。
    /// </summary>
    /// <param name="channelNames">各通道名称，与 AddSample 的索引对应。</param>
    /// <returns>每个通道的 StatisticsResultItem。</returns>
    public StatisticsResultItem[] ComputeAll(string[] channelNames)
    {
        ArgumentNullException.ThrowIfNull(channelNames);

        lock (_lock)
        {
            int channelCount = Math.Min(channelNames.Length, _maxChannels);
            var results = new List<StatisticsResultItem>();

            for (int i = 0; i < channelCount; i++)
            {
                var buffer = _buffers[i];
                if (buffer.Count == 0)
                    continue;

                // 计算截止时间
                double latestTimestamp = buffer[^1].Timestamp;
                double cutoffTime = latestTimestamp - _windowSeconds;

                // 找到第一个 >= cutoffTime 的索引
                int startIndex = 0;
                for (int j = 0; j < buffer.Count; j++)
                {
                    if (buffer[j].Timestamp >= cutoffTime)
                    {
                        startIndex = j;
                        break;
                    }
                }

                // 提取窗口内的值
                int windowCount = buffer.Count - startIndex;
                var values = new double[windowCount];
                for (int j = 0; j < windowCount; j++)
                    values[j] = buffer[startIndex + j].Value;

                results.Add(StatisticsResultItem.Compute(channelNames[i], values));
            }

            return results.ToArray();
        }
    }

    /// <summary>
    /// 清空所有通道的缓冲区数据。
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            for (int i = 0; i < _maxChannels; i++)
                _buffers[i].Clear();
        }
    }
}
