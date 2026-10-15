# TASK C-3：正交度校正向导界面

**文档版本**: v1.0
**编写日期**: 2026-03-21
**优先级**: P0
**所属阶段**: Phase 4
**任务流**: Stream C（正交度校正）
**前置依赖**: C-1（正交度计算引擎）、C-2（正交度校正应用）、D-1（SQLite 数据库服务）

---

## 一、基本信息

### 1.1 任务概述

实现步骤引导式的正交度校正向导界面（Wizard），指导用户完成从传感器选择、数据采集、计算执行、结果查看到配置保存的正交度校正全流程。该界面是用户与正交度校正模块交互的主入口。

### 1.2 设计原则

- **步骤引导**：5 步向导，用户无需理解算法细节即可完成校正
- **实时反馈**：数据采集阶段实时显示样本数和覆盖度
- **结果透明**：校正结果以数值和可视化形式完整呈现
- **容错友好**：每步都有明确的状态反馈和错误提示

### 1.3 涉及的现有代码

| 文件 | 作用 | 关系 |
|------|------|------|
| `Core/Services/DataBus.cs` | 事件总线 | 订阅 `ReadingReceived` 采集校正数据 |
| `Core/Calibration/IOrthogonalityService.cs` | 正交度计算接口 | 调用 `Calculate()` 执行校正计算 |
| `Core/Calibration/OrthogonalityCorrector.cs` | 校正应用器 | 调用 `ApplyToReading()` 预览校正效果 |
| `Core/Models/OrthogonalityParams.cs` | 参数模型 | 保存计算结果 |
| `App/Views/MainWindow.xaml` | 主窗口 | 导航到校正向导页面 |

---

## 二、功能需求

### 2.1 向导步骤总览

| 步骤 | 名称 | 功能 |
|------|------|------|
| Step 1 | 选择传感器 | 选择传感器类型，输入序列号 |
| Step 2 | 采集数据 | 订阅 DataBus，实时采集旋转数据 |
| Step 3 | 执行计算 | 调用 IOrthogonalityService.Calculate() |
| Step 4 | 查看结果 | 显示补偿矩阵、偏移、残差、可视化（C-4） |
| Step 5 | 保存配置 | 命名并保存到数据库 |

### 2.2 Step 1 — 选择传感器

#### 2.2.1 UI 元素

| 控件 | 类型 | 绑定属性 | 说明 |
|------|------|----------|------|
| 传感器类型 | ComboBox | `SelectedSensorType` | 仅显示 `TriaxialFluxgate` 和 `DualTriaxialFluxgate` |
| 传感器序列号 | TextBox | `SensorSerial` | 可选输入，用于关联配置 |
| 参考场强 | TextBox | `ReferenceFieldStrength` | 可选，单位 nT，为空则自动估算 |
| 下一步 | Button | `NextStepCommand` | 传感器类型已选择时启用 |

#### 2.2.2 逻辑

- 传感器类型列表仅包含支持正交度校正的类型（三轴、双三轴）
- 如果当前已连接设备，自动填充传感器类型
- 参考场强输入框带提示文字："留空则自动估算（当地地磁场约 50000 nT）"
- 双三轴传感器时提示："将分别对两组三轴进行独立校正"

### 2.3 Step 2 — 采集数据

#### 2.3.1 UI 元素

| 控件 | 类型 | 绑定属性 | 说明 |
|------|------|----------|------|
| 开始采集 | Button | `StartCollectionCommand` | 启用条件：设备已连接 |
| 停止采集 | Button | `StopCollectionCommand` | 采集中可用 |
| 样本计数 | TextBlock | `CollectedSampleCount` | 实时更新 |
| 最少样本提示 | TextBlock | — | 固定文字 "至少需要 100 个样本" |
| 球面覆盖度 | ProgressBar | `SphericityCoverage` | 0-100% 指示 |
| 覆盖度数值 | TextBlock | `SphericityCoverage` | 百分比文字 |
| 采集状态 | TextBlock | `CollectionStatus` | "等待开始" / "采集中..." / "采集完成" |
| 实时数据预览 | ScottPlot (可选) | — | XY 散点图预览已采集数据的分布 |
| 下一步 | Button | `NextStepCommand` | 样本数 >= 100 时启用 |

