namespace MagnetometerSystem.Core.Calibration;

/// <summary>拟合样本与 N×3 矩阵之间的转换，以及按拟合结果逐行改正（结果页的可视化用）。</summary>
public static class OrthoMatrixHelpers
{
    /// <summary>每个样本取前 3 个值组成 N×3 矩阵。</summary>
    public static double[,] ToMatrix(IReadOnlyList<double[]> data)
    {
        int n = data.Count;
        var matrix = new double[n, 3];
        for (int i = 0; i < n; i++)
        {
            matrix[i, 0] = data[i][0];
            matrix[i, 1] = data[i][1];
            matrix[i, 2] = data[i][2];
        }
        return matrix;
    }

    /// <summary>用拟合参数改正每一行，返回新的 N×3 矩阵；参数或数值无效时抛出与单点改正相同的异常。</summary>
    public static double[,] ApplyAll(OrthogonalityResult result, double[,] rawData)
    {
        var corrected = new double[rawData.GetLength(0), 3];
        for (int i = 0; i < rawData.GetLength(0); i++)
        {
            var c = result.Parameters.Apply(rawData[i, 0], rawData[i, 1], rawData[i, 2]);
            corrected[i, 0] = c[0]; corrected[i, 1] = c[1]; corrected[i, 2] = c[2];
        }
        return corrected;
    }
}
