using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Factorization;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Calibration;

/// <summary>
/// 基于椭球拟合的正交度计算引擎。
/// 从三轴磁通门传感器全方位旋转采集数据中计算偏移向量和 3x3 补偿矩阵。
/// </summary>
public class OrthogonalityCalculator : IOrthogonalityService
{
    private const int MinSampleCount = 100;
    private const double ConditionNumberThreshold = 1e12;

    /// <inheritdoc />
    public OrthogonalityResult Calculate(double[,] rawData, double? referenceFieldStrength = null)
    {
        try
        {
            // Step 0: 数据预处理 — 自动剔除突变异常点（总场偏离均值 > 3σ）
            var dataList = MatrixToList(rawData);
            var filteredList = CalibrationDataValidator.RemoveOutliers(dataList);
            var filteredData = ListToMatrix(filteredList);

            // Step 1: 数据预检 — 过滤 NaN/Inf
            var cleanData = SanitizeData(filteredData, out int removedCount);
            int n = cleanData.GetLength(0);

            if (n == 0)
            {
                return new OrthogonalityResult
                {
                    Success = false,
                    ErrorMessage = "所有数据无效（包含 NaN 或 Inf）"
                };
            }

            if (n < MinSampleCount)
            {
                return new OrthogonalityResult
                {
                    Success = false,
                    ErrorMessage = $"样本数量不足，至少需要 {MinSampleCount} 个数据点，当前: {n}"
                };
            }

            // Step 2: 数据预处理 — 去均值以提高数值稳定性
            double meanX = 0, meanY = 0, meanZ = 0;
            for (int i = 0; i < n; i++)
            {
                meanX += cleanData[i, 0];
                meanY += cleanData[i, 1];
                meanZ += cleanData[i, 2];
            }
            meanX /= n;
            meanY /= n;
            meanZ /= n;

            var centeredData = new double[n, 3];
            for (int i = 0; i < n; i++)
            {
                centeredData[i, 0] = cleanData[i, 0] - meanX;
                centeredData[i, 1] = cleanData[i, 1] - meanY;
                centeredData[i, 2] = cleanData[i, 2] - meanZ;
            }

            // Step 3: 构建设计矩阵 D (N×9)
            var D = BuildDesignMatrix(centeredData);

            // 检查条件数
            var DTD = D.TransposeThisAndMultiply(D);
            double condNumber = DTD.ConditionNumber();
            if (condNumber > ConditionNumberThreshold)
            {
                // 条件数过大，数据分布可能不均匀，但不阻止计算
                // 继续执行，后续质量指标中 SphericityCoverage 会反映这一问题
            }

            // Step 4: 最小二乘求解 D * v = ones
            var ones = Vector<double>.Build.Dense(n, 1.0);
            Vector<double> v;
            try
            {
                // 使用 QR 分解求解以提高数值稳定性
                var onesMatrix = Matrix<double>.Build.DenseOfColumnVectors(ones);
                v = D.QR().Solve(onesMatrix).Column(0);
            }
            catch
            {
                // QR 失败，尝试 SVD
                try
                {
                    var DTones = D.TransposeThisAndMultiply(ones);
                    v = DTD.Svd().Solve(Matrix<double>.Build.DenseOfColumnVectors(DTones)).Column(0);
                }
                catch (Exception ex)
                {
                    return new OrthogonalityResult
                    {
                        Success = false,
                        ErrorMessage = $"设计矩阵求解失败，数据分布可能存在问题: {ex.Message}"
                    };
                }
            }

            // Step 5: 提取椭球参数
            // v = [a, b, c, d, e, f, g, h, i]
            // 设计矩阵行: [x², y², z², xy, xz, yz, x, y, z]
            var (A, bVec) = ExtractEllipsoidParameters(v);

            // Step 6: 计算椭球中心（在去均值坐标系中）
            Vector<double> centerCentered;
            try
            {
                centerCentered = ComputeCenter(A, bVec);
            }
            catch (Exception ex)
            {
                return new OrthogonalityResult
                {
                    Success = false,
                    ErrorMessage = $"椭球中心计算失败（矩阵 A 可能奇异）: {ex.Message}"
                };
            }

            // 转换回原始坐标系
            var center = Vector<double>.Build.DenseOfArray(new[]
            {
                centerCentered[0] + meanX,
                centerCentered[1] + meanY,
                centerCentered[2] + meanZ
            });

            // Step 7: 计算变换矩阵 T（通过 Cholesky 或 SVD 分解）
            Matrix<double> T;
            try
            {
                T = ComputeTransformMatrix(A);
            }
            catch (Exception ex)
            {
                return new OrthogonalityResult
                {
                    Success = false,
                    ErrorMessage = $"变换矩阵计算失败: {ex.Message}"
                };
            }

            // Step 8: 半径归一化
            T = NormalizeRadius(T, center, cleanData, referenceFieldStrength);

            // Step 9: 组装输出参数
            var parameters = new OrthogonalityParams
            {
                Offset = center.ToArray(),
                CompensationMatrix = MatrixToRowMajor(T),
                SampleCount = n
            };

            // Step 10: 评估拟合质量
            var quality = EvaluateFitInternal(parameters, cleanData);

            parameters.ResidualMean = quality.ResidualMean;
            parameters.ResidualStd = quality.ResidualStd;

            return new OrthogonalityResult
            {
                Parameters = parameters,
                Quality = quality,
                Success = true
            };
        }
        catch (Exception ex)
        {
            return new OrthogonalityResult
            {
                Success = false,
                ErrorMessage = $"正交度计算过程中发生未知错误: {ex.Message}"
            };
        }
    }