#### 2.3.2 采集逻辑

```csharp
private readonly List<double[]> _collectedData = new();
private IDisposable? _subscription;

[RelayCommand]
private void StartCollection()
{
    _collectedData.Clear();
    CollectedSampleCount = 0;
    CollectionStatus = "采集中...";
    IsCollecting = true;

    // 订阅 DataBus
    _dataBus.ReadingReceived += OnCalibrationDataReceived;
}

private void OnCalibrationDataReceived(object? sender, MagnetometerReading reading)
{
    // 仅采集三轴数据
    if (reading.ChannelValues.Length < 3) return;

    _collectedData.Add([reading.ChannelValues[0],
                         reading.ChannelValues[1],
                         reading.ChannelValues[2]]);

    // 在 UI 线程更新
    App.Current.Dispatcher.Invoke(() =>
    {
        CollectedSampleCount = _collectedData.Count;
        // 每 50 个点更新一次覆盖度（避免频繁计算）
        if (_collectedData.Count % 50 == 0)
            UpdateCoverageEstimate();
    });
}

[RelayCommand]
private void StopCollection()
{
    _dataBus.ReadingReceived -= OnCalibrationDataReceived;
    IsCollecting = false;
    CollectionStatus = $"采集完成，共 {CollectedSampleCount} 个样本";
}
```

#### 2.3.3 双三轴处理

- 双三轴传感器时，同时采集两组数据（通道 0-2 和通道 3-5）
- 向导内部维护两个独立的数据列表：`_collectedDataGroup1` 和 `_collectedDataGroup2`
- UI 上显示两组的样本数和覆盖度

### 2.4 Step 3 — 执行计算

#### 2.4.1 UI 元素

| 控件 | 类型 | 绑定属性 | 说明 |
|------|------|----------|------|
| 执行计算 | Button | `ExecuteCalculationCommand` | 进入此步时可用 |
| 进度指示 | ProgressBar | `IsCalculating` (IsIndeterminate) | 不确定模式进度条 |
| 计算状态 | TextBlock | `CalculationStatus` | "等待执行" / "计算中..." / "计算完成" / 错误信息 |
| 下一步 | Button | `NextStepCommand` | 计算成功后启用 |

#### 2.4.2 计算逻辑

```csharp
[RelayCommand]
private async Task ExecuteCalculation()
{
    IsCalculating = true;
    CalculationStatus = "计算中...";

    try
    {
        // 将 List<double[]> 转为 double[,]
        var rawData = ConvertToMatrix(_collectedData);

        // 在后台线程执行计算
        var result = await Task.Run(() =>
            _orthogonalityService.Calculate(rawData, ReferenceFieldStrength));

        if (result.Success)
        {
            CalculationResult = result;
            CalculationStatus = "计算完成";
        }
        else
        {
            CalculationStatus = $"计算失败: {result.ErrorMessage}";
        }
    }
    catch (Exception ex)
    {
        CalculationStatus = $"计算异常: {ex.Message}";
    }
    finally
    {
        IsCalculating = false;
    }
}
```

### 2.5 Step 4 — 查看结果

#### 2.5.1 UI 元素

| 控件 | 类型 | 绑定属性 | 说明 |
|------|------|----------|------|
| 补偿矩阵 | 3x3 Grid (TextBlock) | `MatrixDisplayValues` | 3x3 数值网格，保留 6 位小数 |
| 偏移向量 | 3 个 TextBlock | `OffsetX/Y/Z` | 偏移向量三个分量 |
| 残差均值 | TextBlock | `CalculationResult.Quality.ResidualMean` | 单位 nT |
| 残差标准差 | TextBlock | `CalculationResult.Quality.ResidualStd` | 单位 nT |
| 最大残差 | TextBlock | `CalculationResult.Quality.MaxResidual` | 单位 nT |
| 球面覆盖度 | TextBlock | `CalculationResult.Quality.SphericityCoverage` | 百分比 |
| 样本数量 | TextBlock | `CalculationResult.Quality.SampleCount` | 整数 |
| 可视化区域 | UserControl | — | 嵌入 C-4 校正可视化组件 |
| 质量评级 | TextBlock | `QualityRating` | 根据残差给出评级：优秀/良好/一般/较差 |

