# TASK C-2：正交度校正应用

**文档版本**: v1.0
**编写日期**: 2026-03-21
**优先级**: P0
**所属阶段**: Phase 4
**任务流**: Stream C（正交度校正）
**前置依赖**: C-1（正交度计算引擎）

---

## 一、基本信息

### 1.1 任务概述

实现正交度校正的应用层，将 C-1 计算得到的补偿矩阵和偏移向量应用于实时数据流和历史批量数据。本任务负责将校正逻辑嵌入数据处理管线，提供实时校正和批量回溯校正两种工作模式，并在 UI 层提供校正开关和配置选择。

### 1.2 核心职责

| 职责 | 说明 |
|------|------|
| 实时校正 | 在数据采集管线中插入校正环节，对每条 `MagnetometerReading` 实时校正 |
| 批量校正 | 从数据库加载历史会话数据，批量应用校正后保存回数据库 |
| 校正开关 | UI 侧控制是否启用实时正交度校正 |
| 多配置切换 | 支持在多个正交度校正配置之间切换 |
| 双三轴支持 | `DualTriaxialFluxgate` 传感器的两组三轴各自独立校正 |

### 1.3 涉及的现有代码

| 文件 | 作用 | 修改内容 |
|------|------|----------|
| `Core/Models/OrthogonalityParams.cs` | 正交度参数模型 | 不修改，调用其 `Apply(x,y,z)` |
| `Core/Models/MagnetometerReading.cs` | 数据读数 record | 不修改，利用 `with` 表达式创建校正后副本 |
| `Core/Services/DataBus.cs` | 事件总线 | 不修改，校正在发布之前完成 |
| `App/ViewModels/RealtimeChartViewModel.cs` | 实时图表 ViewModel | 新增校正开关属性和活动配置属性 |
| `App/Views/RealtimeChartView.xaml` | 实时图表视图 | 侧边栏新增校正开关 UI |

---

## 二、功能需求

### 2.1 实时校正模式

#### 2.1.1 数据流插入点

校正环节插入在 `ConnectionViewModel.OnDataReceived` 中，位于传感器适配器输出之后、DataBus 发布之前：

```
设备原始字节 → 协议解析 → 传感器适配器 → [校准校正(CalibrationParams)] → [正交度校正] → DataBus.PublishReading()
```

#### 2.1.2 校正逻辑

```csharp
// 在 ConnectionViewModel 或独立的校正管道中
private MagnetometerReading ApplyOrthogonalityCorrection(MagnetometerReading reading)
{
    if (!IsOrthogonalityCorrectionEnabled || ActiveOrthogonalityProfile == null)
        return reading;

    // 仅对三轴和双三轴传感器执行校正
    if (reading.SensorType != SensorType.TriaxialFluxgate &&
        reading.SensorType != SensorType.DualTriaxialFluxgate)
        return reading;

    var correctedValues = (double[])reading.ChannelValues.Clone();

    // 第一组三轴 (通道 0, 1, 2)
    var corrected1 = ActiveOrthogonalityProfile.Apply(
        correctedValues[0], correctedValues[1], correctedValues[2]);
    correctedValues[0] = corrected1[0];
    correctedValues[1] = corrected1[1];
    correctedValues[2] = corrected1[2];

    // 双三轴: 第二组 (通道 3, 4, 5)
    if (reading.SensorType == SensorType.DualTriaxialFluxgate
        && correctedValues.Length >= 6
        && SecondOrthogonalityProfile != null)
    {
        var corrected2 = SecondOrthogonalityProfile.Apply(
            correctedValues[3], correctedValues[4], correctedValues[5]);
        correctedValues[3] = corrected2[0];
        correctedValues[4] = corrected2[1];
        correctedValues[5] = corrected2[2];
    }

    // 重新计算总场
    double totalField = Math.Sqrt(
        correctedValues[0] * correctedValues[0] +
        correctedValues[1] * correctedValues[1] +
        correctedValues[2] * correctedValues[2]);

    // 创建新的不可变实例
    return reading with
    {
        ChannelValues = correctedValues,
        TotalField = totalField,
        IsOrthogonalityCorrected = true
    };
}
```

#### 2.1.3 不可变模式

`MagnetometerReading` 是 `record` 类型，**必须**使用 `with` 表达式创建新实例，禁止直接修改原始对象的属性。这确保了：
- 下游消费者看到一致的数据
- DataBus 的其他订阅者不会看到部分修改的数据
- 线程安全

### 2.2 批量校正模式

#### 2.2.1 功能描述

对已保存的历史会话数据进行回溯校正：

