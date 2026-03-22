# TASK C-1：正交度计算引擎

**文档版本**: v1.0
**编写日期**: 2026-03-21
**优先级**: P0
**所属阶段**: Phase 4
**任务流**: Stream C（正交度校正）
**前置依赖**: 无

---

## 一、基本信息

### 1.1 任务概述

实现基于椭球拟合的正交度计算引擎，从三轴磁通门传感器在稳定地磁场下的全方位旋转采集数据中，自动计算偏移向量和 3x3 补偿矩阵。该引擎是整个正交度校正模块（C-1 ~ C-5）的算法核心，后续的校正应用、向导界面、可视化、配置管理均依赖本任务输出。

### 1.2 输入/输出

| 项目 | 说明 |
|------|------|
| **输入** | N x 3 矩阵，每行为一组三轴磁通门原始读数 (Bx, By, Bz)，单位 nT；可选参考场强值 |
| **输出** | `OrthogonalityResult`，包含 `OrthogonalityParams`（偏移向量 + 补偿矩阵）和 `FitQuality`（拟合质量指标） |

### 1.3 涉及的现有代码

| 文件 | 作用 |
|------|------|
| `Core/Models/OrthogonalityParams.cs` | 正交度参数模型，已有 `Apply(x,y,z)`、`GetMatrix()`、`GetOffsetVector()` 方法 |
| `Core/Models/MagnetometerReading.cs` | 磁力数据读数 record，含 `ChannelValues`、`IsOrthogonalityCorrected` 字段 |

---

## 二、功能需求

### 2.1 核心功能

1. **椭球拟合**：对输入的 N x 3 数据矩阵执行一般椭球拟合，求解椭球方程参数
2. **参数提取**：从椭球参数中提取偏移向量（椭球中心）和形状矩阵
3. **矩阵分解**：通过 Cholesky 或 SVD 分解得到补偿变换矩阵 T
4. **半径归一化**：将变换矩阵 T 归一化，使校正后数据球体半径等于参考场强（用户指定或自动估算）
5. **拟合质量评估**：计算残差统计量，评估校正效果
6. **数据校正应用**：给定已有参数和原始三轴数据，输出校正后数据

### 2.2 算法流程

```
输入: rawData (N×3), referenceFieldStrength (可选)
│
├─ 步骤 1: 数据预检
│   ├─ 验证 N >= 100
│   ├─ 验证数据无 NaN/Inf
│   └─ 评估球面覆盖度（可选警告）
│
├─ 步骤 2: 构建设计矩阵 D (N×9)
│   │  对每行 (x, y, z)，生成行向量:
│   │  [x², y², z², xy, xz, yz, x, y, z]
│   └─ 约束方程: D · v = 1 (列向量 1_N)
│
├─ 步骤 3: 最小二乘求解
│   │  v = (D^T D)^{-1} D^T · 1_N
│   └─ 使用 MathNet.Numerics 的 Matrix.Solve 或 SVD 分解求解
│
├─ 步骤 4: 提取椭球参数
│   │  从 v = [a, b, c, d, e, f, g, h, i] 重构:
│   │  对称矩阵 A = [[a,   d/2, e/2],
│   │                 [d/2, b,   f/2],
│   │                 [e/2, f/2, c  ]]
│   │  向量 bVec = [g, h, i]
│   └─ 椭球中心: center = -A^{-1} · bVec / 2 (即偏移向量)
│
├─ 步骤 5: 计算变换矩阵
│   │  将 A 做 Cholesky 分解: A = L · L^T
│   │  或 SVD 分解: A = U · S · V^T → T = S^{1/2} · V^T
│   └─ T 为将椭球变换为球体的矩阵
│
├─ 步骤 6: 半径归一化
│   │  若用户提供 referenceFieldStrength:
│   │    T_normalized = T * (referenceFieldStrength / currentRadius)
│   │  否则:
│   │    自动估算: currentRadius = mean(|raw - offset|)
│   └─    T_normalized = T * (currentRadius_estimated / currentRadius)
│
├─ 步骤 7: 组装输出
│   │  OrthogonalityParams.Offset = center
│   │  OrthogonalityParams.CompensationMatrix = T_normalized (行优先 9 元素)
│   └─  填充 ResidualMean, ResidualStd, SampleCount
│
└─ 步骤 8: 拟合质量评估
    ├─ corrected_i = T * (raw_i - offset)
    ├─ residual_i = |corrected_i| - referenceFieldStrength
    ├─ ResidualMean = mean(residual_i)
    ├─ ResidualStd = std(residual_i)
    ├─ MaxResidual = max(|residual_i|)
    └─ SphericityCoverage = 球面覆盖度估算
```

