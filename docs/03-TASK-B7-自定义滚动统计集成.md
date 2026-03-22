# TASK-B7: 自定义滚动统计集成

## 基本信息

| 属性     | 值                                                        |
| -------- | --------------------------------------------------------- |
| 任务编号 | B7                                                        |
| 优先级   | P1                                                        |
| 阶段     | Phase 2 剩余                                              |
| 流       | Stream D (Agent 4)                                        |
| 依赖     | TASK-E1（可选；可直接使用现有 `StatisticsResultItem.Compute`） |
| 状态     | 待实现                                                    |

**目标**：将已有的 `StatisticsConfig` 模型集成到 `RealtimeChartView` 的右侧面板 UI 中，使用户可以交互式地配置统计窗口和选择显示的统计指标。当前 ViewModel 的 `UpdateStatistics()` 方法和底部状态栏的绑定已完整可用，唯一缺失的是 `StatisticsConfig` 未实现 `INotifyPropertyChanged`，导致 UI 绑定（尤其是 CheckBox）的变更不能实时反映到统计输出。

---

## 功能需求

### 1. StatisticsConfig 可观察化

当前 `StatisticsConfig` 是一个普通 POCO 类。由于它位于 `MagnetometerSystem.Core` 项目（不引用 CommunityToolkit.Mvvm），需要手动实现 `INotifyPropertyChanged`，参照同项目中 `ChannelDisplayConfig` 的实现模式。

### 2. 统计设置 UI 面板

在 `RealtimeChartView.xaml` 右侧面板中已存在 "统计设置" Expander（第 385-406 行），其中包含：

- 统计窗口秒数 TextBox，绑定 `StatisticsConfig.WindowSeconds`
- 6 个 CheckBox 分别绑定 `StatisticsConfig.ShowMean`/`ShowStdDev`/`ShowPeakToPeak`/`ShowRms`/`ShowMin`/`ShowMax`

**现状分析**：UI 绑定代码已存在，但因 `StatisticsConfig` 不是 `INotifyPropertyChanged`，CheckBox 的勾选变更**可以写入属性**（WPF 默认 OneWay 对 CheckBox 实际是 TwoWay），但 ViewModel 侧不会收到属性变更通知。实际效果：

- CheckBox 勾选/取消 → 属性值确实会改变（WPF binding 的 setter 被调用）
- 但如果 ViewModel 在其他地方重新 set `StatisticsConfig` 为新实例，UI 不会更新
- WindowSeconds 的 TextBox 使用了 `UpdateSourceTrigger=LostFocus`，在失焦时写回，这是可行的

**需要做的**：让 `StatisticsConfig` 实现 `INotifyPropertyChanged`，确保双向绑定完全可靠。

### 3. 默认值

| 属性          | 默认值  |
| ------------- | ------- |
| WindowSeconds | 60      |
| ShowMean      | true    |
| ShowStdDev    | true    |
| ShowPeakToPeak| true    |
| ShowRms       | false   |
| ShowMin       | false   |
| ShowMax       | false   |

（以上默认值已在现有 `StatisticsConfig.cs` 中正确设置，无需修改。）

---

## 接口契约

### 修改文件：`src/MagnetometerSystem.Core/Models/StatisticsConfig.cs`

将现有 POCO 类改为实现 `INotifyPropertyChanged`：

```csharp
using System.ComponentModel;

namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 自定义滚动统计配置
/// </summary>
public class StatisticsConfig : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private double _windowSeconds = 60;
    /// <summary>统计时间窗口（秒），0 表示使用图表时间窗口</summary>
    public double WindowSeconds
    {
        get => _windowSeconds;
        set { if (_windowSeconds != value) { _windowSeconds = value; OnPropertyChanged(nameof(WindowSeconds)); } }
    }

    private bool _showMean = true;
    /// <summary>是否显示均值</summary>
    public bool ShowMean
    {
        get => _showMean;
        set { if (_showMean != value) { _showMean = value; OnPropertyChanged(nameof(ShowMean)); } }
    }

    private bool _showStdDev = true;
    /// <summary>是否显示标准差</summary>
    public bool ShowStdDev
    {
        get => _showStdDev;
        set { if (_showStdDev != value) { _showStdDev = value; OnPropertyChanged(nameof(ShowStdDev)); } }
    }

    private bool _showPeakToPeak = true;
    /// <summary>是否显示峰峰值 (max - min)</summary>
    public bool ShowPeakToPeak
    {
        get => _showPeakToPeak;
        set { if (_showPeakToPeak != value) { _showPeakToPeak = value; OnPropertyChanged(nameof(ShowPeakToPeak)); } }
    }

    private bool _showRms;
    /// <summary>是否显示 RMS（均方根）</summary>
    public bool ShowRms
    {
        get => _showRms;
        set { if (_showRms != value) { _showRms = value; OnPropertyChanged(nameof(ShowRms)); } }
    }

    private bool _showMin;
    /// <summary>是否显示最小值</summary>
    public bool ShowMin
    {
        get => _showMin;
        set { if (_showMin != value) { _showMin = value; OnPropertyChanged(nameof(ShowMin)); } }
    }

    private bool _showMax;
    /// <summary>是否显示最大值</summary>
    public bool ShowMax
    {
        get => _showMax;
        set { if (_showMax != value) { _showMax = value; OnPropertyChanged(nameof(ShowMax)); } }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
```

