using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 采集页“计算通道”的总场与梯度向导：选来源、检查单位，确认后向图表的计算通道列表添加一项。
/// </summary>
public partial class ComputedChannelWizardViewModel : ObservableObject
{
    private readonly ObservableCollection<ComputedChannelDefinition> _computedChannels;
    private readonly Func<IReadOnlyList<SourceOption>> _protocolChannels;

    /// <param name="computedChannels">图表的计算通道列表，确认时添加到这里；梯度也可以选其中已有的通道作来源。</param>
    /// <param name="protocolChannels">当前协议的原始通道（按通道索引，公式变量为 CH0、CH1…）。</param>
    public ComputedChannelWizardViewModel(
        ObservableCollection<ComputedChannelDefinition> computedChannels,
        Func<IReadOnlyList<SourceOption>> protocolChannels)
    {
        _computedChannels = computedChannels;
        _protocolChannels = protocolChannels;
    }

    [ObservableProperty]
    private bool _isAddingTotalField;

    [ObservableProperty]
    private bool _isAddingGradient;

    [ObservableProperty]
    private int _wizardSourceA;

    [ObservableProperty]
    private int _wizardSourceB = 1;

    [ObservableProperty]
    private int _wizardSourceC = 2;

    /// <summary>向导可选的原始通道列表</summary>
    [ObservableProperty]
    private ObservableCollection<SourceOption> _wizardRawSources = new();

    /// <summary>向导可选的梯度源列表（原始通道 + 已有计算通道）</summary>
    [ObservableProperty]
    private ObservableCollection<SourceOption> _wizardGradientSources = new();

    /// <summary>梯度基线距离 (m)，用于将差值转换为梯度值 (nT/m)</summary>
    private double _gradientBaselineDistance = 1.0;
    public double GradientBaselineDistance
    {
        get => _gradientBaselineDistance;
        set => SetProperty(ref _gradientBaselineDistance, value);
    }

    [ObservableProperty]
    private string _computationError = "";

    /// <summary>收起向导（开始新的采集、手动添加公式通道时）。</summary>
    internal void Close()
    {
        IsAddingTotalField = false;
        IsAddingGradient = false;
    }

    // ---- 总场向导 ----

    [RelayCommand]
    private void StartAddTotalField()
    {
        ComputationError = "";
        BuildWizardRawSources();
        WizardSourceA = 0;
        WizardSourceB = Math.Min(1, WizardRawSources.Count - 1);
        WizardSourceC = Math.Min(2, WizardRawSources.Count - 1);
        IsAddingTotalField = true;
        IsAddingGradient = false;
    }

    [RelayCommand]
    private void ConfirmAddTotalField()
    {
        ComputationError = "";
        if (WizardSourceA < 0 || WizardSourceA >= WizardRawSources.Count
            || WizardSourceB < 0 || WizardSourceB >= WizardRawSources.Count
            || WizardSourceC < 0 || WizardSourceC >= WizardRawSources.Count)
        {
            IsAddingTotalField = false;
            return;
        }

        var sources = new[] { WizardRawSources[WizardSourceA], WizardRawSources[WizardSourceB], WizardRawSources[WizardSourceC] };
        if (sources.Select(s => s.FormulaExpr).Distinct().Count() != 3 || !HaveSameMagneticUnit(sources))
        { ComputationError = "总场需要三个不同通道，且使用相同的磁场单位。"; return; }
        var a = WizardRawSources[WizardSourceA].FormulaExpr;
        var b = WizardRawSources[WizardSourceB].FormulaExpr;
        var c = WizardRawSources[WizardSourceC].FormulaExpr;
        var formula = $"sqrt({a}*{a} + {b}*{b} + {c}*{c})";

        int totalCount = _computedChannels.Count(ch => ch.ChannelType == ComputedChannelType.TotalField) + 1;
        _computedChannels.Add(new ComputedChannelDefinition
        {
            Name = $"Total{totalCount}",
            Unit = sources[0].Unit,
            Formula = formula,
            ChannelType = ComputedChannelType.TotalField,
            ColorHex = "#FF000000",
            LineWidth = 2f,
        });

        IsAddingTotalField = false;
    }

    // ---- 梯度向导 ----

    [RelayCommand]
    private void StartAddGradient()
    {
        ComputationError = "";
        BuildWizardGradientSources();
        WizardSourceA = 0;
        WizardSourceB = Math.Min(1, WizardGradientSources.Count - 1);
        IsAddingTotalField = false;
        IsAddingGradient = true;
    }

    [RelayCommand]
    private void ConfirmAddGradient()
    {
        ComputationError = "";
        if (WizardSourceA < 0 || WizardSourceA >= WizardGradientSources.Count
            || WizardSourceB < 0 || WizardSourceB >= WizardGradientSources.Count)
        {
            IsAddingGradient = false;
            return;
        }

        var sources = new[] { WizardGradientSources[WizardSourceA], WizardGradientSources[WizardSourceB] };
        if (sources[0].FormulaExpr == sources[1].FormulaExpr || !HaveSameMagneticUnit(sources))
        { ComputationError = "磁场梯度需要两个不同来源，且使用相同的磁场单位。"; return; }
        var a = WizardGradientSources[WizardSourceA].FormulaExpr;
        var b = WizardGradientSources[WizardSourceB].FormulaExpr;
        if (!double.IsFinite(GradientBaselineDistance) || GradientBaselineDistance <= 0)
        { ComputationError = "梯度基线距离必须为有限正数。"; return; }
        var formula = GradientBaselineDistance != 1.0
            ? $"(({a}) - ({b})) / {GradientBaselineDistance.ToString("R", CultureInfo.InvariantCulture)}"
            : $"({a}) - ({b})";

        int gradCount = _computedChannels.Count(ch => ch.ChannelType == ComputedChannelType.Gradient) + 1;
        _computedChannels.Add(new ComputedChannelDefinition
        {
            Name = $"Grad{gradCount}",
            Unit = sources[0].Unit + "/m",
            Formula = formula,
            ChannelType = ComputedChannelType.Gradient,
            ColorHex = "#FF808080",
        });

        IsAddingGradient = false;
    }

    partial void OnIsAddingTotalFieldChanged(bool value) => ComputationError = "";
    partial void OnIsAddingGradientChanged(bool value) => ComputationError = "";

    [RelayCommand]
    private void CancelAddWizard()
    {
        ComputationError = "";
        IsAddingTotalField = false;
        IsAddingGradient = false;
    }

    private static bool HaveSameMagneticUnit(SourceOption[] sources) =>
        sources.Select(s => s.Unit).Distinct().Count() == 1
        && sources[0].Unit is "nT" or "uT" or "µT" or "μT" or "mT" or "T";

    /// <summary>
    /// 构建向导可选的原始通道列表
    /// </summary>
    private void BuildWizardRawSources()
    {
        WizardRawSources.Clear();
        foreach (var source in _protocolChannels())
            WizardRawSources.Add(source);
    }

    /// <summary>
    /// 构建向导可选的梯度源列表（原始通道 + 已有计算通道）
    /// </summary>
    private void BuildWizardGradientSources()
    {
        WizardGradientSources.Clear();

        // 原始通道
        foreach (var source in _protocolChannels())
            WizardGradientSources.Add(source);

        // 已有计算通道（内联其公式）
        foreach (var comp in _computedChannels)
        {
            if (!string.IsNullOrWhiteSpace(comp.Formula))
            {
                WizardGradientSources.Add(new SourceOption
                {
                    Label = comp.Name,
                    FormulaExpr = comp.Formula,
                    Unit = comp.Unit,
                });
            }
        }
    }
}
