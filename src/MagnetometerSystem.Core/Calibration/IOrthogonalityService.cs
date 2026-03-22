using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Calibration;

/// <summary>
/// 正交度计算与校正服务
/// </summary>
public interface IOrthogonalityService
{
    /// <summary>
    /// 从全方位旋转采集数据计算正交度补偿矩阵
    /// </summary>
    /// <param name="rawData">N x 3 矩阵（每行为 Bx, By, Bz）</param>
    /// <param name="referenceFieldStrength">参考场强(nT)，null 则自动估计</param>
    OrthogonalityResult Calculate(double[,] rawData, double? referenceFieldStrength = null);

    /// <summary>
    /// 对单个三轴读数应用正交度校正
    /// </summary>
    double[] Apply(OrthogonalityParams parameters, double x, double y, double z);

    /// <summary>
    /// 评估拟合质量
    /// </summary>
    FitQuality EvaluateFit(OrthogonalityParams parameters, double[,] rawData);
}

/// <summary>正交度计算结果</summary>
public class OrthogonalityResult
{
    /// <summary>计算得到的正交度参数</summary>
    public OrthogonalityParams Parameters { get; set; } = new();

    /// <summary>拟合质量指标</summary>
    public FitQuality Quality { get; set; } = new();

    /// <summary>计算是否成功</summary>
    public bool Success { get; set; }

    /// <summary>失败时的错误信息</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>拟合质量指标</summary>
public class FitQuality
{
    /// <summary>残差均值 (nT)</summary>
    public double ResidualMean { get; set; }

    /// <summary>残差标准差 (nT)</summary>
    public double ResidualStd { get; set; }

    /// <summary>最大残差绝对值 (nT)</summary>
    public double MaxResidual { get; set; }

    /// <summary>用于拟合的样本数</summary>
    public int SampleCount { get; set; }

    /// <summary>
    /// 球面覆盖度 (0.0~1.0)
    /// 将球面划分为若干区域，统计有数据覆盖的区域占比
    /// </summary>
    public double SphericityCoverage { get; set; }
}
