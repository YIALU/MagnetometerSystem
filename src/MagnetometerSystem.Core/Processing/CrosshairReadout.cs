using System.Globalization;
using System.Text;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Processing;

/// <summary>图表十字准线的读数文字。</summary>
public static class CrosshairReadout
{
    /// <summary>
    /// 离 <paramref name="t"/> 最近的时间点（距离相同取后一个）及给定通道在该点的原始值（不含显示偏移和滤波），
    /// 按当前区域性格式化。<paramref name="t"/> 超出数据时间范围时返回 null；没有该点数据的通道略过。
    /// </summary>
    public static string? Format(double[] times, double[][] raw, double t, IEnumerable<ChannelDisplayConfig> channels)
    {
        if (t < times[0] || t > times[^1]) return null;
        int i = Array.BinarySearch(times, t);
        if (i < 0)
        {
            i = ~i;
            if (i >= times.Length || (i > 0 && t - times[i - 1] < times[i] - t)) i--;
        }
        var sb = new StringBuilder($"{times[i]:0.000} s（原始值）");
        foreach (var cfg in channels)
        {
            if (cfg.ChannelIndex >= raw.Length || i >= raw[cfg.ChannelIndex].Length) continue;
            sb.Append('\n').Append(cfg.Name).Append("  ").Append(raw[cfg.ChannelIndex][i].ToString("G8", CultureInfo.CurrentCulture));
            if (!string.IsNullOrEmpty(cfg.Unit)) sb.Append(' ').Append(cfg.Unit);
        }
        return sb.ToString();
    }
}
