# TASK-TESTS: 单元测试与集成测试

**文档版本**: v1.0
**更新日期**: 2026-03-21
**优先级**: P1
**阶段**: Phase 5
**流**: All Streams
**依赖**: 各被测模块已完成

---

## 一、基本信息

### 背景

当前 `Core.Tests` 项目为空目录，零测试覆盖。随着 Phase 4/5 新增大量处理算法和协议解析逻辑，亟需建立单元测试基线，确保核心逻辑的正确性和回归安全。

### 目标

为 Core 项目的关键模块编写单元测试，覆盖 `Calibration`、`Processing`、`Protocol`、`Helpers` 四个子目录，目标代码覆盖率 >= 80%。所有测试遵循 Arrange-Act-Assert 模式。

---

## 二、功能需求

1. **测试项目搭建** — 创建 xUnit 测试项目，配置必要的 NuGet 依赖。
2. **Helpers 测试** — 覆盖 FormulaEvaluator、CircularBuffer、LttbDownsampler。
3. **Protocol 测试** — 覆盖 AsciiLineParser、ConfigurableBinaryParser。
4. **Calibration 测试** — 覆盖 OrthogonalityCalculator。
5. **Processing 测试** — 覆盖 DataProcessor、RollingStatisticsEngine。
6. **覆盖率目标** — Core/Calibration、Core/Processing、Core/Protocol、Core/Helpers 四个目录总覆盖率 >= 80%。

---

## 三、文件清单

### 项目文件

| 操作 | 文件路径 | 说明 |
|------|---------|------|
| 新建 | `tests/MagnetometerSystem.Core.Tests/MagnetometerSystem.Core.Tests.csproj` | 测试项目文件 |

### 测试文件

| 操作 | 文件路径 | 被测目标 |
|------|---------|---------|
| 新建 | `tests/MagnetometerSystem.Core.Tests/Helpers/FormulaEvaluatorTests.cs` | `Core/Helpers/FormulaEvaluator.cs` |
| 新建 | `tests/MagnetometerSystem.Core.Tests/Helpers/CircularBufferTests.cs` | `Core/Helpers/CircularBuffer.cs` |
| 新建 | `tests/MagnetometerSystem.Core.Tests/Helpers/LttbDownsamplerTests.cs` | `Core/Helpers/LttbDownsampler.cs` |
| 新建 | `tests/MagnetometerSystem.Core.Tests/Protocol/AsciiLineParserTests.cs` | `Core/Protocol/AsciiLineParser.cs` |
| 新建 | `tests/MagnetometerSystem.Core.Tests/Protocol/ConfigurableBinaryParserTests.cs` | `Core/Protocol/ConfigurableBinaryParser.cs` |
| 新建 | `tests/MagnetometerSystem.Core.Tests/Calibration/OrthogonalityCalculatorTests.cs` | `Core/Calibration/OrthogonalityCalculator.cs` |
| 新建 | `tests/MagnetometerSystem.Core.Tests/Processing/DataProcessorTests.cs` | `Core/Processing/DataProcessor.cs` |
| 新建 | `tests/MagnetometerSystem.Core.Tests/Processing/RollingStatisticsEngineTests.cs` | `Core/Processing/RollingStatisticsEngine.cs` |

---

## 四、项目配置

### 测试项目 .csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="Moq" Version="4.*" />
    <PackageReference Include="FluentAssertions" Version="7.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\MagnetometerSystem.Core\MagnetometerSystem.Core.csproj" />
  </ItemGroup>

