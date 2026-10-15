namespace MagnetometerSystem.Core.Calibration;

/// <summary>某一时刻的拟合样本：两组样本的副本，以及这一批的单位、通道数和代号。</summary>
/// <param name="First">第一组三轴样本（X、Y、Z）。</param>
/// <param name="Second">双三轴时的第二组样本；单三轴为空。</param>
/// <param name="Unit">样本的磁场单位。</param>
/// <param name="ChannelCount">每个读数取的通道数：单三轴 3，双三轴 6。</param>
/// <param name="Generation">这一批的代号。</param>
public sealed record CalibrationSamples(List<double[]> First, List<double[]> Second, string Unit, int ChannelCount, long Generation);

/// <summary>
/// 正交度拟合的一批样本：第一组三轴、双三轴时的第二组，以及这一批的单位、通道数、来源和所用通道。
/// 每换一批（开始实时采集、导入文件或会话）代号加一；带旧代号的追加、撤销和清空不再生效，
/// 迟到的读数回调和在途的计算据此发现样本已经换了。所有成员都可以从任意线程调用。
/// </summary>
public sealed class CalibrationSampleSet
{
    private readonly object _lock = new();
    private readonly List<double[]> _first = new();
    private readonly List<double[]> _second = new();
    private long _generation;
    private string _unit = "";
    private int _channelCount;
    private string _sourceKey = "";
    private int[]? _map;

    /// <summary>当前这一批的代号。</summary>
    public long Generation { get { lock (_lock) return _generation; } }

    /// <summary>样本的磁场单位；还没有样本来源时为空。</summary>
    public string Unit { get { lock (_lock) return _unit; } }

    /// <summary>每个读数取的通道数：单三轴 3，双三轴 6。</summary>
    public int ChannelCount { get { lock (_lock) return _channelCount; } }

    /// <summary>样本来自哪个来源（实时布局、会话或文件）；为空表示不记来源。</summary>
    public string SourceKey { get { lock (_lock) return _sourceKey; } }

    /// <summary>取样时用的通道索引（X、Y、Z[、X2、Y2、Z2]）；导入文件有自己的列映射，为 null。</summary>
    public IReadOnlyList<int>? Map { get { lock (_lock) return _map; } }

    /// <summary>第一组的样本数。</summary>
    public int Count { get { lock (_lock) return _first.Count; } }

    /// <summary>换一批样本：两组样本、单位、通道数、来源和所用通道一起替换，返回新的代号。</summary>
    public long Replace(IEnumerable<double[]> first, IEnumerable<double[]> second, string unit, int channelCount,
        string sourceKey = "", IReadOnlyList<int>? map = null)
    {
        lock (_lock)
        {
            _first.Clear();
            _second.Clear();
            _first.AddRange(first);
            _second.AddRange(second);
            _unit = unit;
            _channelCount = channelCount;
            _sourceKey = sourceKey;
            _map = map?.ToArray();
            return ++_generation;
        }
    }

    /// <summary>
    /// 向代号为 <paramref name="generation"/> 的这一批追加一个样本，双三轴同时追加第二组。
    /// 已经换了一批时不追加并返回 -1，否则返回追加后的样本数。
    /// </summary>
    public int TryAdd(long generation, double[] first, double[]? second = null)
    {
        lock (_lock)
        {
            if (generation != _generation) return -1;
            _first.Add(first);
            if (second != null) _second.Add(second);
            return _first.Count;
        }
    }

    /// <summary>
    /// 撤销这一批的最后一个样本；第二组比第一组多时同时撤销它的最后一个，两组保持逐点对应。
    /// 返回剩下的样本数；已经换了一批或没有样本时返回 -1。
    /// </summary>
    public int TryRemoveLast(long generation)
    {
        lock (_lock)
        {
            if (generation != _generation || _first.Count == 0) return -1;
            _first.RemoveAt(_first.Count - 1);
            if (_second.Count > _first.Count) _second.RemoveAt(_second.Count - 1);
            return _first.Count;
        }
    }

    /// <summary>清空这一批的样本，单位与来源不变。返回清掉的样本数；已经换了一批时返回 -1。</summary>
    public int TryClear(long generation)
    {
        lock (_lock)
        {
            if (generation != _generation) return -1;
            int removed = _first.Count;
            _first.Clear();
            _second.Clear();
            return removed;
        }
    }

    /// <summary>作废当前这一批：清空样本并推进代号，在途的追加和计算都不再生效。</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _first.Clear();
            _second.Clear();
            _generation++;
        }
    }

    /// <summary>两组样本的副本连同单位、通道数和代号，在同一把锁内取得。</summary>
    public CalibrationSamples Snapshot()
    {
        lock (_lock) return new CalibrationSamples(_first.ToList(), _second.ToList(), _unit, _channelCount, _generation);
    }
}
