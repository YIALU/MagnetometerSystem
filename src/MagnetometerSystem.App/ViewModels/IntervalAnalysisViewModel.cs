using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Processing;
using MagnetometerSystem.Infrastructure.Export;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 采集页右侧“区间”页签：按起止秒数（或在曲线上拖动）选一段，计算原始读数的统计量并可导出这一段。
/// 数据取自曲线缓冲的原始值副本，不用显示值，也不受暂停、偏移和滤波影响。
/// </summary>
public partial class IntervalAnalysisViewModel : ObservableObject
{
    private readonly Func<ChartRawSnapshot> _snapshotRaw;
    private readonly IDialogService _dialogs;

    /// <param name="snapshotRaw">取曲线缓冲中原始读数的副本。</param>
    public IntervalAnalysisViewModel(Func<ChartRawSnapshot> snapshotRaw, IDialogService dialogs)
    {
        _snapshotRaw = snapshotRaw;
        _dialogs = dialogs;
    }

    [ObservableProperty]
    private IntervalSelection? _currentInterval;

    [ObservableProperty]
    private IntervalStatisticsResult? _intervalStatistics;

    [ObservableProperty]
    private string _intervalStartInput = "";

    [ObservableProperty]
    private string _intervalEndInput = "";

    /// <summary>按输入的起止秒数选定区间并计算统计；输入无效时保持原样。</summary>
    [RelayCommand]
    internal void ApplyIntervalSelection()
    {
        if (!double.TryParse(IntervalStartInput, out double start) ||
            !double.TryParse(IntervalEndInput, out double end))
        {
            return;
        }

        var interval = new IntervalSelection(start, end);
        if (!interval.IsValid) return;

        CurrentInterval = interval;
        ComputeIntervalStatistics();
    }

    [RelayCommand]
    internal void ClearIntervalSelection()
    {
        CurrentInterval = null;
        IntervalStatistics = null;
        IntervalStartInput = "";
        IntervalEndInput = "";
    }

    [RelayCommand]
    private async Task ExportIntervalAsync()
    {
        if (CurrentInterval == null || IntervalStatistics == null) return;

        var path = _dialogs.PickSaveFile("导出区间数据", "CSV 文件 (*.csv)|*.csv", ".csv",
            $"interval_{CurrentInterval.StartTime:F1}s_{CurrentInterval.EndTime:F1}s.csv");
        if (path == null) return;

        await ExportToFileAsync(path);
    }

    private void ComputeIntervalStatistics()
    {
        if (CurrentInterval == null) return;
        var raw = _snapshotRaw();
        IntervalStatistics = IntervalStatisticsResult.Compute(CurrentInterval, raw.Times, raw.Channels, raw.Names);
    }

    /// <summary>把当前区间内的原始读数写到 <paramref name="filePath"/>；区间内没有数据时不写文件。</summary>
    internal async Task ExportToFileAsync(string filePath)
    {
        if (CurrentInterval == null) return;

        var raw = _snapshotRaw();
        var (startIdx, count) = CurrentInterval.GetIndices(raw.Times);
        if (count == 0) return;

        await IntervalCsvExporter.WriteFileAsync(filePath, raw.Times, raw.Channels, raw.Names, raw.Units, startIdx, count);
    }
}