1. 从 `IDataStorageService.GetReadingsAsync(sessionId)` 加载全部读数
2. 逐条应用正交度校正
3. 通过 `IDataStorageService.SaveReadingsAsync()` 批量保存校正后数据（新会话或覆盖原会话）

#### 2.2.2 批量处理接口

```csharp
// OrthogonalityCorrector.cs 中的批量处理方法
public async Task<BatchCorrectionResult> ApplyBatchAsync(
    OrthogonalityParams parameters,
    IReadOnlyList<MagnetometerReading> readings,
    IProgress<int>? progress = null,
    CancellationToken cancellationToken = default)
{
    var corrected = new List<MagnetometerReading>(readings.Count);
    for (int i = 0; i < readings.Count; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        corrected.Add(ApplyToReading(parameters, readings[i]));
        progress?.Report((i + 1) * 100 / readings.Count);
    }
    return new BatchCorrectionResult
    {
        CorrectedReadings = corrected,
        ProcessedCount = corrected.Count
    };
}
```

### 2.3 UI 交互

#### 2.3.1 RealtimeChartView 侧边栏新增控件

在 `RealtimeChartView.xaml` 的侧边栏中，在现有控件之后添加正交度校正区域：

```xml
<!-- 正交度校正区域 -->
<GroupBox Header="正交度校正" Margin="0,8,0,0">
    <StackPanel>
        <CheckBox Content="启用正交度校正"
                  IsChecked="{Binding IsOrthogonalityCorrectionEnabled}"
                  Margin="0,4"/>
        <TextBlock Text="当前配置:" Margin="0,4,0,2"/>
        <ComboBox ItemsSource="{Binding AvailableOrthogonalityProfiles}"
                  SelectedItem="{Binding ActiveOrthogonalityProfile}"
                  DisplayMemberPath="Name"
                  IsEnabled="{Binding IsOrthogonalityCorrectionEnabled}"
                  Margin="0,2"/>
        <!-- 双三轴第二组配置 -->
        <TextBlock Text="第二组配置 (双三轴):" Margin="0,4,0,2"
                   Visibility="{Binding IsDualTriaxial, Converter={StaticResource BoolToVisibility}}"/>
        <ComboBox ItemsSource="{Binding AvailableOrthogonalityProfiles}"
                  SelectedItem="{Binding SecondOrthogonalityProfile}"
                  DisplayMemberPath="Name"
                  IsEnabled="{Binding IsOrthogonalityCorrectionEnabled}"
                  Visibility="{Binding IsDualTriaxial, Converter={StaticResource BoolToVisibility}}"
                  Margin="0,2"/>
    </StackPanel>
</GroupBox>
```

#### 2.3.2 RealtimeChartViewModel 新增属性

```csharp
// 使用 CommunityToolkit.Mvvm 源码生成器
[ObservableProperty]
private bool _isOrthogonalityCorrectionEnabled;

[ObservableProperty]
private OrthogonalityParams? _activeOrthogonalityProfile;

[ObservableProperty]
private OrthogonalityParams? _secondOrthogonalityProfile;

[ObservableProperty]
private ObservableCollection<OrthogonalityParams> _availableOrthogonalityProfiles = new();

public bool IsDualTriaxial => CurrentSensorType == SensorType.DualTriaxialFluxgate;
```

---

## 三、接口契约

### 3.1 新增类 — `OrthogonalityCorrector`

```csharp
// 文件: Core/Calibration/OrthogonalityCorrector.cs
namespace MagnetometerSystem.Core.Calibration;

using MagnetometerSystem.Core.Models;

/// <summary>
/// 正交度校正应用器
/// 负责将补偿参数应用于单条或批量磁力读数
/// </summary>
public class OrthogonalityCorrector
{
    /// <summary>
    /// 对单条读数应用正交度校正
    /// </summary>
    /// <param name="parameters">正交度参数</param>
    /// <param name="reading">原始读数</param>
    /// <returns>校正后的新读数实例</returns>
    public MagnetometerReading ApplyToReading(
        OrthogonalityParams parameters, MagnetometerReading reading);

    /// <summary>
    /// 对单条读数应用正交度校正（双三轴，两组独立参数）
    /// </summary>
    public MagnetometerReading ApplyToReading(
        OrthogonalityParams firstGroup, OrthogonalityParams? secondGroup,
        MagnetometerReading reading);

    /// <summary>
    /// 批量校正
    /// </summary>
    public Task<BatchCorrectionResult> ApplyBatchAsync(
        OrthogonalityParams parameters,
        IReadOnlyList<MagnetometerReading> readings,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 批量校正结果
/// </summary>
public class BatchCorrectionResult
{
    public IReadOnlyList<MagnetometerReading> CorrectedReadings { get; set; } = [];
    public int ProcessedCount { get; set; }
}
```

