using MagnetometerSystem.Core.Helpers;

namespace MagnetometerSystem.Core.Processing;

/// <summary>
/// 计算通道求值：按公式文本缓存解析结果（解析失败不缓存），并在一段通道缓冲上逐点求值。
/// 只在界面线程使用，不加锁。
/// </summary>
public sealed class ComputedChannelEvaluator
{
    private readonly Dictionary<string, FormulaEvaluator> _cache = new();

    /// <summary>取缓存的求值器，没有时解析并缓存；解析失败返回 null。</summary>
    public FormulaEvaluator? GetOrCreate(string formula)
    {
        if (_cache.TryGetValue(formula, out var cached))
            return cached;

        try
        {
            var eval = new FormulaEvaluator(formula);
            _cache[formula] = eval;
            return eval;
        }
        catch
        {
            return null;
        }
    }

    public void Forget(string formula) => _cache.Remove(formula);

    public void Clear() => _cache.Clear();

    /// <summary>
    /// 对 channels[*][start + i]（0 ≤ i &lt; count）逐点求公式值。某通道在该点没有数据时用 <paramref name="missing"/> 代替：
    /// 绘图和单条计算通道的自动偏移历来用 0，一键归零用 NaN，调用方必须显式给出，不能统一。
    /// 公式无法解析时返回 null。
    /// </summary>
    public double[]? Evaluate(string formula, double[][] channels, int start, int count, double missing)
    {
        var evaluator = GetOrCreate(formula);
        if (evaluator == null) return null;

        var values = new double[count];
        var row = new double[channels.Length];
        for (int i = 0; i < count; i++)
        {
            for (int ch = 0; ch < row.Length; ch++)
                row[ch] = channels[ch].Length > start + i ? channels[ch][start + i] : missing;
            values[i] = evaluator.Evaluate(row);
        }
        return values;
    }
}
