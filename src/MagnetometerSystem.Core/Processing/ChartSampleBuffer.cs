using MagnetometerSystem.Core.Helpers;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Processing;

/// <summary>实时图表的通道布局：通道数，以及按通道索引排列的名称和单位。</summary>
public sealed record ChartChannelLayout(int Count, string[] Names, string[] Units)
{
    public static ChartChannelLayout Empty { get; } = new(0, [], []);
}

/// <summary>
/// 从 <see cref="ChartSampleBuffer"/> 复制出的一段数据：时间（相对第一条读数的秒数），按通道索引排列的显示值和原始值，
/// 以及缓冲中的总点数。显示值（<see cref="MagnetometerReading.ChannelValues"/>，可能已校正）只用来画曲线；
/// 原始值（校正前）用于统计、十字准线和读数。
/// </summary>
public sealed record ChartSampleSnapshot(double[] Times, double[][] Display, double[][] Raw, int TotalCount);

/// <summary>
/// 实时图表的数据缓冲：一条时间轴，每个通道一组显示值和一组原始值，以及当前的通道布局。
/// 所有缓冲容量相同、始终与时间轴等长，满了以后一起覆盖最旧的点。
/// 接收线程追加读数，界面线程复制数据；所有成员都可以跨线程调用。
/// </summary>
public sealed class ChartSampleBuffer
{
    /// <summary>默认每个通道保留的点数。</summary>
    public const int DefaultCapacity = 100000;

    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly CircularBuffer<double> _times;
    private CircularBuffer<double>[] _display = [];
    private CircularBuffer<double>[] _raw = [];
    private DateTime _startTime;
    private ChartChannelLayout _layout = ChartChannelLayout.Empty;

    public ChartSampleBuffer(int capacity = DefaultCapacity)
    {
        _capacity = capacity;
        _times = new CircularBuffer<double>(capacity);
    }

    /// <summary>当前的通道布局；读数的通道多于布局时会扩展。</summary>
    public ChartChannelLayout Layout
    {
        get { lock (_lock) return _layout; }
    }

    /// <summary>缓冲中的点数。</summary>
    public int Count
    {
        get { lock (_lock) return _times.Count; }
    }

    /// <summary>开始新的采集：按协议设定通道布局，清空数据。已分配的通道缓冲保留复用。</summary>
    public void Reset(int channelCount, string[] names, string[] units)
    {
        lock (_lock)
        {
            _layout = new ChartChannelLayout(channelCount, names, units);
            EnsureChannels(channelCount);
            ClearData();
        }
    }

    /// <summary>
    /// 追加一条读数。显示值取 <see cref="MagnetometerReading.ChannelValues"/>，原始值取
    /// <see cref="MagnetometerReading.OriginalChannelValues"/>（未校正时与显示值相同），读数缺的通道补 NaN。
    /// 读数的通道多于当前布局（读数先于采集开始事件到达，或实际通道数多于协议）时，新通道先补 NaN 到与时间轴等长，
    /// 布局扩展到读数的通道数（名称补 CHn，单位留空），并返回 true。
    /// </summary>
    public bool Append(MagnetometerReading reading)
    {
        lock (_lock)
        {
            if (_times.Count == 0) _startTime = reading.Timestamp;
            var elapsed = (reading.Timestamp - _startTime).TotalSeconds;
            int needed = reading.ChannelValues.Length;
            if (needed > _display.Length)
            {
                int previous = _display.Length;
                EnsureChannels(needed);
                for (int i = previous; i < needed; i++)
                    for (int pad = 0; pad < _times.Count; pad++)
                    {
                        _display[i].Add(double.NaN);
                        _raw[i].Add(double.NaN);
                    }
            }

            bool layoutGrew = needed > _layout.Count;
            if (layoutGrew)
            {
                var old = _layout;
                _layout = new ChartChannelLayout(needed,
                    Enumerable.Range(0, needed).Select(i => old.Names.ElementAtOrDefault(i) ?? $"CH{i}").ToArray(),
                    Enumerable.Range(0, needed).Select(i => old.Units.ElementAtOrDefault(i) ?? "").ToArray());
            }

            _times.Add(elapsed);
            var rawValues = reading.OriginalChannelValues ?? reading.ChannelValues;
            for (int i = 0; i < _display.Length; i++)
            {
                _display[i].Add(i < reading.ChannelValues.Length ? reading.ChannelValues[i] : double.NaN);
                _raw[i].Add(i < rawValues.Length ? rawValues[i] : double.NaN);
            }
            return layoutGrew;
        }
    }