### 3.2 对现有类的修改

#### 3.2.1 `ConnectionViewModel` (或数据管线控制器)

需在数据处理管线中插入校正调用。具体修改点取决于现有 `OnDataReceived` 的实现方式。核心修改为：

```csharp
// 伪代码 — 在 reading 生成后、发布前
var reading = _sensorAdapter.Process(rawBytes);

// === 插入校正环节 ===
if (_orthogonalityCorrector != null && IsOrthogonalityCorrectionEnabled)
{
    reading = _orthogonalityCorrector.ApplyToReading(
        ActiveOrthogonalityProfile, SecondOrthogonalityProfile, reading);
}

_dataBus.PublishReading(reading);
```

#### 3.2.2 `RealtimeChartViewModel`

新增属性见 2.3.2 节。需注入 `ICalibrationRepository`（来自 C-5）以加载可用配置列表。在 C-5 未完成前，可暂时硬编码空列表或从内存管理。

---

## 四、文件清单

### 4.1 新建文件

| 文件路径 | 说明 |
|----------|------|
| `src/MagnetometerSystem.Core/Calibration/OrthogonalityCorrector.cs` | 校正应用器，含单条和批量校正方法 |

### 4.2 修改文件

| 文件路径 | 修改说明 |
|----------|----------|
| `src/MagnetometerSystem.App/ViewModels/RealtimeChartViewModel.cs` | 新增校正开关属性、活动配置属性、配置列表属性 |
| `src/MagnetometerSystem.App/Views/RealtimeChartView.xaml` | 侧边栏新增校正开关 UI 区域 |
| `src/MagnetometerSystem.App/ViewModels/ConnectionViewModel.cs` | 在数据处理管线中插入正交度校正调用 |
| `src/MagnetometerSystem.App/App.xaml.cs` | DI 注册 `OrthogonalityCorrector` |

### 4.3 DI 注册

```csharp
services.AddSingleton<OrthogonalityCorrector>();
```

---

## 五、数据库变更

本任务无数据库变更。批量校正结果复用已有的 `IDataStorageService.SaveReadingsAsync()` 存储。

---

## 六、实现指南

### 6.1 `OrthogonalityCorrector` 类结构

```csharp
namespace MagnetometerSystem.Core.Calibration;

using MagnetometerSystem.Core.Models;

public class OrthogonalityCorrector
{
    public MagnetometerReading ApplyToReading(
        OrthogonalityParams parameters, MagnetometerReading reading)
    {
        return ApplyToReading(parameters, null, reading);
    }

    public MagnetometerReading ApplyToReading(
        OrthogonalityParams firstGroup, OrthogonalityParams? secondGroup,
        MagnetometerReading reading)
    {
        // 仅对三轴和双三轴传感器有效
        if (reading.SensorType != SensorType.TriaxialFluxgate &&
            reading.SensorType != SensorType.DualTriaxialFluxgate)
            return reading;

        if (reading.ChannelValues.Length < 3)
            return reading;

        var values = (double[])reading.ChannelValues.Clone();

        // 第一组三轴
        var c1 = firstGroup.Apply(values[0], values[1], values[2]);
        values[0] = c1[0];
        values[1] = c1[1];
        values[2] = c1[2];

        // 双三轴第二组
        if (reading.SensorType == SensorType.DualTriaxialFluxgate
            && values.Length >= 6 && secondGroup != null)
        {
            var c2 = secondGroup.Apply(values[3], values[4], values[5]);
            values[3] = c2[0];
            values[4] = c2[1];
            values[5] = c2[2];
        }

        double totalField = Math.Sqrt(values[0] * values[0]
            + values[1] * values[1] + values[2] * values[2]);

        return reading with
        {
            ChannelValues = values,
            TotalField = totalField,
            IsOrthogonalityCorrected = true
        };
    }

    public async Task<BatchCorrectionResult> ApplyBatchAsync(
        OrthogonalityParams parameters,
        IReadOnlyList<MagnetometerReading> readings,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<MagnetometerReading>(readings.Count);
            for (int i = 0; i < readings.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(ApplyToReading(parameters, readings[i]));
                progress?.Report((i + 1) * 100 / readings.Count);
            }
            return new BatchCorrectionResult
            {
                CorrectedReadings = results,
                ProcessedCount = results.Count
            };
        }, cancellationToken);
    }
}
```

### 6.2 实时校正集成要点

