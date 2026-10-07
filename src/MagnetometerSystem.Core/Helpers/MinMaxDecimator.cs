namespace MagnetometerSystem.Core.Helpers;

/// <summary>
/// 最小 / 最大值抽取：按下标把数据分桶，每桶保留最小值和最大值两点（按原顺序），跳过非有限值。
/// 直接遍历源数组，不复制全量数据，适合在界面线程上为大数组生成显示点；保留每个桶的极值，曲线包络不丢失。
/// </summary>
public static class MinMaxDecimator
{
    public static (double[] Xs, double[] Ys) Decimate(double[] xs, double[] ys, int maxPoints)
    {
        if (xs.Length != ys.Length) throw new ArgumentException("xs 与 ys 长度不一致");
        if (maxPoints < 2) throw new ArgumentException("maxPoints 必须 >= 2");
        int n = ys.Length;
        int buckets = n <= maxPoints ? n : maxPoints / 2;
        var outX = new List<double>(Math.Min(n, maxPoints));
        var outY = new List<double>(Math.Min(n, maxPoints));
        for (int b = 0; b < buckets; b++)
        {
            int start = (int)((long)n * b / buckets), end = (int)((long)n * (b + 1) / buckets);
            int iMin = -1, iMax = -1;
            for (int i = start; i < end; i++)
            {
                if (!double.IsFinite(ys[i]) || !double.IsFinite(xs[i])) continue;
                if (iMin < 0 || ys[i] < ys[iMin]) iMin = i;
                if (iMax < 0 || ys[i] > ys[iMax]) iMax = i;
            }
            if (iMin < 0) continue;
            int first = Math.Min(iMin, iMax), second = Math.Max(iMin, iMax);
            outX.Add(xs[first]); outY.Add(ys[first]);
            if (second != first) { outX.Add(xs[second]); outY.Add(ys[second]); }
        }
        return (outX.ToArray(), outY.ToArray());
    }
}