### 2.3 数据预检要求

| 检查项 | 条件 | 处理 |
|--------|------|------|
| 样本数量 | N >= 100 | 不足则返回失败，ErrorMessage 说明 |
| 数据有效性 | 无 NaN / Inf | 自动过滤无效行，记录被过滤数量 |
| 矩阵条件数 | cond(D^T D) < 1e12 | 条件数过大时警告（数据分布可能不均匀） |
| 球面覆盖度 | 覆盖度 > 0.5（建议） | 覆盖度不足时给出警告但不阻止计算 |

### 2.4 参考场强处理

- 若用户提供 `referenceFieldStrength`（如当地地磁场总场 ~50000 nT），使用该值归一化
- 若未提供（`null`），自动估算：取校正后数据的总场均值作为参考值
- 归一化确保校正后数据的总场强度接近参考值

---

## 三、接口契约

### 3.1 新增接口 — `IOrthogonalityService`

```csharp
// 文件: Core/Calibration/IOrthogonalityService.cs
namespace MagnetometerSystem.Core.Calibration;

/// <summary>
/// 正交度校正服务接口
/// </summary>
public interface IOrthogonalityService
{
    /// <summary>
    /// 从全方位旋转采集数据中计算正交度补偿参数
    /// </summary>
    /// <param name="rawData">N×3 矩阵，每行为 (Bx, By, Bz)</param>
    /// <param name="referenceFieldStrength">参考场强 (nT)，null 则自动估算</param>
    /// <returns>计算结果，包含参数和质量指标</returns>
    OrthogonalityResult Calculate(double[,] rawData, double? referenceFieldStrength = null);

    /// <summary>
    /// 应用正交度校正到单个三轴读数
    /// </summary>
    double[] Apply(OrthogonalityParams parameters, double x, double y, double z);

    /// <summary>
    /// 评估已有参数对数据集的拟合质量
    /// </summary>
    FitQuality EvaluateFit(OrthogonalityParams parameters, double[,] rawData);
}
```

### 3.2 新增类 — `OrthogonalityResult`

```csharp
// 文件: Core/Calibration/IOrthogonalityService.cs (同文件)
namespace MagnetometerSystem.Core.Calibration;

/// <summary>
/// 正交度计算结果
/// </summary>
public class OrthogonalityResult
{
    /// <summary>计算得到的正交度参数</summary>
    public OrthogonalityParams? Parameters { get; set; }

    /// <summary>拟合质量指标</summary>
    public FitQuality? Quality { get; set; }

    /// <summary>计算是否成功</summary>
    public bool Success { get; set; }

    /// <summary>失败时的错误信息</summary>
    public string? ErrorMessage { get; set; }
}
```

### 3.3 新增类 — `FitQuality`

```csharp
// 文件: Core/Calibration/IOrthogonalityService.cs (同文件)
namespace MagnetometerSystem.Core.Calibration;

/// <summary>
/// 拟合质量指标
/// </summary>
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
```

### 3.4 对现有接口的依赖

| 接口/类 | 命名空间 | 用途 |
|---------|----------|------|
| `OrthogonalityParams` | `MagnetometerSystem.Core.Models` | 输出结果的参数载体，不修改 |
| `MathNet.Numerics.LinearAlgebra` | MathNet.Numerics | 矩阵运算、SVD、Cholesky 分解 |

---

## 四、文件清单

### 4.1 新建文件

