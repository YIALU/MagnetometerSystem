using MathNet.Numerics.LinearAlgebra;

namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 正交度校正参数
/// </summary>
public class OrthogonalityParams
{
    /// <summary>配置 ID</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>配置名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>关联传感器序列号</summary>
    public string? SensorSerial { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>偏移向量 [Ox, Oy, Oz]</summary>
    public double[] Offset { get; set; } = [0, 0, 0];

    /// <summary>
    /// 3x3 补偿矩阵（行优先存储，共 9 个元素）
    /// [m00, m01, m02, m10, m11, m12, m20, m21, m22]
    /// </summary>
    public double[] CompensationMatrix { get; set; } = [1, 0, 0, 0, 1, 0, 0, 0, 1];

    /// <summary>残差均值</summary>
    public double? ResidualMean { get; set; }

    /// <summary>残差标准差</summary>
    public double? ResidualStd { get; set; }

    /// <summary>用于拟合的样本数</summary>
    public int? SampleCount { get; set; }

    /// <summary>备注</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// 获取 MathNet 矩阵形式的补偿矩阵
    /// </summary>
    public Matrix<double> GetMatrix()
    {
        return Matrix<double>.Build.DenseOfRowMajor(3, 3, CompensationMatrix);
    }

    /// <summary>
    /// 获取 MathNet 向量形式的偏移
    /// </summary>
    public Vector<double> GetOffsetVector()
    {
        return Vector<double>.Build.DenseOfArray(Offset);
    }

    /// <summary>
    /// 对原始三轴数据应用正交度校正
    /// corrected = M * (raw - offset)
    /// </summary>
    public double[] Apply(double x, double y, double z)
    {
        var raw = Vector<double>.Build.DenseOfArray([x, y, z]);
        var offset = GetOffsetVector();
        var matrix = GetMatrix();
        var corrected = matrix * (raw - offset);
        return corrected.ToArray();
    }
}
