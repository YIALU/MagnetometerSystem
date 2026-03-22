using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 传感器校准 ViewModel — 管理硬铁/软铁校准参数（偏移 + 增益）
/// </summary>
public partial class SensorCalibrationViewModel : ObservableObject
{
    private readonly ICalibrationRepository _calibrationRepository;

    public SensorCalibrationViewModel(ICalibrationRepository calibrationRepository)
    {
        _calibrationRepository = calibrationRepository;
        _ = LoadProfilesAsync();
    }

    // ---- 配置列表 ----

    public ObservableCollection<CalibrationParams> Profiles { get; } = new();

    [ObservableProperty]
    private CalibrationParams? _selectedProfile;

    public SensorType[] SensorTypes { get; } = Enum.GetValues<SensorType>();

    // ---- 编辑区域 ----

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private SensorType _editSensorType;

    [ObservableProperty]
    private string _editSensorSerial = string.Empty;

    [ObservableProperty]
    private string _editNotes = string.Empty;

    [ObservableProperty]
    private string _editOffsetValues = "0, 0, 0";

    [ObservableProperty]
    private string _editGainValues = "1, 1, 1";

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    // ---- 命令 ----

    [RelayCommand]
    private async Task LoadProfilesAsync()
    {
        try
        {
            var profiles = await _calibrationRepository.GetCalibrationProfilesAsync();
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                Profiles.Clear();
                foreach (var p in profiles)
                    Profiles.Add(p);
            });
        }
        catch (Exception ex)
        {
            SetStatus($"加载失败: {ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private void NewProfile()
    {
        SelectedProfile = null;
        EditName = $"校准_{DateTime.Now:yyyyMMdd_HHmmss}";
        EditSensorType = SensorType.TriaxialFluxgate;
        EditSensorSerial = string.Empty;
        EditNotes = string.Empty;
        EditOffsetValues = "0, 0, 0";
        EditGainValues = "1, 1, 1";
        SetStatus("已创建新配置，请编辑后保存");
    }

    [RelayCommand]
    private void LoadSelectedProfile()
    {
        if (SelectedProfile == null) return;

        EditName = SelectedProfile.Name;
        EditSensorType = SelectedProfile.SensorType;
        EditSensorSerial = SelectedProfile.SensorSerial ?? string.Empty;
        EditNotes = SelectedProfile.Notes ?? string.Empty;
        EditOffsetValues = string.Join(", ", SelectedProfile.OffsetValues.Select(v => v.ToString("G")));
        EditGainValues = string.Join(", ", SelectedProfile.GainValues.Select(v => v.ToString("G")));
        SetStatus($"已加载: {SelectedProfile.Name}");
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            SetStatus("请输入配置名称", isError: true);
            return;
        }

        try
        {
            var offsets = ParseDoubleArray(EditOffsetValues);
            var gains = ParseDoubleArray(EditGainValues);

            if (offsets == null || gains == null)
            {
                SetStatus("偏移量或增益值格式无效，请使用逗号分隔的数字", isError: true);
                return;
            }

            var profile = SelectedProfile ?? new CalibrationParams();
            profile.Name = EditName;
            profile.SensorType = EditSensorType;
            profile.SensorSerial = string.IsNullOrWhiteSpace(EditSensorSerial) ? null : EditSensorSerial;
            profile.Notes = string.IsNullOrWhiteSpace(EditNotes) ? null : EditNotes;
            profile.OffsetValues = offsets;
            profile.GainValues = gains;

            await _calibrationRepository.SaveCalibrationProfileAsync(profile);
            await LoadProfilesAsync();
            SetStatus($"已保存: {profile.Name}");
        }
        catch (Exception ex)
        {
            SetStatus($"保存失败: {ex.Message}", isError: true);
        }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile == null) return;

        var result = System.Windows.MessageBox.Show(
            $"确定要删除校准配置 '{SelectedProfile.Name}' 吗？",
            "确认删除",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            await _calibrationRepository.DeleteCalibrationProfileAsync(SelectedProfile.Id);
            await LoadProfilesAsync();
            SelectedProfile = null;
            NewProfile();
            SetStatus("已删除");
        }
        catch (Exception ex)
        {
            SetStatus($"删除失败: {ex.Message}", isError: true);
        }
    }

    private static double[]? ParseDoubleArray(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var parts = input.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var values = new List<double>();

        foreach (var part in parts)
        {
            if (double.TryParse(part.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double val))
            {
                values.Add(val);
            }
            else
            {
                return null;
            }
        }

        return values.ToArray();
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }
}