| 文件路径 | 说明 |
|----------|------|
| `src/MagnetometerSystem.Core/Calibration/IOrthogonalityService.cs` | 接口定义 + `OrthogonalityResult` + `FitQuality` 类 |
| `src/MagnetometerSystem.Core/Calibration/OrthogonalityCalculator.cs` | `IOrthogonalityService` 的实现类 |

### 4.2 修改文件

无。本任务为纯算法层，不涉及现有文件修改。

### 4.3 DI 注册

在 `App.xaml.cs` 中注册（待集成时添加）：

```csharp
services.AddSingleton<IOrthogonalityService, OrthogonalityCalculator>();
```

---

## 五、数据库变更

本任务无数据库变更。`orthogonality_profiles` 表已在数据库设计中定义，由 C-5（配置管理）任务负责实现。

---

## 六、实现指南

### 6.1 `OrthogonalityCalculator` 类结构

```csharp
// 文件: Core/Calibration/OrthogonalityCalculator.cs
namespace MagnetometerSystem.Core.Calibration;

using MathNet.Numerics.LinearAlgebra;
using MagnetometerSystem.Core.Models;

public class OrthogonalityCalculator : IOrthogonalityService
{
    private const int MinSampleCount = 100;

    public OrthogonalityResult Calculate(double[,] rawData, double? referenceFieldStrength = null)
    {
        // 1. 数据预检
        // 2. 构建设计矩阵
        // 3. 最小二乘求解
        // 4. 提取椭球参数
        // 5. 计算变换矩阵
        // 6. 半径归一化
        // 7. 组装输出
        // 8. 评估拟合质量
    }

    public double[] Apply(OrthogonalityParams parameters, double x, double y, double z)
    {
        // 委托给 OrthogonalityParams.Apply()
        return parameters.Apply(x, y, z);
    }

    public FitQuality EvaluateFit(OrthogonalityParams parameters, double[,] rawData)
    {
        // 对每个数据点应用校正，计算残差统计
    }

    // === 私有方法 ===

    /// <summary>构建 N×9 设计矩阵</summary>
    private Matrix<double> BuildDesignMatrix(double[,] rawData) { ... }

    /// <summary>从椭球参数向量提取对称矩阵 A 和向量 b</summary>
    private (Matrix<double> A, Vector<double> b) ExtractEllipsoidParameters(Vector<double> v) { ... }

    /// <summary>计算椭球中心（偏移向量）</summary>
    private Vector<double> ComputeCenter(Matrix<double> A, Vector<double> b) { ... }

    /// <summary>通过 Cholesky/SVD 分解计算变换矩阵</summary>
    private Matrix<double> ComputeTransformMatrix(Matrix<double> A) { ... }

    /// <summary>归一化变换矩阵使球体半径等于参考场强</summary>
    private Matrix<double> NormalizeRadius(
        Matrix<double> T, Vector<double> center,
        double[,] rawData, double? referenceFieldStrength) { ... }

    /// <summary>估算球面覆盖度</summary>
    private double EstimateSphericityCoverage(double[,] rawData, Vector<double> center) { ... }

    /// <summary>数据预检：过滤 NaN/Inf 行</summary>
    private double[,] SanitizeData(double[,] rawData, out int removedCount) { ... }
}
```

### 6.2 关键算法细节

#### 6.2.1 设计矩阵构建

对于 N 个数据点，构建 N x 9 设计矩阵 D：

```
D[i] = [x_i^2, y_i^2, z_i^2, x_i*y_i, x_i*z_i, y_i*z_i, x_i, y_i, z_i]
```

约束方程为 `D * v = 1`（N 维全 1 向量），求解 9 维参数向量 v。

#### 6.2.2 MathNet 求解方式

```csharp
// 构建 D 和 ones
var D = Matrix<double>.Build.Dense(n, 9);
var ones = Vector<double>.Build.Dense(n, 1.0);

// 最小二乘求解: v = (D^T * D)^{-1} * D^T * ones
// 推荐使用 QR 分解或 SVD 以提高数值稳定性
var v = D.QR().Solve(Matrix<double>.Build.DenseOfColumnVectors(ones)).Column(0);
// 或: var svd = D.Svd(); v = svd.Solve(ones);
```