#### 2.5.2 质量评级逻辑

```csharp
public string QualityRating => CalculationResult?.Quality switch
{
    { ResidualStd: < 10 }   => "优秀",
    { ResidualStd: < 50 }   => "良好",
    { ResidualStd: < 200 }  => "一般",
    _                        => "较差"
};
```

#### 2.5.3 补偿矩阵显示格式

```
┌                          ┐
│  1.000023  -0.002145   0.001532 │
│  0.001876   0.999845  -0.003021 │
│ -0.000945   0.002567   1.000112 │
└                          ┘
```

对角线元素绿色高亮（接近 1.0 时），非对角线元素根据大小黄色或红色提示。

### 2.6 Step 5 — 保存配置

#### 2.6.1 UI 元素

| 控件 | 类型 | 绑定属性 | 说明 |
|------|------|----------|------|
| 配置名称 | TextBox | `ProfileName` | 必填，默认值 "正交度校正_{日期时间}" |
| 备注 | TextBox | `ProfileNotes` | 可选，多行 |
| 设为默认 | CheckBox | `SetAsDefault` | 是否设为当前传感器的默认校正配置 |
| 保存 | Button | `SaveProfileCommand` | 名称非空时启用 |
| 保存状态 | TextBlock | `SaveStatus` | "保存成功" / 错误信息 |

#### 2.6.2 保存逻辑

```csharp
[RelayCommand]
private async Task SaveProfile()
{
    var parameters = CalculationResult!.Parameters!;
    parameters.Name = ProfileName;
    parameters.SensorSerial = SensorSerial;
    parameters.Notes = ProfileNotes;
    parameters.ResidualMean = CalculationResult.Quality!.ResidualMean;
    parameters.ResidualStd = CalculationResult.Quality.ResidualStd;
    parameters.SampleCount = CalculationResult.Quality.SampleCount;

    await _calibrationRepository.SaveOrthogonalityProfileAsync(parameters);
    SaveStatus = "保存成功";
}
```

---

## 三、接口契约

### 3.1 ViewModel 接口

```csharp
// 文件: App/ViewModels/OrthogonalityCalibrationViewModel.cs
namespace MagnetometerSystem.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;

public partial class OrthogonalityCalibrationViewModel : ObservableObject
{
    // === 依赖注入 ===
    private readonly IOrthogonalityService _orthogonalityService;
    private readonly ICalibrationRepository _calibrationRepository; // C-5 提供
    private readonly DataBus _dataBus;

    // === 步骤控制 ===
    [ObservableProperty] private int _currentStep = 1;           // 1~5
    [ObservableProperty] private bool _canGoNext;
    [ObservableProperty] private bool _canGoBack;

    // === Step 1 属性 ===
    [ObservableProperty] private SensorType _selectedSensorType;
    [ObservableProperty] private string _sensorSerial = string.Empty;
    [ObservableProperty] private double? _referenceFieldStrength;

    // === Step 2 属性 ===
    [ObservableProperty] private bool _isCollecting;
    [ObservableProperty] private int _collectedSampleCount;
    [ObservableProperty] private double _sphericityCoverage;
    [ObservableProperty] private string _collectionStatus = "等待开始";

    // === Step 3 属性 ===
    [ObservableProperty] private bool _isCalculating;
    [ObservableProperty] private string _calculationStatus = "等待执行";
    [ObservableProperty] private OrthogonalityResult? _calculationResult;

    // === Step 4 属性 ===
    [ObservableProperty] private string _qualityRating = string.Empty;
    // 矩阵显示值通过计算属性从 CalculationResult 派生

    // === Step 5 属性 ===
    [ObservableProperty] private string _profileName = string.Empty;
    [ObservableProperty] private string _profileNotes = string.Empty;
    [ObservableProperty] private bool _setAsDefault;
    [ObservableProperty] private string _saveStatus = string.Empty;

    // === 命令 ===
    public IRelayCommand NextStepCommand { get; }
    public IRelayCommand PreviousStepCommand { get; }
    public IRelayCommand StartCollectionCommand { get; }
    public IRelayCommand StopCollectionCommand { get; }
    public IAsyncRelayCommand ExecuteCalculationCommand { get; }
    public IAsyncRelayCommand SaveProfileCommand { get; }
}
```