1. `ConnectionViewModel` 需持有 `OrthogonalityCorrector` 实例（通过 DI 注入）
2. 校正开关状态 `IsOrthogonalityCorrectionEnabled` 通过 `RealtimeChartViewModel` 暴露，二者通过 DataBus 或共享设置同步
3. 切换校正配置时无需断开连接，下一条数据即使用新配置
4. 校正操作为纯计算（矩阵乘法），延迟可忽略（< 1 微秒），不影响高采样率场景

### 6.3 线程安全

- `OrthogonalityCorrector` 无状态，线程安全
- `OrthogonalityParams` 的 `Apply` 方法为纯函数，线程安全
- `ActiveOrthogonalityProfile` 引用替换为原子操作，不需要锁
- 切换配置时使用 `Interlocked.Exchange` 或在 UI 线程操作

### 6.4 双三轴处理

| 传感器类型 | 通道分组 | 校正方式 |
|-----------|---------|---------|
| `TriaxialFluxgate` | [0,1,2] | 一组参数校正 |
| `DualTriaxialFluxgate` | [0,1,2] + [3,4,5] | 两组参数独立校正 |
| `SingleAxisFluxgate` | [0] | 不执行正交度校正 |
| `ProtonMagnetometer` | [0] | 不执行正交度校正 |

---

## 七、验收标准

### 7.1 功能验收

| 编号 | 验收项 | 通过条件 |
|------|--------|----------|
| AC-1 | 实时校正 | 启用校正后，DataBus 发布的 `MagnetometerReading` 的 `IsOrthogonalityCorrected == true` |
| AC-2 | 实时校正效果 | 对已知畸变数据，校正后总场标准差明显降低 |
| AC-3 | 校正开关 | 关闭校正开关后，数据不经过校正直接发布 |
| AC-4 | 配置切换 | 切换校正配置后，后续数据使用新配置校正 |
| AC-5 | 批量校正 | 历史会话数据批量校正后全部 `IsOrthogonalityCorrected == true` |
| AC-6 | 进度报告 | 批量校正过程中 `IProgress<int>` 正确报告百分比 |
| AC-7 | 取消支持 | 批量校正可通过 `CancellationToken` 取消 |
| AC-8 | 不可变性 | 校正后返回新 `MagnetometerReading` 实例，原始实例未被修改 |
| AC-9 | 双三轴独立校正 | 两组三轴使用不同参数，各自独立校正 |
| AC-10 | 非三轴传感器 | 单轴磁通门和质子磁力仪数据原样通过 |
| AC-11 | UI 校正开关 | `RealtimeChartView` 侧边栏可见校正开关复选框 |

### 7.2 非功能验收

| 编号 | 验收项 | 通过条件 |
|------|--------|----------|
| NF-1 | 实时延迟 | 单条校正耗时 < 10 微秒，不影响 500Hz 采样 |
| NF-2 | 内存 | 批量校正 100 万条数据，内存增长可控 |
| NF-3 | 线程安全 | 多线程并发调用 `ApplyToReading` 无异常 |

---

## 八、单元测试要求

测试文件：`tests/MagnetometerSystem.Core.Tests/Calibration/OrthogonalityCorrectorTests.cs`

### 8.1 测试用例清单

| 编号 | 测试名称 | 测试内容 |
|------|----------|----------|
| T-01 | `ApplyToReading_TriaxialFluxgate_CorrectsChanValues` | 三轴传感器读数经校正后通道值正确变换 |
| T-02 | `ApplyToReading_DualTriaxial_CorrectsBothGroups` | 双三轴传感器两组分别使用不同参数校正 |
| T-03 | `ApplyToReading_SingleAxis_ReturnsUnchanged` | 单轴传感器读数原样返回 |
| T-04 | `ApplyToReading_ProtonMag_ReturnsUnchanged` | 质子磁力仪读数原样返回 |
| T-05 | `ApplyToReading_SetsIsOrthogonalityCorrected` | 校正后标志位为 true |
| T-06 | `ApplyToReading_RecalculatesTotalField` | 校正后总场根据新通道值重新计算 |
| T-07 | `ApplyToReading_DoesNotMutateOriginal` | 原始 reading 的 ChannelValues 未被修改 |
| T-08 | `ApplyToReading_IdentityMatrix_PreservesValues` | 单位矩阵 + 零偏移时通道值不变 |
| T-09 | `ApplyBatchAsync_ProcessesAllReadings` | 批量校正处理全部读数 |
| T-10 | `ApplyBatchAsync_ReportsProgress` | 进度回调被正确调用 |
| T-11 | `ApplyBatchAsync_SupportsCancellation` | 取消令牌触发后抛出 `OperationCanceledException` |
| T-12 | `ApplyToReading_DualTriaxial_NoSecondProfile_OnlyFirstGroupCorrected` | 双三轴未提供第二组参数时仅校正第一组 |