#### 6.2.3 球面覆盖度估算

将球面按经纬度划分为若干区域（如 12 x 6 = 72 个区域），统计有数据覆盖的区域比例：

```csharp
private double EstimateSphericityCoverage(double[,] rawData, Vector<double> center)
{
    const int nLon = 12; // 经度方向 12 等分
    const int nLat = 6;  // 纬度方向 6 等分
    var covered = new bool[nLon, nLat];

    for (int i = 0; i < rawData.GetLength(0); i++)
    {
        // 计算去中心后的方向角
        double dx = rawData[i, 0] - center[0];
        double dy = rawData[i, 1] - center[1];
        double dz = rawData[i, 2] - center[2];
        double r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (r < 1e-10) continue;

        double lat = Math.Asin(dz / r);            // [-pi/2, pi/2]
        double lon = Math.Atan2(dy, dx);            // [-pi, pi]

        int lonIdx = (int)((lon + Math.PI) / (2 * Math.PI) * nLon) % nLon;
        int latIdx = (int)((lat + Math.PI / 2) / Math.PI * nLat) % nLat;
        covered[lonIdx, latIdx] = true;
    }

    int total = nLon * nLat;
    int count = 0;
    foreach (bool c in covered) if (c) count++;
    return (double)count / total;
}
```

### 6.3 异常处理

| 场景 | 处理方式 |
|------|----------|
| N < 100 | 返回 `Success = false`，`ErrorMessage = "样本数量不足，至少需要 100 个数据点，当前: {N}"` |
| 设计矩阵奇异 | 捕获求解异常，返回 `Success = false`，提示数据分布问题 |
| Cholesky 分解失败（A 非正定） | 回退到 SVD 分解 |
| 数据全部为 NaN | 返回 `Success = false`，`ErrorMessage = "所有数据无效"` |

### 6.4 性能考虑

- 典型数据量：几百到几千个数据点，矩阵运算在毫秒级完成，无需异步
- `Calculate` 方法为 CPU 密集型，调用方应在后台线程调用（由 C-3 向导界面负责）
- 设计矩阵构建使用 `Matrix<double>.Build.Dense` 一次性分配内存

---

## 七、验收标准

### 7.1 功能验收

| 编号 | 验收项 | 通过条件 |
|------|--------|----------|
| AC-1 | 已知椭球数据校正 | 人工构造的标准椭球数据（已知偏移和变换），校正后残差均值 < 参考场强的 0.1% |
| AC-2 | 补偿矩阵合理性 | 对角线元素接近 1.0（偏差 < 0.1），非对角线元素量级 < 0.05 |
| AC-3 | 偏移向量准确性 | 计算得到的偏移与已知偏移的误差 < 1 nT（对模拟数据） |
| AC-4 | 样本数量校验 | N < 100 时返回 `Success = false`，包含清晰的错误信息 |
| AC-5 | 参考场强处理 | 指定参考场强时，校正后总场均值与参考值偏差 < 0.1% |
| AC-6 | 自动估算参考场强 | 未指定参考场强时，能自动估算并输出合理结果 |
| AC-7 | 拟合质量指标 | `FitQuality` 所有字段正确计算，`SampleCount` 等于有效数据点数 |
| AC-8 | NaN/Inf 过滤 | 含有无效数据时能正确过滤并继续计算 |

### 7.2 非功能验收

| 编号 | 验收项 | 通过条件 |
|------|--------|----------|
| NF-1 | 计算耗时 | 1000 个数据点的计算耗时 < 500ms |
| NF-2 | 接口合规 | 实现 `IOrthogonalityService` 的全部三个方法 |
| NF-3 | 无 UI 依赖 | `OrthogonalityCalculator` 仅依赖 `Core` 层和 `MathNet.Numerics`，无 WPF 引用 |

---

## 八、单元测试要求

测试文件：`tests/MagnetometerSystem.Core.Tests/Calibration/OrthogonalityCalculatorTests.cs`

### 8.1 测试用例清单