    /// <inheritdoc />
    public double[] Apply(OrthogonalityParams parameters, double x, double y, double z)
    {
        return parameters.Apply(x, y, z);
    }

    /// <inheritdoc />
    public FitQuality EvaluateFit(OrthogonalityParams parameters, double[,] rawData)
    {
        var cleanData = SanitizeData(rawData, out _);
        return EvaluateFitInternal(parameters, cleanData);
    }

    // ========== 私有方法 ==========

    /// <summary>
    /// 构建 N×9 设计矩阵。
    /// 对每行 (x, y, z)，生成: [x², y², z², xy, xz, yz, x, y, z]
    /// </summary>
    private Matrix<double> BuildDesignMatrix(double[,] data)
    {
        int n = data.GetLength(0);
        var D = Matrix<double>.Build.Dense(n, 9);

        for (int i = 0; i < n; i++)
        {
            double x = data[i, 0];
            double y = data[i, 1];
            double z = data[i, 2];

            D[i, 0] = x * x;       // a * x²
            D[i, 1] = y * y;       // b * y²
            D[i, 2] = z * z;       // c * z²
            D[i, 3] = x * y;       // d * xy
            D[i, 4] = x * z;       // e * xz
            D[i, 5] = y * z;       // f * yz
            D[i, 6] = x;           // g * x
            D[i, 7] = y;           // h * y
            D[i, 8] = z;           // i * z
        }

        return D;
    }

    /// <summary>
    /// 从椭球参数向量 v 提取对称矩阵 A (3x3) 和向量 b (3x1)。
    /// v = [a, b, c, d, e, f, g, h, i]
    /// A = [[a,   d/2, e/2],
    ///      [d/2, b,   f/2],
    ///      [e/2, f/2, c  ]]
    /// bVec = [g, h, i]
    /// </summary>
    private (Matrix<double> A, Vector<double> bVec) ExtractEllipsoidParameters(Vector<double> v)
    {
        double a = v[0], b = v[1], c = v[2];
        double d = v[3], e = v[4], f = v[5];
        double g = v[6], h = v[7], ii = v[8];

        var A = Matrix<double>.Build.DenseOfArray(new double[,]
        {
            { a,     d / 2, e / 2 },
            { d / 2, b,     f / 2 },
            { e / 2, f / 2, c     }
        });

        var bVec = Vector<double>.Build.DenseOfArray(new[] { g, h, ii });

        return (A, bVec);
    }

    /// <summary>
    /// 计算椭球中心: center = -A⁻¹ * bVec / 2
    /// </summary>
    private Vector<double> ComputeCenter(Matrix<double> A, Vector<double> bVec)
    {
        var AInv = A.Inverse();
        return -AInv * bVec / 2.0;
    }

