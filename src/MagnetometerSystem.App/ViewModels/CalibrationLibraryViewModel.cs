using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Infrastructure.Export;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>校正页的“配置库”：已保存的正交度配置的列表、删除、导出和使用说明。</summary>
public partial class CalibrationLibraryViewModel : ObservableObject
{
    private readonly ICalibrationRepository _calibrationRepository;
    private readonly IDialogService _dialogs;

    public CalibrationLibraryViewModel(ICalibrationRepository calibrationRepository, IDialogService dialogs)
    {
        _calibrationRepository = calibrationRepository;
        _dialogs = dialogs;
    }

    [ObservableProperty]
    private ObservableCollection<OrthogonalityParams> _savedProfiles = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSavedProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectedProfileJsonCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSelectedProfileCsvCommand))]
    private OrthogonalityParams? _selectedSavedProfile;

    private bool HasSelectedSavedProfile() => SelectedSavedProfile != null;

    /// <summary>配置库的加载 / 删除结果；失败时显示原因，不只写日志。</summary>
    [ObservableProperty]
    private string _libraryStatus = string.Empty;

    /// <summary>重新读取配置列表（“刷新”按钮、首次进入校正页和保存新配置后）。</summary>
    [RelayCommand]
    internal async Task LoadSavedProfilesAsync()
    {
        try
        {
            var profiles = await _calibrationRepository.GetOrthogonalityProfilesAsync();
            SavedProfiles.Clear();
            foreach (var p in profiles)
                SavedProfiles.Add(p);
            LibraryStatus = string.Empty;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"加载校正配置列表失败: {ex.Message}");
            LibraryStatus = $"加载配置列表失败：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSavedProfile))]
    private async Task DeleteSavedProfileAsync()
    {
        if (SelectedSavedProfile is not { } profile) return;
        if (!_dialogs.Confirm($"确定要删除正交度配置“{profile.Name}”吗？此操作不能撤销。", "确认删除")) return;
        try
        {
            await _calibrationRepository.DeleteOrthogonalityProfileAsync(profile.Id);
            SavedProfiles.Remove(profile);
            LibraryStatus = $"已删除“{profile.Name}”";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"删除配置失败: {ex.Message}");
            LibraryStatus = $"删除失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ShowProfileUsageHelp() => _dialogs.ShowHelp(HelpTopic.ProfileUsage);

    [RelayCommand(CanExecute = nameof(HasSelectedSavedProfile))]
    private async Task ExportSelectedProfileJsonAsync()
    {
        if (SelectedSavedProfile is not { } profile)
        {
            _dialogs.Notify("请先在表格中选中一个配置", "提示");
            return;
        }
        var path = _dialogs.PickSaveFile("导出正交度配置 (JSON)", "JSON 文件 (*.json)|*.json", ".json",
            $"{OrthogonalityProfileExporter.SanitizeFileName(profile.Name)}.json");
        if (path == null) return;
        try
        {
            var json = OrthogonalityProfileExporter.BuildJson(profile);
            await File.WriteAllTextAsync(path, json);
            _dialogs.Notify($"已导出: {path}", "成功");
        }
        catch (Exception ex)
        {
            _dialogs.Notify($"导出失败: {ex.Message}", "错误");
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSavedProfile))]
    private async Task ExportSelectedProfileCsvAsync()
    {
        if (SelectedSavedProfile is not { } profile)
        {
            _dialogs.Notify("请先在表格中选中一个配置", "提示");
            return;
        }
        var path = _dialogs.PickSaveFile("导出正交度配置 (CSV)", "CSV 文件 (*.csv)|*.csv", ".csv",
            $"{OrthogonalityProfileExporter.SanitizeFileName(profile.Name)}.csv");
        if (path == null) return;
        try
        {
            var csv = OrthogonalityProfileExporter.BuildCsv(profile);
            await File.WriteAllTextAsync(path, csv, new System.Text.UTF8Encoding(true));
            _dialogs.Notify($"已导出: {path}", "成功");
        }
        catch (Exception ex)
        {
            _dialogs.Notify($"导出失败: {ex.Message}", "错误");
        }
    }
}
