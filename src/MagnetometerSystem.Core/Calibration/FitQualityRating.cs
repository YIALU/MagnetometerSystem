using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Calibration;

/// <summary>
/// 正交度拟合质量评级：残差标准差折算到 nT 后按 10 / 50 / 200 nT 分级，任何拟合单位下阈值的物理含义相同。
/// </summary>
public static class FitQualityRating
{
    /// <summary>残差标准差折算到 nT；没有结果、拟合单位未知或残差不是非负有限值时为 NaN。</summary>
    public static double ResidualStdInNt(OrthogonalityResult? result)
    {
        if (result == null) return double.NaN;
        var scale = OrthogonalityParams.CanonicalUnit(result.Parameters.Unit) switch
        {
            "nT" => 1d, "uT" => 1e3, "mT" => 1e6, "T" => 1e9, _ => double.NaN
        };
        var residualNt = result.Quality.ResidualStd * scale;
        return double.IsFinite(residualNt) && residualNt >= 0 ? residualNt : double.NaN;
    }

    /// <summary>评级文字：没有结果为“—”，无法折算为“未知”，其余为优秀 / 良好 / 一般 / 较差。</summary>
    public static string Rate(OrthogonalityResult? result)
    {
        if (result == null) return "—";
        var residualNt = ResidualStdInNt(result);
        if (double.IsNaN(residualNt)) return "未知";
        return residualNt switch { < 10 => "优秀", < 50 => "良好", < 200 => "一般", _ => "较差" };
    }

    /// <summary>评级对应的状态色：ok / warn / err；无法折算（NaN）时为 err。</summary>
    public static string Level(double residualNt) =>
        residualNt switch { < 50 => "ok", < 200 => "warn", _ => "err" };

    /// <summary>结果页评级下方的说明文字。</summary>
    public static string Hint(double residualNt) => residualNt switch
    {
        < 10 => "残差标准差低于 10 nT",
        < 50 => "残差标准差低于 50 nT",
        < 200 => "残差标准差低于 200 nT，建议增加姿态覆盖后重算",
        double.NaN => "单位未知，不能评级",
        _ => "残差标准差不低于 200 nT，建议检查数据后重新采集",
    };
}