**关键点**：
- 不引入 CommunityToolkit.Mvvm 依赖（Core 项目不依赖它）
- 采用与 `ChannelDisplayConfig` 一致的手动 INPC 模式
- 所有默认值保持不变

### 现有文件（仅供参考，无需修改）

以下文件已正确实现，本任务不需要修改：

| 文件 | 说明 |
| ---- | ---- |
| `src/MagnetometerSystem.Core/Models/StatisticsResultItem.cs` | `Compute()` 静态方法 + `Format()` 方法，完整可用 |
| `src/MagnetometerSystem.App/ViewModels/RealtimeChartViewModel.cs` | `StatisticsConfig` 属性 (第 79-80 行) + `UpdateStatistics()` 方法 (第 397-429 行)，已正确使用 StatisticsConfig |
| `src/MagnetometerSystem.App/Views/RealtimeChartView.xaml` | 统计设置 Expander (第 385-406 行) 已存在，绑定表达式已正确 |

---

## 文件清单

| 操作 | 文件路径                                                 | 说明                                        |
| ---- | -------------------------------------------------------- | ------------------------------------------- |
| 修改 | `src/MagnetometerSystem.Core/Models/StatisticsConfig.cs` | 实现 INotifyPropertyChanged                 |

### 禁止修改的文件

- `src/MagnetometerSystem.Core/Protocol/` 目录下所有文件
- `src/MagnetometerSystem.Core/Communication/` 目录下所有文件
- `src/MagnetometerSystem.Infrastructure/` 目录下所有文件

---

## 实现指南

### 步骤一：修改 StatisticsConfig

1. 打开 `src/MagnetometerSystem.Core/Models/StatisticsConfig.cs`。
2. 添加 `using System.ComponentModel;`。
3. 让类实现 `INotifyPropertyChanged` 接口。
4. 将每个自动属性改为带 backing field 的手动属性，在 setter 中触发 `PropertyChanged` 事件。
5. 完整代码见上方"接口契约"部分。

**注意事项**：
- `ChannelDisplayConfig`（同项目，`src/MagnetometerSystem.Core/Models/ChannelDisplayConfig.cs`）已采用完全相同的模式，可直接参照。
- `ChannelDisplayConfig` 目前只对 `DisplayOffset` 一个属性做了 INPC（其他属性如 `Visible` 是自动属性）。`StatisticsConfig` 需要对**所有属性**实现 INPC，因为所有属性都绑定到 UI CheckBox/TextBox。

### 步骤二：验证 XAML 绑定（无需修改）

现有 XAML（第 385-406 行）中的绑定已正确：

```xml
<Expander Header="统计设置" IsExpanded="False" Margin="0,0,0,6">
    <StackPanel Margin="0,4,0,0">
        <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
            <TextBlock Text="窗口:" VerticalAlignment="Center" FontSize="11" Margin="0,0,3,0"/>
            <TextBox Width="50" FontSize="11"
                     Text="{Binding StatisticsConfig.WindowSeconds, UpdateSourceTrigger=LostFocus}"/>
            <TextBlock Text="s" VerticalAlignment="Center" FontSize="11" Margin="3,0,0,0" Foreground="Gray"/>
        </StackPanel>
        <CheckBox Content="均值 (Avg)" FontSize="11" Margin="0,1"
                  IsChecked="{Binding StatisticsConfig.ShowMean}"/>
        <CheckBox Content="标准差 (Std)" FontSize="11" Margin="0,1"
                  IsChecked="{Binding StatisticsConfig.ShowStdDev}"/>
        <CheckBox Content="峰峰值 (PP)" FontSize="11" Margin="0,1"
                  IsChecked="{Binding StatisticsConfig.ShowPeakToPeak}"/>
        <CheckBox Content="均方根 (RMS)" FontSize="11" Margin="0,1"
                  IsChecked="{Binding StatisticsConfig.ShowRms}"/>
        <CheckBox Content="最小值 (Min)" FontSize="11" Margin="0,1"
                  IsChecked="{Binding StatisticsConfig.ShowMin}"/>
        <CheckBox Content="最大值 (Max)" FontSize="11" Margin="0,1"
                  IsChecked="{Binding StatisticsConfig.ShowMax}"/>
    </StackPanel>
</Expander>
```

