using System.Globalization;

namespace MagnetometerSystem.Core.Calibration;

/// <summary>
/// CSV 导入结果：第一组、第二组（仅双三轴）三轴样本，以及无法解析而跳过的行数（空行不计）。
/// 双三轴时第二组缺列或无法解析的行只计入第一组，所以第二组可能比第一组少。
/// </summary>
public sealed record CalibrationCsvImport(List<double[]> First, List<double[]> Second, int SkippedLines);

/// <summary>
/// 解析外部 CSV / 文本文件中的三轴校正样本；只处理文本行，不读文件、不弹窗。
/// 分隔符为逗号、制表符或分号，数值按不变区域性解析（支持指数）。
/// 第一行列数足够且全部不是数值时视为表头，按列名定位各轴；否则按位置取值，首列不是数值时视为时间戳并跳过。
/// </summary>
public static class CalibrationCsvImporter
{
    public static CalibrationCsvImport Parse(IReadOnlyList<string> lines, bool dual)
    {
        var importedData = new List<double[]>();
        var importedDataSecond = new List<double[]>();
        int skippedLines = 0;
        int requiredCols = dual ? 6 : 3;

        // 第一行特判 header：所有列都无法 parse 成 double ⇒ 是 header
        int startIdx = 0;
        int[]? columnMap = null; // 长度 = requiredCols，映射到具体列索引
        if (lines.Count > 0)
        {
            var firstParts = SplitCsvLine(lines[0]);
            if (firstParts.Length >= requiredCols && !firstParts.Any(p => TryParseDouble(p, out _)))
            {
                // 是 header，尝试按列名定位
                columnMap = BuildColumnMap(firstParts, dual);
                startIdx = 1;
            }
        }

        for (int i = startIdx; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = SplitCsvLine(line);

            // 有 header 列名映射时直接按 map 取
            if (columnMap != null)
            {
                if (!TryExtractByMap(parts, columnMap, 0, 3, out var triple)) { skippedLines++; continue; }
                importedData.Add(triple);
                if (dual)
                {
                    if (TryExtractByMap(parts, columnMap, 3, 3, out var triple2))
                        importedDataSecond.Add(triple2);
                }
                continue;
            }

            // 无 header：先尝试前 3 列；若前 3 列含非 double（可能第一列是时间戳）则跳过第一列试 1..3
            if (parts.Length < requiredCols) { skippedLines++; continue; }

            int offset = 0;
            if (!TryParseDouble(parts[0], out _) && parts.Length >= requiredCols + 1) offset = 1;

            if (parts.Length < offset + requiredCols) { skippedLines++; continue; }

            if (TryParseDouble(parts[offset], out var bx) &&
                TryParseDouble(parts[offset + 1], out var by) &&
                TryParseDouble(parts[offset + 2], out var bz))
            {
                importedData.Add(new[] { bx, by, bz });

                if (dual && parts.Length >= offset + 6 &&
                    TryParseDouble(parts[offset + 3], out var bx2) &&
                    TryParseDouble(parts[offset + 4], out var by2) &&
                    TryParseDouble(parts[offset + 5], out var bz2))
                {
                    importedDataSecond.Add(new[] { bx2, by2, bz2 });
                }
            }
            else
            {
                skippedLines++;
            }
        }

        return new CalibrationCsvImport(importedData, importedDataSecond, skippedLines);
    }

    private static string[] SplitCsvLine(string line) =>
        line.Split(new[] { ',', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim()).ToArray();

    private static bool TryParseDouble(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static bool TryExtractByMap(string[] parts, int[] map, int start, int count, out double[] result)
    {
        result = new double[count];
        for (int i = 0; i < count; i++)
        {
            int colIdx = map[start + i];
            if (colIdx < 0 || colIdx >= parts.Length || !TryParseDouble(parts[colIdx], out result[i]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// 按 header 列名定位 X/Y/Z (双三轴: X1/Y1/Z1/X2/Y2/Z2)。
    /// 找不到的列返回 -1，TryExtractByMap 会因此返回 false 并跳行。
    /// </summary>
    private static int[] BuildColumnMap(string[] headers, bool dual)
    {
        var lower = headers.Select(h => h.ToLowerInvariant().Trim()).ToArray();

        int find(params string[] names)
        {
            foreach (var n in names)
            {
                int idx = Array.IndexOf(lower, n);
                if (idx >= 0) return idx;
            }
            return -1;
        }

        if (dual)
        {
            return new[]
            {
                find("x1", "bx1", "ch0"),
                find("y1", "by1", "ch1"),
                find("z1", "bz1", "ch2"),
                find("x2", "bx2", "ch3"),
                find("y2", "by2", "ch4"),
                find("z2", "bz2", "ch5"),
            };
        }
        return new[]
        {
            find("x", "bx", "ch0"),
            find("y", "by", "ch1"),
            find("z", "bz", "ch2"),
        };
    }
}