### 3.2 对外部接口的依赖

| 接口 | 来源任务 | 调用方式 |
|------|----------|----------|
| `IOrthogonalityService.Calculate()` | C-1 | Step 3 计算 |
| `ICalibrationRepository.SaveOrthogonalityProfileAsync()` | C-5 | Step 5 保存 |
| `DataBus.ReadingReceived` | 已有 | Step 2 数据采集 |

---

## 四、文件清单

### 4.1 新建文件

| 文件路径 | 说明 |
|----------|------|
| `src/MagnetometerSystem.App/ViewModels/OrthogonalityCalibrationViewModel.cs` | 向导 ViewModel |
| `src/MagnetometerSystem.App/Views/OrthogonalityCalibrationView.xaml` | 向导视图 XAML |
| `src/MagnetometerSystem.App/Views/OrthogonalityCalibrationView.xaml.cs` | 视图代码隐藏（最小化） |

### 4.2 修改文件

| 文件路径 | 修改说明 |
|----------|----------|
| `src/MagnetometerSystem.App/App.xaml.cs` | DI 注册 `OrthogonalityCalibrationViewModel` |
| `src/MagnetometerSystem.App/Views/MainWindow.xaml` | 导航菜单添加 "正交度校正" 入口 |

---

## 五、数据库变更

本任务无数据库变更。保存操作通过 C-5 的 `ICalibrationRepository` 完成。

---

## 六、实现指南

### 6.1 向导步骤切换机制

使用 `CurrentStep` 属性配合 XAML `DataTrigger` 控制各步骤面板的可见性：

```xml
<UserControl x:Class="MagnetometerSystem.App.Views.OrthogonalityCalibrationView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             mc:Ignorable="d">

    <DockPanel>
        <!-- 顶部步骤指示器 -->
        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal"
                    HorizontalAlignment="Center" Margin="0,16">
            <RadioButton Content="1. 选择传感器" IsChecked="{Binding CurrentStep,
                Converter={StaticResource EqualConverter}, ConverterParameter=1}"
                IsEnabled="False" Style="{StaticResource StepIndicatorStyle}"/>
            <RadioButton Content="2. 采集数据" ... />
            <RadioButton Content="3. 执行计算" ... />
            <RadioButton Content="4. 查看结果" ... />
            <RadioButton Content="5. 保存配置" ... />
        </StackPanel>

        <!-- 底部导航按钮 -->
        <StackPanel DockPanel.Dock="Bottom" Orientation="Horizontal"
                    HorizontalAlignment="Right" Margin="16">
            <Button Content="上一步" Command="{Binding PreviousStepCommand}"
                    Margin="0,0,8,0"/>
            <Button Content="下一步" Command="{Binding NextStepCommand}"/>
        </StackPanel>

        <!-- 步骤内容面板 -->
        <Grid>
            <!-- Step 1 -->
            <Grid>
                <Grid.Style>
                    <Style TargetType="Grid">
                        <Setter Property="Visibility" Value="Collapsed"/>
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding CurrentStep}" Value="1">
                                <Setter Property="Visibility" Value="Visible"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </Grid.Style>
                <!-- Step 1 内容 -->
            </Grid>

            <!-- Step 2 ~ Step 5 同理 -->
        </Grid>
    </DockPanel>
</UserControl>
```

### 6.2 步骤状态管理

```csharp
partial void OnCurrentStepChanged(int value)
{
    CanGoBack = value > 1;
    CanGoNext = value switch
    {
        1 => SelectedSensorType == SensorType.TriaxialFluxgate
             || SelectedSensorType == SensorType.DualTriaxialFluxgate,
        2 => CollectedSampleCount >= 100 && !IsCollecting,
        3 => CalculationResult?.Success == true,
        4 => true,
        5 => false, // 最后一步无下一步
        _ => false
    };
}
```