WPF 绑定路径 `StatisticsConfig.ShowMean` 会通过 `RealtimeChartViewModel.StatisticsConfig` 属性到达 `StatisticsConfig.ShowMean`。只要 `StatisticsConfig` 实现了 INPC，WPF 的 binding 引擎就能正确监听子属性变更。

### 步骤三：功能验证清单

1. 启动采集，确认底部状态栏默认显示 Avg + Std + PP 三项统计。
2. 在右侧面板展开"统计设置"，取消勾选"标准差"→ 状态栏立即不再显示 Std 项。
3. 勾选"RMS"和"最大值"→ 状态栏立即显示 RMS 和 Max 项。
4. 修改窗口秒数为 10，点击其他地方使 TextBox 失焦 → 统计值基于最近 10 秒数据重新计算。
5. 修改窗口秒数为 0 → 统计使用与图表时间窗口相同的范围。

---

## 验收标准

| 编号 | 验收条件                                                               | 验证方式       |
| ---- | ---------------------------------------------------------------------- | -------------- |
| AC-1 | CheckBox 勾选/取消后，底部状态栏对应统计项立即出现/消失                 | 手动测试       |
| AC-2 | 修改统计窗口秒数并失焦后，统计值基于新窗口重新计算                     | 手动测试       |
| AC-3 | 默认状态：均值、标准差、峰峰值勾选；RMS、最小值、最大值未勾选；窗口 60s | 启动后检查     |
| AC-4 | `StatisticsConfig` 实现 `INotifyPropertyChanged`，所有属性均触发通知    | 代码审查       |
| AC-5 | Core 项目不引入 CommunityToolkit.Mvvm 依赖                             | 检查 .csproj   |
| AC-6 | 未修改任何 Core/Protocol/、Core/Communication/、Infrastructure/ 下的文件 | `git diff`    |

---

## 单元测试要求

本任务修改量较小（仅一个文件的 INPC 重构），无需新建独立单元测试文件。但建议在现有测试项目中验证 `StatisticsConfig` 的 INPC 行为：

文件路径：`tests/MagnetometerSystem.Core.Tests/Models/StatisticsConfigTests.cs`（如需创建）

### 测试用例

```csharp
[TestClass]
public class StatisticsConfigTests
{
    /// <summary>
    /// 修改属性应触发 PropertyChanged 事件
    /// </summary>
    [TestMethod]
    public void SetProperty_RaisesPropertyChanged()
    {
        var config = new StatisticsConfig();
        var changedProperties = new List<string>();
        config.PropertyChanged += (s, e) => changedProperties.Add(e.PropertyName!);

        config.WindowSeconds = 30;
        config.ShowMean = false;
        config.ShowStdDev = false;
        config.ShowPeakToPeak = false;
        config.ShowRms = true;
        config.ShowMin = true;
        config.ShowMax = true;

        CollectionAssert.AreEquivalent(
            new[] { "WindowSeconds", "ShowMean", "ShowStdDev", "ShowPeakToPeak", "ShowRms", "ShowMin", "ShowMax" },
            changedProperties);
    }

    /// <summary>
    /// 设置相同值不应触发 PropertyChanged
    /// </summary>
    [TestMethod]
    public void SetSameValue_DoesNotRaisePropertyChanged()
    {
        var config = new StatisticsConfig();
        var raised = false;
        config.PropertyChanged += (s, e) => raised = true;

        // 设置与默认值相同的值
        config.WindowSeconds = 60;  // 默认就是 60
        config.ShowMean = true;     // 默认就是 true

        Assert.IsFalse(raised, "设置相同值时不应触发 PropertyChanged");
    }

    /// <summary>
    /// 默认值应正确
    /// </summary>
    [TestMethod]
    public void DefaultValues_AreCorrect()
    {
        var config = new StatisticsConfig();

        Assert.AreEqual(60.0, config.WindowSeconds);
        Assert.IsTrue(config.ShowMean);
        Assert.IsTrue(config.ShowStdDev);
        Assert.IsTrue(config.ShowPeakToPeak);
        Assert.IsFalse(config.ShowRms);
        Assert.IsFalse(config.ShowMin);
        Assert.IsFalse(config.ShowMax);
    }
}
```