    /// <summary>
    /// 通过 Cholesky 分解（首选）或 SVD 分解（降级方案）计算变换矩阵 T。
    /// A = T^T * T （Cholesky），T 将椭球变换为球体。
    /// </summary>
    private Matrix<double> ComputeTransformMatrix(Matrix<double> A)
    {
        try
        {
            // 尝试 Cholesky 分解: A = L * L^T, 其中 L 是下三角
            // 我们需要的 T 使得 A = T^T * T，所以 T = L^T（上三角）
            var cholesky = A.Cholesky();
            var T = cholesky.Factor.Transpose();
            return T;
        }
        catch
        {
            // Cholesky 分解失败（矩阵非正定），回退到 SVD
            return ComputeTransformMatrixSvd(A);
        }
    }

    /// <summary>
    /// SVD 降级方案: A = U * S * V^T → T = S^{1/2} * V^T
    /// </summary>
    private Matrix<double> ComputeTransformMatrixSvd(Matrix<double> A)
    {
        var svd = A.Svd(true);
        var S = svd.S;
        var Vt = svd.VT;

        // S^{1/2}
        var sqrtS = Matrix<double>.Build.DenseDiagonal(3, 3, i =>
        {
            double si = S[i];
            return si > 0 ? Math.Sqrt(si) : 0;
        });

        return sqrtS * Vt;
    }

    /// <summary>
    /// 归一化变换矩阵，使校正后数据球体半径等于参考场强。
    /// </summary>
    private Matrix<double> NormalizeRadius(
        Matrix<double> T, Vector<double> center,
        double[,] rawData, double? referenceFieldStrength)
    {
        int n = rawData.GetLength(0);

        // 用当前 T 和 center 计算校正后数据的平均总场
        double sumMagnitude = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = rawData[i, 0] - center[0];
            double dy = rawData[i, 1] - center[1];
            double dz = rawData[i, 2] - center[2];

            var diff = Vector<double>.Build.DenseOfArray(new[] { dx, dy, dz });
            var corrected = T * diff;
            double mag = corrected.L2Norm();
            sumMagnitude += mag;
        }

        double currentRadius = sumMagnitude / n;

        if (currentRadius < 1e-10)
        {
            // 避免除以零
            return T;
        }

        double targetRadius;
        if (referenceFieldStrength.HasValue)
        {
            targetRadius = referenceFieldStrength.Value;
        }
        else
        {
            // 自动估算：使用校正后的平均总场作为目标
            targetRadius = currentRadius;
        }