### 6.3 数据采集与离开步骤的处理

- 从 Step 2 返回 Step 1 时，已采集数据保留（不清空），提示用户数据将保留
- 从 Step 2 进入 Step 3 前，自动停止采集（如仍在进行中）
- 从 Step 3 返回 Step 2 时，允许追加采集更多数据
- 从 Step 4 返回 Step 3 时，可重新执行计算

### 6.4 内存管理

- 采集的原始数据存储在 `List<double[]>` 中，典型场景（数千个点）内存开销 < 1MB
- 向导关闭时释放数据引用
- 取消订阅 DataBus 事件以防止内存泄漏

---

## 七、验收标准

### 7.1 功能验收

| 编号 | 验收项 | 通过条件 |
|------|--------|----------|
| AC-1 | 向导全流程 | 从 Step 1 到 Step 5 完整走通，无异常中断 |
| AC-2 | 传感器选择 | 仅显示三轴和双三轴选项 |
| AC-3 | 数据采集 | 连接设备后能实时采集并显示样本计数 |
| AC-4 | 覆盖度指示 | 球面覆盖度实时更新，数值 0-100% |
| AC-5 | 最小样本检查 | 样本 < 100 时 "下一步" 按钮禁用 |
| AC-6 | 计算执行 | 点击计算后异步执行，UI 不阻塞 |
| AC-7 | 计算失败处理 | 计算失败时显示错误信息，不崩溃 |
| AC-8 | 结果展示 | 补偿矩阵、偏移向量、残差统计完整显示 |
| AC-9 | 质量评级 | 根据残差标准差正确给出评级 |
| AC-10 | 配置保存 | 保存后可在数据库中查询到该配置 |
| AC-11 | 步骤导航 | 前进/后退按钮正确启用/禁用 |
| AC-12 | 双三轴支持 | 双三轴传感器时分别显示两组数据的采集状态 |

### 7.2 非功能验收

| 编号 | 验收项 | 通过条件 |
|------|--------|----------|
| NF-1 | UI 响应性 | 数据采集和计算过程中 UI 保持响应 |
| NF-2 | 内存清理 | 向导关闭后采集数据正确释放 |
| NF-3 | 事件清理 | 向导关闭后 DataBus 事件订阅已取消 |

---

## 八、单元测试要求

测试文件：`tests/MagnetometerSystem.Core.Tests/ViewModels/OrthogonalityCalibrationViewModelTests.cs`

### 8.1 测试用例清单

| 编号 | 测试名称 | 测试内容 |
|------|----------|----------|
| T-01 | `InitialState_StepIs1` | 初始 CurrentStep == 1 |
| T-02 | `Step1_NoSensorSelected_CannotGoNext` | 未选择传感器时 CanGoNext == false |
| T-03 | `Step1_SensorSelected_CanGoNext` | 选择三轴传感器后 CanGoNext == true |
| T-04 | `Step2_LessThan100Samples_CannotGoNext` | 样本不足时无法进入下一步 |
| T-05 | `Step2_100PlusSamples_CanGoNext` | 样本充足时可以进入下一步 |
| T-06 | `Step2_StartCollection_SubscribesToDataBus` | 开始采集后订阅了 DataBus 事件 |
| T-07 | `Step2_StopCollection_UnsubscribesFromDataBus` | 停止采集后取消了 DataBus 事件订阅 |
| T-08 | `Step3_CalculationSuccess_CanGoNext` | 计算成功后可进入下一步 |
| T-09 | `Step3_CalculationFail_ShowsError` | 计算失败时显示错误信息 |
| T-10 | `Step5_SaveProfile_CallsRepository` | 保存调用了 `ICalibrationRepository.SaveOrthogonalityProfileAsync` |
| T-11 | `Step5_ProfileNameEmpty_CannotSave` | 名称为空时保存按钮禁用 |
| T-12 | `NavigateBack_PreservesData` | 后退时已采集数据未丢失 |

**注意**：ViewModel 测试需 Mock `IOrthogonalityService`、`ICalibrationRepository` 和 `DataBus`。使用 Moq 框架。
