using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Calibration;

/// <summary>
/// 正交度拟合取哪几个通道作为 X / Y / Z（双三轴两组）。通道顺序、名称和单位由协议决定，
/// 不能假定“前三个通道就是 X / Y / Z”：CCT-5 的顺序是 X1、X2、Y1……，数采卡前面是磁分量、后面还有梯度和姿态。
/// </summary>
public static class FittingChannelMap
{
    /// <summary>
    /// 建议的通道索引：每组按 X、Y、Z 排列，共 <paramref name="groups"/> 组；给不出可靠建议时返回 null，由用户选择。
    /// 先按名称识别：同一探头（名称去掉轴字母后相同，如 X1 / Y1 / Z1、Bx / By / Bz）的三个轴组成一组，
    /// 按出现顺序取前几组；只考虑磁场单位（nT / uT / mT / T）的通道，Δ 开头的梯度通道除外。
    /// 名称识别不出时，只有磁场通道恰好 3×groups 个且单位相同才按顺序分配，不截取前缀。
    /// </summary>
    public static int[]? Suggest(IReadOnlyList<string> names, IReadOnlyList<string> units, int groups)
    {
        if (groups is not (1 or 2) || names.Count != units.Count) return null;
        var magnetic = Enumerable.Range(0, names.Count)
            .Where(i => OrthogonalityParams.CanonicalUnit(units[i]).Length > 0 && !(names[i] ?? "").TrimStart().StartsWith('Δ'))
            .ToArray();

        var probes = new List<(string Key, string Unit, int?[] Axes)>();
        foreach (var i in magnetic)
        {
            if (AxisOf(names[i] ?? "") is not var (axis, key)) continue;
            var unit = OrthogonalityParams.CanonicalUnit(units[i]);
            var probe = probes.FirstOrDefault(p => p.Key == key && p.Unit == unit);
            if (probe.Axes == null) probes.Add(probe = (key, unit, new int?[3]));
            probe.Axes[axis] ??= i;
        }
        var complete = probes.Where(p => p.Axes.All(a => a.HasValue)).Take(groups).ToList();
        if (complete.Count == groups)
            return complete.SelectMany(p => p.Axes.Select(a => a!.Value)).ToArray();

        if (magnetic.Length == 3 * groups
            && magnetic.Select(i => OrthogonalityParams.CanonicalUnit(units[i])).Distinct().Count() == 1)
            return magnetic;
        return null;
    }

    /// <summary>检查所选通道：数量正确、互不相同、都在范围内且为同一磁场单位。返回该单位，否则抛出说明原因的异常。</summary>
    public static string Validate(IReadOnlyList<int> indices, IReadOnlyList<string> units, int groups)
    {
        if (indices.Count != 3 * groups || indices.Any(i => i < 0 || i >= units.Count))
            throw new ArgumentException(groups == 2
                ? "请在“拟合通道”中为两组探头的 X、Y、Z 各选一个通道。"
                : "请在“拟合通道”中为 X、Y、Z 各选一个通道。");
        if (indices.Distinct().Count() != indices.Count)
            throw new ArgumentException("拟合通道不能重复选择同一个通道。");
        var selected = indices.Select(i => OrthogonalityParams.CanonicalUnit(units[i])).ToArray();
        if (selected.Any(u => u.Length == 0) || selected.Distinct().Count() != 1)
            throw new ArgumentException("拟合通道必须是同一磁场单位（nT / uT / mT / T）的通道。");
        return selected[0];
    }

    /// <summary>名称里唯一的轴字母（X / Y / Z，不分大小写）及去掉它之后的探头标识；没有或不止一个时返回 null。</summary>
    private static (int Axis, string Key)? AxisOf(string name)
    {
        var trimmed = name.Trim();
        int position = -1;
        for (int i = 0; i < trimmed.Length; i++)
        {
            if (char.ToUpperInvariant(trimmed[i]) is not ('X' or 'Y' or 'Z')) continue;
            if (position >= 0) return null;
            position = i;
        }
        if (position < 0) return null;
        // 轴字母所在的英文字母串只能是轴字母本身（X1、Mag_X、磁力仪X），或单个前缀字母加轴字母（Bx、Hx1）；
        // “Max / May / Maz”“Xaxis”之类更长的单词不当成轴，免得把无关的列自动选作 X / Y / Z。
        int runStart = position, runEnd = position + 1;
        while (runStart > 0 && char.IsAsciiLetter(trimmed[runStart - 1])) runStart--;
        while (runEnd < trimmed.Length && char.IsAsciiLetter(trimmed[runEnd])) runEnd++;
        bool standalone = runEnd - runStart == 1;
        bool prefixed = runEnd - runStart == 2 && position == runEnd - 1;
        if (!standalone && !prefixed) return null;
        return ("XYZ".IndexOf(char.ToUpperInvariant(trimmed[position])),
                (trimmed[..position] + trimmed[(position + 1)..]).ToUpperInvariant());
    }
}
