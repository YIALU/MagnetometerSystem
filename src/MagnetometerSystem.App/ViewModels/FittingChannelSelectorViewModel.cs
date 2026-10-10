using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Calibration;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>“拟合通道”下拉框的一项：协议或会话中的通道索引与显示文字。</summary>
public sealed record FittingChannelOption(int Index, string Label);

/// <summary>拟合通道的来源：来源标识（没有可选通道时为空）、通道名称与单位，以及没有通道时的提示。</summary>
public sealed record FittingChannelSource(string Key, IReadOnlyList<string> Names, IReadOnlyList<string> Units, string EmptyHint);

/// <summary>
/// 第 2 步的“拟合通道”：协议决定通道顺序、名称和单位，拟合用哪几个通道由用户选定（按名称自动建议），不取前缀。
/// 来源（实时连接或所选会话）与组数由父视图模型提供，每次用到时重新读取；这里不读取会话，
/// 用户改了选择只发出 <see cref="Edited"/>，由父视图模型决定是否按新的通道重新读取。
/// </summary>
public partial class FittingChannelSelectorViewModel : ObservableObject
{
    private readonly Func<FittingChannelSource> _source;
    private readonly Func<int> _groups;
    private bool _applyingSuggestion;

    /// <param name="source">当前来源的通道。</param>
    /// <param name="groups">拟合的组数：单三轴 1，双三轴 2。</param>
    public FittingChannelSelectorViewModel(Func<FittingChannelSource> source, Func<int> groups)
    {
        _source = source;
        _groups = groups;
    }

    /// <summary>当前来源（实时连接或所选会话）的通道。</summary>
    public ObservableCollection<FittingChannelOption> Options { get; } = new();

    [ObservableProperty] private int _fitX1 = -1;
    [ObservableProperty] private int _fitY1 = -1;
    [ObservableProperty] private int _fitZ1 = -1;
    [ObservableProperty] private int _fitX2 = -1;
    [ObservableProperty] private int _fitY2 = -1;
    [ObservableProperty] private int _fitZ2 = -1;

    /// <summary>所选通道的单位，或尚不能开始的原因。</summary>
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private bool _isValid;

    /// <summary>选项是按哪个来源建立的；停止采集后来源已变时父视图模型据此重建。</summary>
    public string OptionsSourceKey { get; private set; } = "";

    /// <summary>所选通道、提示或有效性已更新（自动建议或用户修改）。</summary>
    public event Action? Changed;

    /// <summary>用户改了某个下拉框，或点了“自动识别”。</summary>
    public event Action? Edited;

    /// <summary>所选通道：每组按 X、Y、Z 排列。</summary>
    public int[] SelectedMap() => _groups() == 2
        ? [FitX1, FitY1, FitZ1, FitX2, FitY2, FitZ2]
        : [FitX1, FitY1, FitZ1];

    /// <summary>来源变化（新连接、选了会话、切换来源）：重建选项并按名称重新建议。</summary>
    public void Refresh()
    {
        var source = _source();
        OptionsSourceKey = source.Key;
        Options.Clear();
        if (source.Names.Count == source.Units.Count)
            for (int i = 0; i < source.Names.Count; i++)
            {
                string name = source.Names[i] ?? "", unit = source.Units[i] ?? "";
                var label = name.Length > 0 ? name : $"通道 {i}";
                Options.Add(new FittingChannelOption(i, unit.Length > 0 ? $"{label} ({unit})" : label));
            }
        ApplySuggestion();
    }

    /// <summary>按通道名称重新建议所选通道；给不出可靠建议时清空选择，等用户选择。</summary>
    public void ApplySuggestion()
    {
        var source = _source();
        var map = source.Names.Count == source.Units.Count ? FittingChannelMap.Suggest(source.Names, source.Units, _groups()) : null;
        _applyingSuggestion = true;
        try
        {
            FitX1 = map?[0] ?? -1; FitY1 = map?[1] ?? -1; FitZ1 = map?[2] ?? -1;
            FitX2 = map is { Length: 6 } ? map[3] : -1;
            FitY2 = map is { Length: 6 } ? map[4] : -1;
            FitZ2 = map is { Length: 6 } ? map[5] : -1;
        }
        finally { _applyingSuggestion = false; }
        UpdateHint();
        Changed?.Invoke();
    }

    /// <summary>“自动识别”：按通道名称重新建议。</summary>
    [RelayCommand]
    private void AutoDetect()
    {
        ApplySuggestion();
        Edited?.Invoke();
    }

    partial void OnFitX1Changed(int value) => OnSelectionEdited();
    partial void OnFitY1Changed(int value) => OnSelectionEdited();
    partial void OnFitZ1Changed(int value) => OnSelectionEdited();
    partial void OnFitX2Changed(int value) => OnSelectionEdited();
    partial void OnFitY2Changed(int value) => OnSelectionEdited();
    partial void OnFitZ2Changed(int value) => OnSelectionEdited();

    /// <summary>用户改了某个下拉框：更新提示并通知父视图模型。</summary>
    private void OnSelectionEdited()
    {
        if (_applyingSuggestion) return;
        UpdateHint();
        Changed?.Invoke();
        Edited?.Invoke();
    }

    private void UpdateHint()
    {
        var source = _source();
        if (source.Names.Count == 0)
        {
            IsValid = false;
            Hint = source.EmptyHint;
            return;
        }
        try
        {
            var unit = FittingChannelMap.Validate(SelectedMap(), source.Units, _groups());
            IsValid = true;
            Hint = $"单位 {unit}";
        }
        catch (ArgumentException ex)
        {
            IsValid = false;
            Hint = ex.Message;
        }
    }
}