        double scale = targetRadius / currentRadius;
        return T * scale;
    }

    /// <summary>
    /// 估算球面覆盖度。将球面按经纬度划分为 12×6 = 72 个区域，
    /// 统计有数据覆盖的区域占比。
    /// </summary>
    private double EstimateSphericityCoverage(double[,] rawData, Vector<double> center)
    {
        const int nLon = 12;
        const int nLat = 6;
        var covered = new bool[nLon, nLat];

        int n = rawData.GetLength(0);
        for (int i = 0; i < n; i++)
        {
            double dx = rawData[i, 0] - center[0];
            double dy = rawData[i, 1] - center[1];
            double dz = rawData[i, 2] - center[2];
            double r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (r < 1e-10) continue;

            double lat = Math.Asin(Math.Clamp(dz / r, -1.0, 1.0)); // [-pi/2, pi/2]
            double lon = Math.Atan2(dy, dx);                        // [-pi, pi]

            int lonIdx = (int)((lon + Math.PI) / (2 * Math.PI) * nLon);
            if (lonIdx >= nLon) lonIdx = nLon - 1;
            int latIdx = (int)((lat + Math.PI / 2) / Math.PI * nLat);
            if (latIdx >= nLat) latIdx = nLat - 1;

            covered[lonIdx, latIdx] = true;
        }

        int total = nLon * nLat;
        int count = 0;
        foreach (bool c in covered)
            if (c) count++;

        return (double)count / total;
    }

    /// <summary>
    /// 数据预检：过滤包含 NaN 或 Inf 的行。
    /// </summary>
    private double[,] SanitizeData(double[,] rawData, out int removedCount)
    {
        int n = rawData.GetLength(0);
        int cols = rawData.GetLength(1);
        removedCount = 0;

        if (cols < 3)
        {
            removedCount = n;
            return new double[0, 3];
        }

        // 第一遍：计算有效行数
        var validRows = new List<int>();
        for (int i = 0; i < n; i++)
        {
            bool valid = true;
            for (int j = 0; j < 3; j++)
            {
                if (double.IsNaN(rawData[i, j]) || double.IsInfinity(rawData[i, j]))
                {
                    valid = false;
                    break;
                }
            }
            if (valid) validRows.Add(i);
        }

        removedCount = n - validRows.Count;

        var result = new double[validRows.Count, 3];
        for (int i = 0; i < validRows.Count; i++)
        {
            result[i, 0] = rawData[validRows[i], 0];
            result[i, 1] = rawData[validRows[i], 1];
            result[i, 2] = rawData[validRows[i], 2];
        }

        return result;
    }

    /// <summary>
    /// 内部拟合质量评估，对每个数据点应用校正后计算残差统计。
    /// </summary>
    private FitQuality EvaluateFitInternal(OrthogonalityParams parameters, double[,] rawData)
    {
        int n = rawData.GetLength(0);
        var matrix = parameters.GetMatrix();
        var offset = parameters.GetOffsetVector();

        // 计算校正后各点的总场强度
        var magnitudes = new double[n];
        for (int i = 0; i < n; i++)
        {
            var raw = Vector<double>.Build.DenseOfArray(new[]
            {
                rawData[i, 0], rawData[i, 1], rawData[i, 2]
            });
            var corrected = matrix * (raw - offset);
            magnitudes[i] = corrected.L2Norm();
        }

        // 计算参考场强（校正后总场均值）
        double meanMagnitude = 0;
        for (int i = 0; i < n; i++)
            meanMagnitude += magnitudes[i];
        meanMagnitude /= n;

        // 计算残差: residual_i = |corrected_i| - meanMagnitude
        var residuals = new double[n];
        for (int i = 0; i < n; i++)
            residuals[i] = magnitudes[i] - meanMagnitude;

        // 统计量
        double residualMean = 0;
        for (int i = 0; i < n; i++)
            residualMean += residuals[i];
        residualMean /= n;

        double residualVariance = 0;
        for (int i = 0; i < n; i++)
        {
            double diff = residuals[i] - residualMean;
            residualVariance += diff * diff;
        }
        residualVariance /= n;
        double residualStd = Math.Sqrt(residualVariance);

        double maxResidual = 0;
        for (int i = 0; i < n; i++)
        {
            double absRes = Math.Abs(residuals[i]);
            if (absRes > maxResidual) maxResidual = absRes;
        }

        // 球面覆盖度
        double coverage = EstimateSphericityCoverage(rawData, offset);

        return new FitQuality
        {
            ResidualMean = residualMean,
            ResidualStd = residualStd,
            MaxResidual = maxResidual,
            SampleCount = n,
            SphericityCoverage = coverage
        };
    }

    /// <summary>
    /// 将 3x3 MathNet 矩阵转换为行优先存储的 9 元素数组。
    /// </summary>
    private static double[] MatrixToRowMajor(Matrix<double> m)
    {
        return new[]
        {
            m[0, 0], m[0, 1], m[0, 2],
            m[1, 0], m[1, 1], m[1, 2],
            m[2, 0], m[2, 1], m[2, 2]
        };
    }

    /// <summary>
    /// 将 N×3 矩阵转换为 List&lt;double[]&gt;
    /// </summary>
    private static List<double[]> MatrixToList(double[,] matrix)
    {
        int n = matrix.GetLength(0);
        var list = new List<double[]>(n);
        for (int i = 0; i < n; i++)
        {
            list.Add(new[] { matrix[i, 0], matrix[i, 1], matrix[i, 2] });
        }
        return list;
    }

    /// <summary>
    /// 将 List&lt;double[]&gt; 转换为 N×3 矩阵
    /// </summary>
    private static double[,] ListToMatrix(List<double[]> list)
    {
        int n = list.Count;
        var matrix = new double[n, 3];
        for (int i = 0; i < n; i++)
        {
            matrix[i, 0] = list[i][0];
            matrix[i, 1] = list[i][1];
            matrix[i, 2] = list[i][2];
        }
        return matrix;
    }
}