</Project>
```

> 注意：`ProjectReference` 路径需根据实际项目结构调整。

---

## 五、测试清单

### 5.1 FormulaEvaluatorTests

被测类：`Core/Helpers/FormulaEvaluator.cs`（Shunting-yard 公式解析引擎）

| 测试方法 | 说明 |
|---------|------|
| `Evaluate_Addition_CorrectResult` | `"2 + 3"` => 5.0 |
| `Evaluate_Subtraction_CorrectResult` | `"10 - 4"` => 6.0 |
| `Evaluate_Multiplication_CorrectResult` | `"3 * 7"` => 21.0 |
| `Evaluate_Division_CorrectResult` | `"20 / 4"` => 5.0 |
| `Evaluate_OperatorPrecedence_CorrectResult` | `"2 + 3 * 4"` => 14.0 |
| `Evaluate_Parentheses_CorrectResult` | `"(2 + 3) * 4"` => 20.0 |
| `Evaluate_NestedParentheses_CorrectResult` | `"((1 + 2) * (3 + 4))"` => 21.0 |
| `Evaluate_SqrtFunction_CorrectResult` | `"sqrt(16)"` => 4.0 |
| `Evaluate_AbsFunction_NegativeInput` | `"abs(-5)"` => 5.0 |
| `Evaluate_SinFunction_CorrectResult` | `"sin(0)"` => 0.0 |
| `Evaluate_CosFunction_CorrectResult` | `"cos(0)"` => 1.0 |
| `Evaluate_ChannelVariable_Substituted` | 设置 CH1=100，`"CH1 * 2"` => 200.0 |
| `Evaluate_MultipleVariables_CorrectResult` | CH1=10, CH2=20，`"CH1 + CH2"` => 30.0 |
| `Evaluate_DivisionByZero_ReturnsNaNOrInfinity` | `"1 / 0"` 不抛异常 |
| `Evaluate_EmptyExpression_ThrowsOrReturnsDefault` | 空字符串的行为 |
| `Evaluate_InvalidExpression_ThrowsException` | `"2 + + 3"` 应抛异常 |
| `Evaluate_UnknownFunction_ThrowsException` | `"foo(1)"` 应抛异常 |

### 5.2 CircularBufferTests

被测类：`Core/Helpers/CircularBuffer.cs`（容量 100,000 的环形缓冲区）

| 测试方法 | 说明 |
|---------|------|
| `Add_SingleItem_CountIsOne` | 添加一个元素后 Count=1 |
| `Add_MultipleItems_CorrectCount` | 添加 N 个元素后 Count=N |
| `Add_ExceedsCapacity_CountEqualsCapacity` | 超过容量后 Count 不超过 Capacity |
| `Add_ExceedsCapacity_OldestOverwritten` | 超容量后最旧数据被覆盖 |
| `ToArray_ReturnsItemsInOrder` | ToArray 返回先进先出的顺序 |
| `ToArray_AfterOverflow_CorrectOrder` | 溢出后 ToArray 仍保持正确顺序 |
| `Clear_ResetsCountToZero` | Clear 后 Count=0 |
| `Clear_ToArrayReturnsEmpty` | Clear 后 ToArray 返回空数组 |
| `Indexer_ValidIndex_ReturnsCorrectItem` | 索引访问返回正确元素 |
| `Indexer_OutOfRange_ThrowsException` | 越界访问抛出异常 |

### 5.3 LttbDownsamplerTests

被测类：`Core/Helpers/LttbDownsampler.cs`（LTTB 降采样算法）

| 测试方法 | 说明 |
|---------|------|
| `Downsample_SineWave_PreservesShape` | 正弦波降采样后峰值和谷值被保留 |
| `Downsample_TargetEqualsSource_ReturnsCopy` | 目标点数=源点数时返回原始数据副本 |
| `Downsample_TargetGreaterThanSource_ReturnsCopy` | 目标点数>源点数时返回原始数据 |
| `Downsample_TargetIsTwo_ReturnsFirstAndLast` | 目标=2 时返回首尾两点 |
| `Downsample_EmptyInput_ReturnsEmpty` | 空输入返回空数组 |
| `Downsample_SinglePoint_ReturnsSinglePoint` | 单点输入返回单点 |
| `Downsample_ConstantData_ReducesCorrectly` | 常数数据降采样后值不变 |
| `Downsample_OutputLength_EqualsTarget` | 输出长度精确等于目标点数 |

### 5.4 AsciiLineParserTests

被测类：`Core/Protocol/AsciiLineParser.cs`（ASCII 文本协议解析）

| 测试方法 | 说明 |
|---------|------|
| `Parse_ValidLine_ReturnsValues` | 正常格式行返回正确的解析值 |
| `Parse_MultipleValues_AllParsed` | 多值行全部正确解析 |
| `Parse_WithWhitespace_Trimmed` | 含前后空格的行正确处理 |
| `Parse_PartialLine_WaitsForComplete` | 不完整行（无换行符）等待后续数据 |
| `Parse_TwoLinesInOneChunk_BothParsed` | 一次输入含两行，两行都被解析 |
| `Parse_EmptyLine_Skipped` | 空行被跳过 |
| `Parse_CorruptedData_HandledGracefully` | 非数字数据不抛异常，被跳过或报告错误 |
| `Parse_DifferentDelimiters_Configurable` | 支持逗号/空格/制表符分隔 |
| `Parse_NegativeValues_Correct` | 负数值正确解析 |
| `Parse_ScientificNotation_Correct` | 科学计数法（如 1.5e-3）正确解析 |

### 5.5 ConfigurableBinaryParserTests

被测类：`Core/Protocol/ConfigurableBinaryParser.cs`（可配置二进制协议解析）

| 测试方法 | 说明 |
|---------|------|
| `Parse_CompleteFrame_ReturnsData` | 完整帧正确解析 |
| `Parse_PartialFrame_WaitsForMore` | 不完整帧等待更多数据 |
| `Parse_TwoFramesInOneChunk_BothParsed` | 一次输入含两帧，两帧都解析 |
| `Parse_InvalidChecksum_FrameRejected` | 校验和错误的帧被丢弃 |
| `Parse_ValidChecksum_FrameAccepted` | 校验和正确的帧被接受 |
| `Parse_FrameWithHeader_HeaderMatched` | 帧头匹配正确 |
| `Parse_GarbageBeforeFrame_Skipped` | 帧头前的垃圾数据被跳过 |
| `Parse_MultipleFieldTypes_CorrectExtraction` | 不同字段类型（int16/int32/float）正确提取 |
| `Parse_BigEndian_CorrectByteOrder` | 大端字节序正确处理 |
| `Parse_LittleEndian_CorrectByteOrder` | 小端字节序正确处理 |

### 5.6 OrthogonalityCalculatorTests

被测类：`Core/Calibration/OrthogonalityCalculator.cs`（椭球拟合正交度校正）

| 测试方法 | 说明 |
|---------|------|
| `Calibrate_SyntheticSphere_ReturnsIdentityTransform` | 理想球面数据，校正矩阵接近单位矩阵 |
| `Calibrate_SyntheticEllipsoid_CorrectTransform` | 已知椭球参数，校正后残差小于阈值 |
| `Calibrate_WithOffset_CorrectCenter` | 含偏移的数据，正确估计中心点 |
| `Calibrate_KnownResult_Verification` | 与 MathNet 或文献结果交叉验证 |
| `Calibrate_AppliedToData_ResidualBelowThreshold` | 校正后的数据模长方差 < 预设阈值 |
| `Calibrate_InsufficientData_ThrowsOrReturnsError` | 数据点不足时的行为 |
| `Calibrate_NoisyData_StillConverges` | 含高斯噪声的椭球数据仍可收敛 |

### 5.7 DataProcessorTests

被测类：`Core/Processing/DataProcessor.cs`（滤波算法）

参见 [TASK-E2 单元测试要求](14-TASK-E2-数字滤波.md#七单元测试要求) 中的测试清单。

| 测试方法 | 说明 |
|---------|------|
| `MovingAverage_ConstantData_ReturnsSameValues` | 常数不变 |
| `MovingAverage_KnownSequence_CorrectResult` | 已知序列验证 |
| `MovingAverage_WindowSize1_ReturnsCopy` | 窗口=1 等于原始 |
| `MovingAverage_EmptyData_ReturnsEmpty` | 空输入 |
| `MovingAverage_BoundaryHandling_NoException` | 边界处理 |
| `MedianFilter_SinglePulse_Removed` | 脉冲消除 |
| `MedianFilter_KnownSequence_CorrectResult` | 已知序列验证 |
| `MedianFilter_EvenWindowSize_AdjustedToOdd` | 偶数窗口自动调整 |
| `MedianFilter_EmptyData_ReturnsEmpty` | 空输入 |
| `MedianFilter_WindowLargerThanData_NoException` | 超长窗口 |

### 5.8 RollingStatisticsEngineTests

被测类：`Core/Processing/RollingStatisticsEngine.cs`（滚动统计引擎）

参见 [TASK-E1 单元测试要求](13-TASK-E1-实时统计引擎.md#七单元测试要求) 中的测试清单。

| 测试方法 | 说明 |
|---------|------|
| `ComputeAll_NoData_ReturnsEmpty` | 无数据返回空 |
| `ComputeAll_SingleChannel_CorrectMean` | 单通道均值 |
| `ComputeAll_MultiChannel_IndependentResults` | 多通道独立 |
| `ComputeAll_WindowTrimming_OnlyRecentData` | 窗口裁剪 |
| `Configure_UpdatesWindowSize` | 动态更新窗口 |
| `Clear_ResetsAllBuffers` | 清空重置 |
| `AddSample_ExceedsCapacity_NoException` | 超容量不崩 |
| `ConcurrentAccess_NoException` | 并发安全 |

---

## 六、实现指南

### 6.1 测试命名规范

采用 `MethodName_Scenario_ExpectedBehavior` 三段式命名。

### 6.2 Arrange-Act-Assert 模式

每个测试方法严格分为三个区域：

```csharp
[Fact]
public void MethodName_Scenario_ExpectedBehavior()
{
    // Arrange
    var sut = new TargetClass();
    var input = ...;

    // Act
    var result = sut.Method(input);

    // Assert
    result.Should().Be(expectedValue);
}
```

### 6.3 测试数据准备

- **FormulaEvaluator**：使用 `[Theory]` + `[InlineData]` 提供多组输入输出。
- **CircularBuffer**：使用简单整数序列。
- **LttbDownsampler**：生成正弦波 `Math.Sin(2 * Math.PI * i / N)`。
- **Protocol 解析器**：手工构造字节数组，包含帧头、数据、校验和。
- **OrthogonalityCalculator**：用 MathNet 生成已知椭球参数的合成数据。
- **DataProcessor**：使用短数组 `[1, 2, 3, 4, 5]` 手工验算。

### 6.4 Mock 使用

- 协议解析器测试中，如需模拟数据源，使用 Moq 创建 Mock 对象。
- 统计引擎测试中，`StatisticsResultItem.Compute` 为静态方法，无需 Mock。

### 6.5 运行测试

```bash
dotnet test tests/MagnetometerSystem.Core.Tests/
```

### 6.6 覆盖率检测

```bash
dotnet test --collect:"XPlat Code Coverage"
# 或使用 coverlet
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=lcov
```

---

## 七、验收标准

1. 测试项目编译通过，`dotnet test` 全部通过。
2. 总测试数量 >= 70 个。
3. Core/Helpers 目录覆盖率 >= 80%。
4. Core/Protocol 目录覆盖率 >= 80%。
5. Core/Processing 目录覆盖率 >= 80%。
6. Core/Calibration 目录覆盖率 >= 80%（取决于 OrthogonalityCalculator 复杂度）。
7. 所有测试遵循 Arrange-Act-Assert 模式，命名符合三段式规范。
8. 无测试依赖外部资源（网络、文件系统、数据库），全部为纯单元测试（OrthogonalityCalculator 可使用内存计算）。
9. CI 可执行：`dotnet test` 无需人工干预即可完成。