    /// <summary>
    /// 复制最近 <paramref name="windowSeconds"/> 秒的数据（不大于 0 时复制全部保留的点）。
    /// 只复制这一段，缓冲回绕后也一样；复制布局中的全部通道，隐藏的通道也可能参与计算通道和统计。
    /// </summary>
    public ChartSampleSnapshot Capture(double windowSeconds)
    {
        lock (_lock)
        {
            int totalCount = _times.Count;
            int start = 0;
            if (totalCount > 0 && windowSeconds > 0)
            {
                double minimumTime = _times[totalCount - 1] - windowSeconds;
                for (int i = totalCount - 1; i >= 0; i--)
                {
                    if (_times[i] < minimumTime) { start = i + 1; break; }
                }
                start = Math.Min(start, totalCount - 1);
            }
            int count = totalCount - start;
            double[] CopyRange(CircularBuffer<double> buffer)
            {
                var values = new double[count];
                for (int i = 0; i < count; i++)
                    values[i] = start + i < buffer.Count ? buffer[start + i] : double.NaN;
                return values;
            }
            var display = new double[_layout.Count][];
            var raw = new double[_layout.Count][];
            for (int ch = 0; ch < _layout.Count; ch++)
            {
                display[ch] = CopyRange(_display[ch]);
                raw[ch] = CopyRange(_raw[ch]);
            }
            return new ChartSampleSnapshot(CopyRange(_times), display, raw, totalCount);
        }
    }

    /// <summary>整个缓冲的原始值及通道名称、单位的副本（区间统计与导出用）。</summary>
    public ChartRawSnapshot SnapshotRaw()
    {
        lock (_lock)
        {
            var channels = new double[_layout.Count][];
            for (int i = 0; i < _layout.Count; i++)
                channels[i] = _raw[i].ToArray();
            return new ChartRawSnapshot(_times.ToArray(), channels, _layout.Names.ToArray(), _layout.Units.ToArray());
        }
    }

    /// <summary>整个缓冲的显示值副本，按通道索引排列。</summary>
    public double[][] SnapshotDisplay()
    {
        lock (_lock)
        {
            var channels = new double[_layout.Count][];
            for (int i = 0; i < _layout.Count; i++)
                channels[i] = _display[i].ToArray();
            return channels;
        }
    }

    /// <summary>一个通道整个缓冲的显示值副本；布局中没有这个通道时返回 null。</summary>
    public double[]? SnapshotDisplay(int channel)
    {
        lock (_lock)
            return channel >= 0 && channel < _layout.Count ? _display[channel].ToArray() : null;
    }

    /// <summary>清空数据，保留通道布局；下一条读数重新从 0 秒开始。</summary>
    public void Clear()
    {
        lock (_lock) ClearData();
    }

    private void ClearData()
    {
        _times.Clear();
        for (int i = 0; i < _display.Length; i++)
        {
            _display[i].Clear();
            _raw[i].Clear();
        }
    }

    /// <summary>通道缓冲至少分配到 <paramref name="required"/> 个；已有缓冲原样保留，只补齐缺少的部分。</summary>
    private void EnsureChannels(int required)
    {
        if (required <= _display.Length) return;
        var display = new CircularBuffer<double>[required];
        var raw = new CircularBuffer<double>[required];
        Array.Copy(_display, display, _display.Length);
        Array.Copy(_raw, raw, _raw.Length);
        for (int i = _display.Length; i < required; i++)
        {
            display[i] = new CircularBuffer<double>(_capacity);
            raw[i] = new CircularBuffer<double>(_capacity);
        }
        _display = display;
        _raw = raw;
    }
}
