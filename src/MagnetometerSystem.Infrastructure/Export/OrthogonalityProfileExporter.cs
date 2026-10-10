using System.Globalization;
using System.Text;
using System.Text.Json;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Infrastructure.Export;

/// <summary>单个正交度配置的导出内容（CSV / JSON）和默认文件名；不弹对话框、不写文件。</summary>
public static class OrthogonalityProfileExporter
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// 单个正交度配置的 CSV：名称与序列号按 RFC 4180 加引号（内部引号加倍，逗号与换行留在引号内），
    /// 数值用不变区域性的往返格式。
    /// </summary>
    public static string BuildCsv(OrthogonalityParams p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("name,sensor_serial,created_at,unit,sample_count,residual_mean,residual_std," +
                      "offset_x,offset_y,offset_z," +
                      "m00,m01,m02,m10,m11,m12,m20,m21,m22");
        static string Text(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        static string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string DN(double? v) => v.HasValue ? D(v.Value) : "";
        sb.Append($"{Text(p.Name)},{Text(p.SensorSerial)},{p.CreatedAt:yyyy-MM-dd HH:mm:ss},");
        sb.Append($"{OrthogonalityParams.CanonicalUnit(p.Unit)},{p.SampleCount},{DN(p.ResidualMean)},{DN(p.ResidualStd)},");
        sb.Append($"{D(p.Offset[0])},{D(p.Offset[1])},{D(p.Offset[2])},");
        for (int i = 0; i < 9; i++)
        {
            sb.Append(D(p.CompensationMatrix[i]));
            if (i < 8) sb.Append(',');
        }
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>缩进格式的 JSON，字段与配置库里保存的一致（含拟合单位）。</summary>
    public static string BuildJson(OrthogonalityParams p) => JsonSerializer.Serialize(p, IndentedJson);

    /// <summary>由配置名称生成文件名：去掉当前系统不允许的字符，最长 80 个字符；为空时用 profile。</summary>
    public static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "profile";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name)
            if (Array.IndexOf(invalid, c) < 0) sb.Append(c);
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "profile" : (s.Length > 80 ? s[..80] : s);
    }
}
