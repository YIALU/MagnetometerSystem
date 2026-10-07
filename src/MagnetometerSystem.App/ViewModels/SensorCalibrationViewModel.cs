using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>偏移 / 增益表格的一行：按通道序号填写，文本在保存时统一校验。</summary>
public partial class CalibrationChannelRow : ObservableObject
{
    public int Index { get; init; }
    public string Name => $"CH{Index}";

    [ObservableProperty] private string _offset = "0";
    [ObservableProperty] private string _gain = "1";
}

/// <summary>
/// 传感器校准 ViewModel — 管理硬铁/软铁校准参数（偏移 + 增益）
/// </summary>
public partial class SensorCalibrationViewModel : ObservableObject
{
    private readonly ICalibrationRepository _calibrationRepository;

    public SensorCalibrationViewModel(ICalibrationRepository calibrationRepository)
    {
        _calibrationRepository = calibrationRepository;
        Channels.CollectionChanged += (_, _) => RemoveLastChannelCommand.NotifyCanExecuteChanged();
        ResetChannels(3);
    }

    private bool _isLoaded;
    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded) return;
        _isLoaded = true;
        await LoadProfilesAsync();
    }

    // ---- 配置列表 ----

    public ObservableCollection<CalibrationParams> Profiles { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    private CalibrationParams? _selectedProfile;

    public SensorType[] SensorTypes { get; } = Enum.GetValues<SensorType>();

    // ---- 编辑区域 ----

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private SensorType _editSensorType = SensorType.TriaxialFluxgate;

    [ObservableProperty]
    private string _editSensorSerial = string.Empty;

    [ObservableProperty]
    private string _editNotes = string.Empty;

    /// <summary>按通道填写的偏移与增益。</summary>
    public ObservableCollection<CalibrationChannelRow> Channels { get; } = new();

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
        ResetChannels(3);
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
        var count = Math.Max(SelectedProfile.OffsetValues.Length, SelectedProfile.GainValues.Length);
        ResetChannels(count);
        for (int i = 0; i < count; i++)
        {
            Channels[i].Offset = i < SelectedProfile.OffsetValues.Length ? SelectedProfile.OffsetValues[i].ToString("G", CultureInfo.InvariantCulture) : "0";
            Channels[i].Gain = i < SelectedProfile.GainValues.Length ? SelectedProfile.GainValues[i].ToString("G", CultureInfo.InvariantCulture) : "1";
        }
        SetStatus($"已加载: {SelectedProfile.Name}");
    }

    [RelayCommand]
    private void AddChannel() => Channels.Add(new CalibrationChannelRow { Index = Channels.Count });

    private bool CanRemoveChannel() => Channels.Count > 1;

    [RelayCommand(CanExecute = nameof(CanRemoveChannel))]
    private void RemoveLastChannel()
    {
        if (Channels.Count > 1) Channels.RemoveAt(Channels.Count - 1);
    }

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            SetStatus("请输入配置名称", isError: true);
            return;
        }

        var offsets = new double[Channels.Count];
        var gains = new double[Channels.Count];
        foreach (var row in Channels)
        {
            if (!TryParse(row.Offset, out offsets[row.Index]))
            {
                SetStatus($"{row.Name} 的偏移不是有效数字", isError: true);
                return;
            }
            if (!TryParse(row.Gain, out gains[row.Index]))
            {
                SetStatus($"{row.Name} 的增益不是有效数字", isError: true);
                return;
            }
        }

        try
        {
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

    [RelayCommand(CanExecute = nameof(HasSelectedProfile))]
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

    private bool HasSelectedProfile() => SelectedProfile != null;

    private void ResetChannels(int count)
    {
        Channels.Clear();
        for (int i = 0; i < Math.Max(1, count); i++)
            Channels.Add(new CalibrationChannelRow { Index = i });
    }

    private static bool TryParse(string text, out double value) =>
        double.TryParse(text?.Trim().Replace('−', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private void SetStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        IsStatusError = isError;
    }
}