| 编号 | 测试名称 | 测试内容 |
|------|----------|----------|
| T-01 | `Calculate_WithKnownEllipsoid_ReturnsCorrectOffset` | 构造已知中心 (100, -200, 300) 的椭球数据，验证计算的偏移向量与已知值接近 |
| T-02 | `Calculate_WithKnownEllipsoid_ReturnsCorrectMatrix` | 构造已知变换矩阵的椭球数据，验证计算的补偿矩阵与已知矩阵接近 |
| T-03 | `Calculate_WithSphereData_ReturnsIdentityLikeMatrix` | 输入完美球体数据（无畸变），补偿矩阵应接近单位矩阵 |
| T-04 | `Calculate_CorrectedResidual_LessThanThreshold` | 校正后所有数据点的总场残差 < 参考场强的 0.1% |
| T-05 | `Calculate_WithReferenceField_NormalizesCorrectly` | 指定参考场强 50000 nT，校正后总场均值接近 50000 |
| T-06 | `Calculate_WithoutReferenceField_AutoEstimates` | 不指定参考场强，校正后总场标准差显著减小 |
| T-07 | `Calculate_InsufficientSamples_ReturnsFail` | 输入 50 个数据点，返回 `Success == false` |
| T-08 | `Calculate_WithNaNValues_FiltersAndSucceeds` | 数据中混入 NaN 行，仍能正确计算 |
| T-09 | `Apply_WithIdentityMatrix_ReturnsOriginal` | 单位矩阵 + 零偏移，`Apply` 返回原始值 |
| T-10 | `Apply_WithKnownParams_ReturnsExpected` | 已知参数校正已知输入，验证输出精确匹配 |
| T-11 | `EvaluateFit_ReturnsCorrectStatistics` | 对已知数据和参数，验证残差统计量正确 |
| T-12 | `Calculate_DualTriaxial_IndependentGroups` | 验证两组三轴数据分别独立调用 `Calculate` 的结果正确 |

### 8.2 测试数据生成辅助

```csharp
/// <summary>
/// 生成已知椭球参数的模拟数据
/// </summary>
private static double[,] GenerateEllipsoidData(
    double[] center,           // 椭球中心 [3]
    double[,] transformMatrix, // 3x3 变换矩阵（将球变为椭球）
    double radius,             // 球体半径
    int sampleCount,           // 数据点数
    double noiseStd = 0.0)     // 噪声标准差
{
    var rng = new Random(42); // 固定种子，确保可重复
    var data = new double[sampleCount, 3];

    for (int i = 0; i < sampleCount; i++)
    {
        // 在球面上均匀采样（Marsaglia 方法）
        double u, v, s;
        do {
            u = rng.NextDouble() * 2 - 1;
            v = rng.NextDouble() * 2 - 1;
            s = u * u + v * v;
        } while (s >= 1);

        double factor = 2 * Math.Sqrt(1 - s);
        double sx = u * factor * radius;
        double sy = v * factor * radius;
        double sz = (1 - 2 * s) * radius;

        // 应用变换矩阵（球→椭球）+ 加中心偏移 + 加噪声
        data[i, 0] = transformMatrix[0,0]*sx + transformMatrix[0,1]*sy + transformMatrix[0,2]*sz
                    + center[0] + rng.NextDouble() * noiseStd;
        data[i, 1] = transformMatrix[1,0]*sx + transformMatrix[1,1]*sy + transformMatrix[1,2]*sz
                    + center[1] + rng.NextDouble() * noiseStd;
        data[i, 2] = transformMatrix[2,0]*sx + transformMatrix[2,1]*sy + transformMatrix[2,2]*sz
                    + center[2] + rng.NextDouble() * noiseStd;
    }
    return data;
}
```

### 8.3 断言精度

- 偏移向量：绝对误差 < 1.0 nT（对 50000 nT 量级数据）
- 补偿矩阵：对角线元素与期望值相对误差 < 1%，非对角线元素绝对误差 < 0.01
- 残差统计：均值绝对值 < 参考场强 * 0.001，标准差 < 参考场强 * 0.001
