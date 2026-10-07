using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 正交度校正向导 ViewModel（4 步向导）
/// Step 1: 选择传感器  Step 2: 采集/导入数据  Step 3: 计算与结果  Step 4: 保存配置
/// </summary>
public enum CalibrationCollectionMode { Continuous, Manual48 }

/// <summary>第 2 步的数据来源：实时读数、外部文件或已保存会话。</summary>
public enum CalibrationDataSource { Live, File, Session }

/// <summary>“拟合通道”下拉框的一项：协议或会话中的通道索引与显示文字。</summary>
public sealed record FittingChannelOption(int Index, string Label);

/// <summary>结果页一组三轴的显示数据：评级、矩阵、偏移、残差和可视化用的样本。数值单位与拟合数据一致。</summary>
public sealed class OrthoGroupResult(OrthogonalityResult result, double[,] rawData, double[,] correctedData)
{
    public FitQuality Quality { get; } = result.Quality;
    public double[,] RawData { get; } = rawData;
    public double[,] CorrectedData { get; } = correctedData;

    /// <summary>拟合数据的磁场单位（nT / uT / mT / T）。</summary>
    public string Unit { get; } = OrthogonalityParams.CanonicalUnit(result.Parameters.Unit);

    /// <summary>按行排列的补偿矩阵 M（3×3）。</summary>
    public string[] Matrix { get; } = result.Parameters.CompensationMatrix.Select(v => v.ToString("F6", CultureInfo.InvariantCulture)).ToArray();

    /// <summary>偏移 O（X / Y / Z），与数据同单位。</summary>
    public string[] Offset { get; } = result.Parameters.Offset.Select(v => v.ToString("G6", CultureInfo.InvariantCulture)).ToArray();

    /// <summary>残差标准差折算到 nT 后分级；阈值与旧版一致，任何单位下物理含义相同。</summary>
    public string Rating => OrthogonalityCalibrationViewModel.RateQuality(result);

    private double ResidualNt => OrthogonalityCalibrationViewModel.ResidualStdInNt(result);

    /// <summary>评级对应的状态色：ok / warn / err。</summary>
    public string Level => ResidualNt switch { < 50 => "ok", < 200 => "warn", _ => "err" };

    public string RatingHint => ResidualNt switch
    {
        < 10 => "残差标准差低于 10 nT",
        < 50 => "残差标准差低于 50 nT",
        < 200 => "残差标准差低于 200 nT，建议增加姿态覆盖后重算",
        double.NaN => "单位未知，不能评级",
        _ => "残差标准差不低于 200 nT，建议检查数据后重新采集",
    };
}

public partial class OrthogonalityCalibrationViewModel : ObservableObject
{
    private readonly IOrthogonalityService _orthogonalityService;
    private readonly ICalibrationRepository _calibrationRepository;
    private readonly DataBus _dataBus;
    private readonly IDataStorageService _storageService;
    private readonly List<double[]> _collectedData = new();
    private readonly List<double[]> _collectedDataSecondGroup = new();
    private string _collectedUnit = "";
    private int _collectedChannelCount;
    private long _collectedGeneration;

    // 实时采集开始时冻结：所选通道索引（X、Y、Z[、X2、Y2、Z2]）、显示名称，以及当时的协议通道布局。
    private int[] _collectedMap = [];
    private string[] _collectedLabels = [];
    private IReadOnlyList<string> _collectedLayoutNames = [];
    private IReadOnlyList<string> _collectedLayoutUnits = [];

    /// <summary>导入 CSV 时数值的磁场单位（不自动猜测或换算）；实时采集与会话导入按协议通道单位自动设置。</summary>
    [ObservableProperty] private string _fittingUnit = "";
    public string[] FittingUnits { get; } = ["nT", "uT", "mT", "T"];
    public string CollectedUnit => _collectedUnit;
    public string ReferenceUnit => _collectedUnit.Length > 0 ? _collectedUnit : FittingUnit;
    /// <summary>参考场强的单位文字（标题与输入框后缀共用）；单位未知时提示待定。</summary>
    public string ReferenceUnitText => ReferenceUnit.Length > 0 ? ReferenceUnit : "单位待定";
    partial void OnFittingUnitChanged(string? oldValue, string newValue)
    {
        if (_collectedUnit.Length == 0 && OrthogonalityParams.CanonicalUnit(oldValue) != OrthogonalityParams.CanonicalUnit(newValue))
            ReferenceFieldStrength = null;
        OnPropertyChanged(nameof(ReferenceUnit)); OnPropertyChanged(nameof(ReferenceUnitText));
    }

    /// <summary>换一批拟合数据：记录单位与通道数，清掉上一批的结果，旧批次的回调不能混入。</summary>
    private void SetCollectedUnit(string unit, int channelCount)
    {
        if (OrthogonalityParams.CanonicalUnit(ReferenceUnit) != unit) ReferenceFieldStrength = null;
        Interlocked.Increment(ref _collectedGeneration);
        _collectedUnit = unit;
        _collectedChannelCount = channelCount;
        FittingUnit = unit;
        CalculationResult = null;
        SecondCalculationResult = null;
        FirstGroupResult = SecondGroupResult = null;
        SavedProfile = null;
        SavedSecondProfile = null;
        OnPropertyChanged(nameof(CollectedUnit));
        OnPropertyChanged(nameof(ReferenceUnit)); OnPropertyChanged(nameof(ReferenceUnitText));
    }

    // ========== 拟合通道 ==========
    // 协议决定通道顺序、名称和单位：拟合用哪几个通道由用户在第 2 步选定（按名称自动建议），不取前缀。

    /// <summary>当前来源（实时连接或所选会话）的通道。</summary>
    public ObservableCollection<FittingChannelOption> FittingChannelOptions { get; } = new();

    [ObservableProperty] private int _fitX1 = -1;
    [ObservableProperty] private int _fitY1 = -1;
    [ObservableProperty] private int _fitZ1 = -1;
    [ObservableProperty] private int _fitX2 = -1;
    [ObservableProperty] private int _fitY2 = -1;
    [ObservableProperty] private int _fitZ2 = -1;

    /// <summary>所选通道的单位，或尚不能开始的原因。</summary>
    [ObservableProperty] private string _fittingChannelHint = "";
    [ObservableProperty] private bool _isFittingChannelValid;

    /// <summary>导入文件有自己的列映射，不显示通道选择。</summary>
    public bool ShowsFittingChannels => DataSource != CalibrationDataSource.File;

    /// <summary>当前在通道选项中显示的会话（来源为“已保存会话”时）。</summary>
    private SessionInfo? _loadedSession;

    // 已有样本是按哪个来源、哪组通道取得的（导入文件时为 null）。来源相同而所选通道不同时，
    // 下拉框显示的已不是样本实际使用的列，不能据此计算和保存校正。
    private int[]? _samplesMap;
    private string _samplesSourceKey = "";

    private const string FileSourceKey = "file";

    /// <summary>
    /// 界面显示的来源或拟合通道与已有样本的来源不一致：显示的是另一个来源（换了会话、切到别的有数据的来源），
    /// 或同一来源下改了拟合通道。当前来源没有可显示的通道（已断开、未选会话）时不拦截，它不描述别的样本。
    /// </summary>
    public bool FittingMapMismatch
    {
        get
        {
            if (_samplesSourceKey.Length == 0) return false;
            var current = CurrentFittingSourceKey();
            if (current.Length == 0) return false;
            if (current != _samplesSourceKey) return true;
            return _samplesMap != null && !SelectedFittingMap().SequenceEqual(_samplesMap);
        }
    }

    /// <summary>已有样本来自另一个来源（相对当前显示）。</summary>
    private bool SamplesFromOtherSource
    {
        get
        {
            var current = CurrentFittingSourceKey();
            return _samplesSourceKey.Length > 0 && current.Length > 0 && current != _samplesSourceKey;
        }
    }

    private string CurrentFittingSourceKey()
    {
        if (DataSource == CalibrationDataSource.File) return FileSourceKey;
        if (DataSource == CalibrationDataSource.Session)
            return _loadedSession is { } session ? "session\u0001" + session.Id : "";
        var (names, units) = FittingSourceLayout();
        return names.Count == 0 ? "" : "live\u0001" + string.Join("\u0001", names) + "\u0002" + string.Join("\u0001", units);
    }

    /// <summary>记录现有样本的来源与所用通道（导入文件时没有通道映射）。</summary>
    private void RememberSamplesSource(int[]? map, string sourceKey)
    {
        _samplesMap = map;
        _samplesSourceKey = sourceKey;
        UpdateStepNavigation();
    }

    /// <summary>选项是按哪个实时布局建立的；停止采集后布局已变时据此重建。</summary>
    private string _optionsSourceKey = "";
    private bool _applyingSuggestion;
    private int _sessionLoadVersion;

    private int FittingGroups => SelectedSensorType == SensorType.DualTriaxialFluxgate ? 2 : 1;

    private int[] SelectedFittingMap() => FittingGroups == 2
        ? [FitX1, FitY1, FitZ1, FitX2, FitY2, FitZ2]
        : [FitX1, FitY1, FitZ1];

    private (IReadOnlyList<string> Names, IReadOnlyList<string> Units) FittingSourceLayout() => DataSource switch
    {
        CalibrationDataSource.Live when _dataBus.CurrentConnection != null && !_dataBus.IsPlaybackMode
            => (_dataBus.AcquisitionChannelNames, _dataBus.AcquisitionChannelUnits),
        CalibrationDataSource.Session when _loadedSession is { } session => (session.ChannelNames, session.ChannelUnits),
        _ => ([], []),
    };

    /// <summary>来源变化（新连接、选了会话、切换来源）：重建选项并按名称重新建议。</summary>
    private void RefreshFittingChannels()
    {
        var (names, units) = FittingSourceLayout();
        _optionsSourceKey = CurrentFittingSourceKey();
        FittingChannelOptions.Clear();
        if (names.Count == units.Count)
            for (int i = 0; i < names.Count; i++)
                FittingChannelOptions.Add(new FittingChannelOption(i, units[i].Length > 0 ? $"{names[i]} ({units[i]})" : names[i]));
        ApplyFittingSuggestion();
    }

    /// <summary>“自动识别”：按通道名称重新建议；已选会话时按建议重新读取。</summary>
    [RelayCommand]
    private void SuggestFittingChannels()
    {
        ApplyFittingSuggestion();
        ReloadSessionIfSelected();
    }

    private void ApplyFittingSuggestion()
    {
        var (names, units) = FittingSourceLayout();
        var map = names.Count == units.Count ? FittingChannelMap.Suggest(names, units, FittingGroups) : null;
        _applyingSuggestion = true;
        try
        {
            FitX1 = map?[0] ?? -1; FitY1 = map?[1] ?? -1; FitZ1 = map?[2] ?? -1;
            FitX2 = map is { Length: 6 } ? map[3] : -1;
            FitY2 = map is { Length: 6 } ? map[4] : -1;
            FitZ2 = map is { Length: 6 } ? map[5] : -1;
        }
        finally { _applyingSuggestion = false; }
        UpdateFittingChannelHint();
        UpdateStepNavigation();
    }

    partial void OnFitX1Changed(int value) => OnFittingChannelsEdited();
    partial void OnFitY1Changed(int value) => OnFittingChannelsEdited();
    partial void OnFitZ1Changed(int value) => OnFittingChannelsEdited();
    partial void OnFitX2Changed(int value) => OnFittingChannelsEdited();
    partial void OnFitY2Changed(int value) => OnFittingChannelsEdited();
    partial void OnFitZ2Changed(int value) => OnFittingChannelsEdited();

    /// <summary>用户改了某个下拉框：更新提示；已选会话且选择有效时按新的通道重新读取。</summary>
    private void OnFittingChannelsEdited()
    {
        if (_applyingSuggestion) return;
        UpdateFittingChannelHint();
        UpdateStepNavigation();
        ReloadSessionIfSelected();
    }

    private void ReloadSessionIfSelected()
    {
        if (IsFittingChannelValid && DataSource == CalibrationDataSource.Session && _loadedSession is { } session && !IsCollecting)
            _ = LoadSessionDataAsync(session);
    }

    private void UpdateFittingChannelHint()
    {
        var (names, units) = FittingSourceLayout();
        if (names.Count == 0)
        {
            IsFittingChannelValid = false;
            FittingChannelHint = DataSource == CalibrationDataSource.Session
                ? "选择会话后，按会话的通道选择 X、Y、Z。"
                : "连接设备后，按当前协议的通道选择 X、Y、Z。";
            return;
        }
        try
        {
            var unit = FittingChannelMap.Validate(SelectedFittingMap(), units, FittingGroups);
            IsFittingChannelValid = true;
            FittingChannelHint = $"单位 {unit}";
        }
        catch (ArgumentException ex)
        {
            IsFittingChannelValid = false;
            FittingChannelHint = ex.Message;
        }
    }

    // 手动模式：保留最近 10 条读数用于记录点时求均值。
    // 读数在接收线程到达，记录点、统计和导入在界面线程进行：样本列表、最近读数和原始 CSV 都在 _sampleLock 内访问。
    // 接收线程只入队并用 BeginInvoke 通知界面，不能同步等待界面线程（接收端在自己的锁内回调）。
    private readonly Queue<MagnetometerReading> _recentReadings = new(10);
    private readonly object _sampleLock = new();
    private readonly List<double[]> _pendingUiSamples = new();
    private int _uiFlushPending;
    private const int RecentBufferSize = 10;
    public const int ManualPointTarget = 48;
    private long _lastLiveValuesTicks;

    // raw CSV 写入
    private StreamWriter? _rawWriter;
    private string? _rawFilePath;
    private int _rawPointIndex;

    public OrthogonalityCalibrationViewModel(
        IOrthogonalityService orthogonalityService,
        ICalibrationRepository calibrationRepository,
        DataBus dataBus,
        IDataStorageService storageService)
    {
        _orthogonalityService = orthogonalityService;
        _calibrationRepository = calibrationRepository;
        _dataBus = dataBus;
        _storageService = storageService;

        ProfileName = $"正交度校正_{DateTime.Now:yyyyMMdd_HHmmss}";
        UpdateStepNavigation();

        // 订阅链路条上的"记录当前点"按钮（任何页面都能记录）
        _dataBus.ManualOrthoRecordRequested += OnManualOrthoRecordRequested;

        _isDeviceConnected = dataBus.CurrentConnection != null;
        _dataBus.ConnectionChanged += OnConnectionChanged;
        RefreshFittingChannels();

        // 已保存配置延迟加载
    }

    /// <summary>实时数据来源需要已连接的设备；未连接时向导给出提示。</summary>
    [ObservableProperty]
    private bool _isDeviceConnected;

    private void OnConnectionChanged(Core.Communication.IDeviceConnection? connection)
    {
        RunOnUi(() =>
        {
            IsDeviceConnected = connection != null;
            // 采集中途换了连接由读数回调发现并停止；这里只更新可选通道。
            if (DataSource == CalibrationDataSource.Live && !IsCollecting) RefreshFittingChannels();
        });
    }

    private void OnManualOrthoRecordRequested()
    {
        if (SelectedMode == CalibrationCollectionMode.Manual48 && IsCollecting)
            RecordCurrentPointCommand.Execute(null);
    }

    private bool _isLoaded;
    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded) return;
        _isLoaded = true;
        await LoadSavedProfilesAsync();
    }

    // ========== 已保存配置管理 ==========

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

    [RelayCommand]
    private async Task LoadSavedProfilesAsync()
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
        var confirm = System.Windows.MessageBox.Show(
            $"确定要删除正交度配置“{profile.Name}”吗？此操作不能撤销。",
            "确认删除", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;
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

    // ========== 步骤控制 (共 4 步) ==========

    [ObservableProperty]
    private int _currentStep = 1;

    [ObservableProperty]
    private bool _canGoNext;

    [ObservableProperty]
    private bool _canGoBack;

    [RelayCommand]
    private void NextStep()
    {
        if (CurrentStep >= 4) return;

        // 离开 Step 2 时自动停止采集
        if (CurrentStep == 2 && IsCollecting)
        {
            StopCollecting();
        }

        // Step 2 → Step 3：执行校验（仅展示警告，不阻止）
        if (CurrentStep == 2)
        {
            var validation = CalibrationDataValidator.Validate(SnapshotSamples(), _collectedUnit);
            DataValidation = validation;
            HasValidationWarnings = validation.Warnings.Count > 0;

            if (validation.Warnings.Count > 0)
            {
                ValidationStatusText = string.Join("\n", validation.Warnings);
            }
        }

        CurrentStep++;
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep <= 1) return;
        CurrentStep--;
    }

    partial void OnCurrentStepChanged(int value)
    {
        UpdateStepNavigation();
    }

    private void UpdateStepNavigation()
    {
        CanGoBack = CurrentStep > 1;
        CanGoNext = CurrentStep switch
        {
            1 => SelectedSensorType == SensorType.TriaxialFluxgate
                 || SelectedSensorType == SensorType.DualTriaxialFluxgate,
            2 => CollectedSampleCount >= 3 && !IsCollecting && !FittingMapMismatch,
            3 => SelectedSensorType == SensorType.DualTriaxialFluxgate
                ? (CalculationResult?.Success == true && SecondCalculationResult?.Success == true)
                : CalculationResult?.Success == true,
            4 => false, // 最后一步无下一步
            _ => false
        };
        OnPropertyChanged(nameof(StepGateText));
    }

    /// <summary>底部说明：进入下一步需要满足什么条件，当前是否满足。</summary>
    public string StepGateText => CurrentStep switch
    {
        1 => CanGoNext ? "已选择传感器类型" : "请选择单三轴或双三轴磁通门",
        2 when IsCollecting => "先结束采集，再进入下一步",
        2 when SamplesFromOtherSource => "已有样本来自另一个数据来源：切回该来源，或在当前来源重新采集 / 加载",
        2 when FittingMapMismatch => "拟合通道已更改，与已有样本不一致：改回原来的通道，或重新采集 / 加载",
        2 => CollectedSampleCount >= 3 ? $"至少需要 3 个样本，已有 {CollectedSampleCount:N0} 个" : $"至少需要 3 个样本，当前 {CollectedSampleCount:N0} 个",
        3 when CanGoNext => "计算完成，可以保存",
        3 when IsDualSensor => "两组都计算成功后可继续",
        3 => "计算成功后可继续",
        _ => "",
    };

    public bool IsDualSensor => SelectedSensorType == SensorType.DualTriaxialFluxgate;

    /// <summary>步骤条右侧的摘要，例如“双三轴磁通门 · FG-A-0217”。</summary>
    public string SensorSummary => SelectedSensorType switch
    {
        SensorType.TriaxialFluxgate => "单三轴磁通门",
        SensorType.DualTriaxialFluxgate => "双三轴磁通门",
        _ => "未选择传感器",
    } + (string.IsNullOrWhiteSpace(SensorSerial) ? "" : $" · {SensorSerial}");

    partial void OnSensorSerialChanged(string value) => OnPropertyChanged(nameof(SensorSummary));

    // ========== Step 1 - 选择传感器 ==========

    [ObservableProperty]
    private SensorType _selectedSensorType = SensorType.TriaxialFluxgate;

    [ObservableProperty]
    private string _sensorSerial = string.Empty;

    [ObservableProperty]
    private double? _referenceFieldStrength;

    public SensorType[] AvailableSensorTypes { get; } =
    [
        SensorType.TriaxialFluxgate,
        SensorType.DualTriaxialFluxgate
    ];

    partial void OnSelectedSensorTypeChanged(SensorType value)
    {
        OnPropertyChanged(nameof(IsDualSensor));
        OnPropertyChanged(nameof(SensorSummary));
        if (value != SensorType.DualTriaxialFluxgate) SelectedResultGroup = 0;
        if (!IsCollecting) ApplyFittingSuggestion();
        UpdateStepNavigation();
    }

    // ========== Step 2 - 采集/导入数据 ==========

    [ObservableProperty]
    private bool _isCollecting;

    [ObservableProperty]
    private int _collectedSampleCount;

    [ObservableProperty]
    private double _sphericityCoverage;

    [ObservableProperty]
    private string _collectionStatus = "等待开始采集或导入数据";

    [ObservableProperty]
    private CalibrationDataValidation? _dataValidation;

    [ObservableProperty]
    private string _validationStatusText = string.Empty;

    [ObservableProperty]
    private bool _hasValidationWarnings;

    public ObservableCollection<double[]> CollectedData { get; } = new();

    /// <summary>采集模式：连续 / 手动 48 点</summary>
    [ObservableProperty]
    private CalibrationCollectionMode _selectedMode = CalibrationCollectionMode.Continuous;

    /// <summary>是否处于手动模式（供 XAML DataTrigger 使用）</summary>
    public bool IsManualMode => SelectedMode == CalibrationCollectionMode.Manual48;

    /// <summary>手动采集的缓冲就绪状态（链路条与本页“记录当前点”按钮共用）。</summary>
    public ManualOrthoState ManualState => _dataBus.ManualOrthoState;
    partial void OnSelectedModeChanged(CalibrationCollectionMode value)
    {
        OnPropertyChanged(nameof(IsManualMode));
        OnPropertyChanged(nameof(IsContinuousMode));
    }
    public bool IsContinuousMode => SelectedMode == CalibrationCollectionMode.Continuous;

    /// <summary>数据来源只决定第 2 步显示哪组操作；三种来源都填充同一份样本。</summary>
    [ObservableProperty]
    private CalibrationDataSource _dataSource = CalibrationDataSource.Live;

    partial void OnDataSourceChanged(CalibrationDataSource value)
    {
        OnPropertyChanged(nameof(ShowsFittingChannels));
        RefreshFittingChannels();
    }

    /// <summary>手动 48 点的格子：已记录为 "on"，下一个为 "next"，其余为空。</summary>
    public IReadOnlyList<string> ManualPointSlots => Enumerable.Range(0, ManualPointTarget)
        .Select(i => i < CollectedSampleCount ? "on" : i == CollectedSampleCount && IsCollecting ? "next" : "")
        .ToArray();

    /// <summary>最近一条读数的前三个（双三轴为六个）通道值，约每 0.2 秒更新一次。</summary>
    [ObservableProperty]
    private string _liveValuesText = "—";

    [RelayCommand]
    private void StartCollecting()
    {
        // 拟合数据的单位来自当前连接的协议通道；未连接、回放中或通道布局不符时不开始。
        try
        {
            if (_dataBus.CurrentConnection == null || _dataBus.IsPlaybackMode)
                throw new ArgumentException("请先连接设备，再按当前协议通道单位采集拟合数据。");
            var names = _dataBus.AcquisitionChannelNames;
            var units = _dataBus.AcquisitionChannelUnits;
            var map = SelectedFittingMap();
            var unit = FittingChannelMap.Validate(map, units, FittingGroups);
            if (names.Count != units.Count)
                throw new ArgumentException("当前连接的通道名称与单位数量不一致，不能采集拟合数据。");
            SetCollectedUnit(unit, map.Length);
            _collectedMap = map;
            _collectedLabels = map.Select(i => names[i]).ToArray();
            _collectedLayoutNames = names;
            _collectedLayoutUnits = units;
            RememberSamplesSource(map, CurrentFittingSourceKey());
        }
        catch (Exception ex) { CollectionStatus = ex.Message; return; }
        lock (_sampleLock)
        {
            _collectedData.Clear();
            _collectedDataSecondGroup.Clear();
        }
        CollectedData.Clear();
        lock (_sampleLock) _recentReadings.Clear();
        CollectedSampleCount = 0;
        SphericityCoverage = 0;
        LiveValuesText = "—";
        CollectionStatus = SelectedMode == CalibrationCollectionMode.Manual48
            ? "手动模式：把传感器转到一个稳定方位，点「记录当前点」（这里或顶部链路条都可以）。建议 48 个方位在球面上均匀分布。"
            : "采集中（连续模式）：缓慢旋转传感器，尽量覆盖所有方位。";
        IsCollecting = true;

        DataValidation = null;
        ValidationStatusText = string.Empty;
        HasValidationWarnings = false;

        OpenRawCsv(SelectedMode);

        if (SelectedMode == CalibrationCollectionMode.Manual48)
        {
            lock (_sampleLock) _manualPublishing = true;
            PublishManualState(0, "等待数据缓冲...", false);
        }

        _dataBus.ReadingReceived += OnCalibrationDataReceived;
        UpdateStepNavigation();
    }

    private void OnCalibrationDataReceived(MagnetometerReading reading)
    {
        // 连接的通道布局与开始采集时不同，或读数通道数与协议不符：不混入这批数据，在界面线程停止采集。
        var generation = Interlocked.Read(ref _collectedGeneration);
        var map = _collectedMap;
        string? unitProblem = null;
        if (!SameLayout(_dataBus.AcquisitionChannelUnits, _collectedLayoutUnits)
            || !SameLayout(_dataBus.AcquisitionChannelNames, _collectedLayoutNames))
            unitProblem = "连接的通道布局已改变，已停止拟合数据采集，请重新选择拟合通道后开始。";
        else if (reading.ChannelValues.Length != _collectedLayoutUnits.Count || map.Length == 0)
            unitProblem = "读数的通道数与协议不一致，已停止拟合数据采集。";
        if (unitProblem != null)
        {
            RunOnUi(() =>
            {
                if (generation != Interlocked.Read(ref _collectedGeneration) || !IsCollecting) return;
                StopCollecting();
                CollectionStatus = unitProblem;
            });
            return;
        }

        // 任何模式都先维护"最近 10 条"队列
        int buffered;
        lock (_sampleLock)
        {
            if (generation != Interlocked.Read(ref _collectedGeneration)) return;
            if (_recentReadings.Count >= RecentBufferSize)
                _recentReadings.Dequeue();
            _recentReadings.Enqueue(reading);
            buffered = _recentReadings.Count;
        }
        PublishLiveValues(reading);

        if (SelectedMode == CalibrationCollectionMode.Manual48)
        {
            // 手动模式：仅保持队列，不入 _collectedData；更新缓冲就绪状态
            bool enough = buffered >= RecentBufferSize;
            PublishManualState(_collectedData.Count,
                enough ? "缓冲就绪，可以记录" : $"缓冲中 ({buffered}/{RecentBufferSize})",
                enough, generation);
            return;
        }

        // 连续模式：每条读数取所选的三个（双三轴为六个）通道作为样本
        bool dual = map.Length == 6;
        var v = Pick(reading.ChannelValues, map);
        var sample1 = new[] { v[0], v[1], v[2] };
        lock (_sampleLock)
        {
            if (!IsCollecting || generation != Interlocked.Read(ref _collectedGeneration)) return;
            _collectedData.Add(sample1);
            if (dual) _collectedDataSecondGroup.Add(new[] { v[3], v[4], v[5] });
            _pendingUiSamples.Add(sample1);
            AppendRawPoint(reading.Timestamp, v);
        }

        if (Interlocked.Exchange(ref _uiFlushPending, 1) == 0)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) FlushPendingSamples();
            else dispatcher.BeginInvoke(FlushPendingSamples, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private static double[] Pick(double[] values, int[] map)
    {
        var picked = new double[map.Length];
        for (int i = 0; i < map.Length; i++) picked[i] = values[map[i]];
        return picked;
    }

    /// <summary>通道布局是否未变：同一份冻结列表，或内容相同（同一协议重新连接）。</summary>
    private static bool SameLayout(IReadOnlyList<string> current, IReadOnlyList<string> frozen) =>
        ReferenceEquals(current, frozen) || current.SequenceEqual(frozen);

    /// <summary>界面线程：把接收线程新增的样本并入显示集合，并按 50 个样本一次更新覆盖度与校验。</summary>
    private void FlushPendingSamples()
    {
        double[][] added;
        int count;
        lock (_sampleLock)
        {
            added = _pendingUiSamples.ToArray();
            _pendingUiSamples.Clear();
            count = _collectedData.Count;
            Interlocked.Exchange(ref _uiFlushPending, 0);
        }
        if (added.Length == 0) return;
        var before = CollectedSampleCount;
        foreach (var sample in added) CollectedData.Add(sample);
        CollectedSampleCount = count;
        if (before / 50 != count / 50)
        {
            UpdateCoverageEstimate();
            RunDataValidation();
        }
        UpdateStepNavigation();
    }

    private List<double[]> SnapshotSamples()
    {
        lock (_sampleLock) return _collectedData.ToList();
    }

    private void UpdateCoverageEstimate()
    {
        const int nLon = 12;
        const int nLat = 6;
        var covered = new bool[nLon, nLat];
        var samples = SnapshotSamples();

        double cx = 0, cy = 0, cz = 0;
        int n = samples.Count;
        if (n == 0) { SphericityCoverage = 0; return; }
        for (int i = 0; i < n; i++)
        {
            cx += samples[i][0];
            cy += samples[i][1];
            cz += samples[i][2];
        }
        cx /= n; cy /= n; cz /= n;

        for (int i = 0; i < n; i++)
        {
            double dx = samples[i][0] - cx;
            double dy = samples[i][1] - cy;
            double dz = samples[i][2] - cz;
            double r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (r < 1e-10) continue;

            double lat = Math.Asin(Math.Clamp(dz / r, -1.0, 1.0));
            double lon = Math.Atan2(dy, dx);

            int lonIdx = (int)((lon + Math.PI) / (2 * Math.PI) * nLon);
            if (lonIdx >= nLon) lonIdx = nLon - 1;
            int latIdx = (int)((lat + Math.PI / 2) / Math.PI * nLat);
            if (latIdx >= nLat) latIdx = nLat - 1;

            covered[lonIdx, latIdx] = true;
        }

        int total = nLon * nLat;
        int count = 0;
        foreach (bool c in covered)
            if (c) count++;

        SphericityCoverage = (double)count / total * 100.0;
    }

    [RelayCommand]
    private void StopCollecting()
    {
        _dataBus.ReadingReceived -= OnCalibrationDataReceived;
        IsCollecting = false;
        FlushPendingSamples();
        // 采集中途重连了别的协议时选项没有刷新；停止后若实时布局已变，按新布局重建，免得下拉框仍显示旧通道名。
        if (DataSource == CalibrationDataSource.Live && CurrentFittingSourceKey() != _optionsSourceKey)
            RefreshFittingChannels();
        CollectionStatus = $"采集完成，共 {CollectedSampleCount} 个样本";

        CloseRawCsv();
        // 退订不会取消已在进行的读数回调：与回调用同一把锁结束发布，回调不会在此之后把状态改回“采集中”。
        lock (_sampleLock)
        {
            _manualPublishing = false;
            _dataBus.ManualOrthoState.Update(false, 0, null, "", false);
        }

        UpdateCoverageEstimate();
        RunDataValidation();
        UpdateStepNavigation();
    }

    /// <summary>
    /// 手动模式下，把最近 10 条读数对每通道求均值，作为 1 个点加入 _collectedData。
    /// </summary>
    [RelayCommand]
    private void RecordCurrentPoint()
    {
        if (SelectedMode != CalibrationCollectionMode.Manual48 || !IsCollecting) return;

        var map = _collectedMap;
        bool dual = map.Length == 6;
        int n = map.Length;
        var sums = new double[n];
        int validCount = 0, buffered, count;
        double[] sample1;
        lock (_sampleLock)
        {
            buffered = _recentReadings.Count;
            if (buffered == 0)
            {
                CollectionStatus = "还没有收到读数：确认设备已连接并在输出数据";
                return;
            }
            // 每个点承诺为最近 RecentBufferSize 条读数的均值；不足时不记录（页面按钮、链路条按钮和快捷键一致）。
            if (buffered < RecentBufferSize)
            {
                CollectionStatus = $"缓冲中（{buffered}/{RecentBufferSize}），收满 {RecentBufferSize} 条读数后再记录";
                return;
            }

            // 对每通道求均值
            DateTime lastTs = DateTime.Now;
            foreach (var r in _recentReadings)
            {
                if (r.ChannelValues.Length != _collectedLayoutUnits.Count) continue;
                for (int i = 0; i < n; i++) sums[i] += r.ChannelValues[map[i]];
                validCount++;
                lastTs = r.Timestamp;
            }
            if (validCount < RecentBufferSize)
            {
                CollectionStatus = $"最近 {RecentBufferSize} 条读数中只有 {validCount} 条与协议通道数一致，不能记录";
                return;
            }
            var avg = new double[n];
            for (int i = 0; i < n; i++) avg[i] = sums[i] / validCount;

            sample1 = new[] { avg[0], avg[1], avg[2] };
            _collectedData.Add(sample1);
            if (dual) _collectedDataSecondGroup.Add(new[] { avg[3], avg[4], avg[5] });
            count = _collectedData.Count;

            // 写 raw CSV
            AppendRawPoint(lastTs, avg);
        }

        CollectedData.Add(sample1);
        CollectedSampleCount = count;
        CollectionStatus = $"已记录第 {count} 点（最近 {validCount} 条读数的均值）";
        UpdateManualState(buffered);
        if (count % 6 == 0)
        {
            UpdateCoverageEstimate();
            RunDataValidation();
        }
    }

    private bool CanEditManualPoints() => IsManualMode && IsCollecting && CollectedSampleCount > 0;

    /// <summary>撤销最后一个手动点。原始 CSV 只追加一行注释，不改写已记录的行。</summary>
    [RelayCommand(CanExecute = nameof(CanEditManualPoints))]
    private void UndoLastPoint()
    {
        int count;
        lock (_sampleLock)
        {
            if (_collectedData.Count == 0) return;
            _collectedData.RemoveAt(_collectedData.Count - 1);
            if (_collectedDataSecondGroup.Count > _collectedData.Count)
                _collectedDataSecondGroup.RemoveAt(_collectedDataSecondGroup.Count - 1);
            count = _collectedData.Count;
            AppendRawComment($"已撤销第 {count + 1} 点");
        }
        if (CollectedData.Count > 0) CollectedData.RemoveAt(CollectedData.Count - 1);
        CollectedSampleCount = count;
        CollectionStatus = $"已撤销第 {count + 1} 点";
        UpdateManualState(null);
        UpdateCoverageEstimate();
        RunDataValidation();
    }

    /// <summary>清空已记录的手动点，继续采集。原始 CSV 追加注释说明之前的点已作废。</summary>
    [RelayCommand(CanExecute = nameof(CanEditManualPoints))]
    private void ClearManualPoints()
    {
        int removed;
        lock (_sampleLock)
        {
            removed = _collectedData.Count;
            _collectedData.Clear();
            _collectedDataSecondGroup.Clear();
            AppendRawComment($"已清空之前的 {removed} 点，重新记录");
        }
        CollectedData.Clear();
        CollectedSampleCount = 0;
        SphericityCoverage = 0;
        DataValidation = null;
        HasValidationWarnings = false;
        ValidationStatusText = string.Empty;
        CollectionStatus = $"已清空 {removed} 点，可以重新记录";
        UpdateManualState(null);
    }

    private void UpdateManualState(int? buffered)
    {
        int bufferCount = buffered ?? RecentBufferedCount();
        PublishManualState(CollectedSampleCount,
            CollectedSampleCount >= ManualPointTarget ? $"已达 {ManualPointTarget} 点，可以结束采集" : $"已记录 {CollectedSampleCount} 点",
            bufferCount >= RecentBufferSize);
    }

    /// <summary>手动采集进行中才发布的标志；与 <see cref="StopCollecting"/> 共用 _sampleLock。</summary>
    private bool _manualPublishing;

    /// <summary>
    /// 只在手动采集进行中（且仍是同一批数据）发布“采集中”状态。检查与发布在同一把锁内，
    /// 停止采集之后，在途的读数回调或撤销 / 清空操作都不能把链路条状态改回采集中。
    /// 订阅者只有界面绑定，跨线程通知由绑定异步转到界面线程，不会在锁内等待界面。
    /// </summary>
    private void PublishManualState(int points, string status, bool enoughBuffer, long? generation = null)
    {
        lock (_sampleLock)
        {
            if (!_manualPublishing) return;
            if (generation is { } g && g != Interlocked.Read(ref _collectedGeneration)) return;
            _dataBus.ManualOrthoState.Update(true, points, _rawFilePath, status, enoughBuffer);
        }
    }

    private int RecentBufferedCount()
    {
        lock (_sampleLock) return _recentReadings.Count;
    }

    /// <summary>界面显示的实时值，限制在约 5 次/秒，避免每条读数都排一次界面更新。</summary>
    private void PublishLiveValues(MagnetometerReading reading)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastLiveValuesTicks);
        if (now - last < TimeSpan.TicksPerMillisecond * 200 || Interlocked.CompareExchange(ref _lastLiveValuesTicks, now, last) != last) return;
        var map = _collectedMap;
        var labels = _collectedLabels;
        if (map.Length == 0 || reading.ChannelValues.Length <= map.Max()) return;
        var text = string.Join("   ", map.Select((channel, i) =>
            $"{(i < labels.Length ? labels[i] : "XYZ"[i % 3].ToString())} {reading.ChannelValues[channel]:0.0}"));
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) LiveValuesText = text;
        else dispatcher.BeginInvoke(() => { if (IsCollecting) LiveValuesText = text; });
    }

    partial void OnCollectedSampleCountChanged(int value)
    {
        OnPropertyChanged(nameof(ManualPointSlots));
        UndoLastPointCommand.NotifyCanExecuteChanged();
        ClearManualPointsCommand.NotifyCanExecuteChanged();
        UpdateStepNavigation();
    }

    // ---- raw CSV 写入 ----

    private static string RawDataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MagnetometerSystem", "statistics", "calibration_raw");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private void OpenRawCsv(CalibrationCollectionMode mode)
    {
        if (_rawWriter != null) return;
        var safeName = string.Join("_",
            (ProfileName ?? "calib").Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "calib";
        _rawFilePath = Path.Combine(RawDataDir,
            $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}_raw.csv");
        _rawWriter = new StreamWriter(_rawFilePath, append: false, new System.Text.UTF8Encoding(true));
        _rawPointIndex = 0;
        _rawWriter.WriteLine($"# Calibration Profile : {ProfileName}");
        _rawWriter.WriteLine($"# Sensor Type         : {SelectedSensorType}");
        _rawWriter.WriteLine($"# Unit                : {_collectedUnit}");
        _rawWriter.WriteLine($"# Collection Mode     : {mode}");
        _rawWriter.WriteLine($"# Recorded At         : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var channelNames = _collectedChannelCount == 6
            ? "X1,Y1,Z1,X2,Y2,Z2" : "X,Y,Z";
        // 列名按轴排列；对应的协议通道写在注释里（名称中的换行替换为空格）。
        var axes = channelNames.Split(',');
        _rawWriter.WriteLine("# Source Channels     : " + string.Join(", ", _collectedLabels.Select((label, i) =>
            $"{(i < axes.Length ? axes[i] : "?")}={label.Replace('\r', ' ').Replace('\n', ' ')}")));
        _rawWriter.WriteLine("point_index,timestamp," + channelNames);
        _rawWriter.Flush();
    }

    /// <summary>写一行原始点：<paramref name="values"/> 已按所选通道排成 X、Y、Z[、X2、Y2、Z2]。</summary>
    private void AppendRawPoint(DateTime timestamp, double[] values)
    {
        if (_rawWriter == null) return;
        _rawPointIndex++;
        var sb = new System.Text.StringBuilder();
        sb.Append(_rawPointIndex).Append(',');
        sb.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        foreach (var value in values)
        {
            sb.Append(',');
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }
        _rawWriter.WriteLine(sb.ToString());
        _rawWriter.Flush();
    }

    /// <summary>在原始 CSV 中追加注释行（# 开头），记录撤销 / 清空等操作。调用方持有 _sampleLock。</summary>
    private void AppendRawComment(string text)
    {
        if (_rawWriter == null) return;
        _rawWriter.WriteLine($"# {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}");
        _rawWriter.Flush();
    }

    private void CloseRawCsv()
    {
        lock (_sampleLock)
        {
            try { _rawWriter?.Flush(); } catch { }
            try { _rawWriter?.Dispose(); } catch { }
            _rawWriter = null;
        }
    }

    [RelayCommand]
    private void ImportFromFile()
    {
        var importUnit = OrthogonalityParams.CanonicalUnit(FittingUnit);
        if (importUnit.Length == 0)
        {
            CollectionStatus = "请先明确选择 CSV 数值的磁场单位；不会自动猜测或换算。";
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "导入三轴校正数据",
            Filter = "CSV 文件 (*.csv)|*.csv|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            DefaultExt = ".csv"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var lines = File.ReadAllLines(dialog.FileName);
            var importedData = new List<double[]>();
            var importedDataSecond = new List<double[]>();
            int skippedLines = 0;
            bool dual = SelectedSensorType == SensorType.DualTriaxialFluxgate;
            int requiredCols = dual ? 6 : 3;

            // 第一行特判 header：所有列都无法 parse 成 double ⇒ 是 header
            int startIdx = 0;
            int[]? columnMap = null; // 长度 = requiredCols，映射到具体列索引
            if (lines.Length > 0)
            {
                var firstParts = SplitCsvLine(lines[0]);
                if (firstParts.Length >= requiredCols && !firstParts.Any(p => TryParseDouble(p, out _)))
                {
                    // 是 header，尝试按列名定位
                    columnMap = BuildColumnMap(firstParts, dual);
                    startIdx = 1;
                }
            }

            for (int i = startIdx; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = SplitCsvLine(line);

                // 有 header 列名映射时直接按 map 取
                if (columnMap != null)
                {
                    if (!TryExtractByMap(parts, columnMap, 0, 3, out var triple)) { skippedLines++; continue; }
                    importedData.Add(triple);
                    if (dual)
                    {
                        if (TryExtractByMap(parts, columnMap, 3, 3, out var triple2))
                            importedDataSecond.Add(triple2);
                    }
                    continue;
                }

                // 无 header：先尝试前 3 列；若前 3 列含非 double（可能第一列是时间戳）则跳过第一列试 1..3
                if (parts.Length < requiredCols) { skippedLines++; continue; }

                int offset = 0;
                if (!TryParseDouble(parts[0], out _) && parts.Length >= requiredCols + 1) offset = 1;

                if (parts.Length < offset + requiredCols) { skippedLines++; continue; }

                if (TryParseDouble(parts[offset], out var bx) &&
                    TryParseDouble(parts[offset + 1], out var by) &&
                    TryParseDouble(parts[offset + 2], out var bz))
                {
                    importedData.Add(new[] { bx, by, bz });

                    if (dual && parts.Length >= offset + 6 &&
                        TryParseDouble(parts[offset + 3], out var bx2) &&
                        TryParseDouble(parts[offset + 4], out var by2) &&
                        TryParseDouble(parts[offset + 5], out var bz2))
                    {
                        importedDataSecond.Add(new[] { bx2, by2, bz2 });
                    }
                }
                else
                {
                    skippedLines++;
                }
            }

            if (importedData.Count == 0)
            {
                CollectionStatus = "导入失败：文件中未找到有效的三轴数据。点击 [格式说明] 查看支持的格式。";
                return;
            }

            ReplaceSamples(importedData, importedDataSecond, importUnit, requiredCols);
            RememberSamplesSource(null, FileSourceKey);

            string skipInfo = skippedLines > 0 ? $"（跳过 {skippedLines} 行）" : "";
            CollectionStatus = $"已从文件导入 {importedData.Count} 个样本{skipInfo}";

            UpdateCoverageEstimate();
            RunDataValidation();
            UpdateStepNavigation();
        }
        catch (Exception ex)
        {
            CollectionStatus = $"导入失败：{ex.Message}";
        }
    }

    /// <summary>用导入的样本替换当前样本（文件或会话来源）。只在未采集时调用。</summary>
    private void ReplaceSamples(List<double[]> first, List<double[]> second, string unit, int channelCount)
    {
        if (IsCollecting) StopCollecting();
        SetCollectedUnit(unit, channelCount);
        _samplesMap = null;   // 导入文件有自己的列映射；会话导入由调用方随后记录所用通道
        _samplesSourceKey = "";
        lock (_sampleLock)
        {
            _collectedData.Clear();
            _collectedDataSecondGroup.Clear();
            _collectedData.AddRange(first);
            _collectedDataSecondGroup.AddRange(second);
        }
        CollectedData.Clear();
        foreach (var d in first) CollectedData.Add(d);
        CollectedSampleCount = first.Count;
    }

    private void RunDataValidation()
    {
        var samples = SnapshotSamples();
        if (samples.Count < 3) return;

        var validation = CalibrationDataValidator.Validate(samples, _collectedUnit);
        DataValidation = validation;
        HasValidationWarnings = validation.Warnings.Count > 0;

        if (validation.Warnings.Count > 0)
        {
            ValidationStatusText = string.Join("\n", validation.Warnings);
        }
        else
        {
            ValidationStatusText = $"数据质量良好（总场 {validation.MeanTotalField:G6} {_collectedUnit}，覆盖度 {validation.SphericityCoverage:P0}）";
        }
    }

    partial void OnIsCollectingChanged(bool value)
    {
        OnPropertyChanged(nameof(ManualPointSlots));
        UndoLastPointCommand.NotifyCanExecuteChanged();
        ClearManualPointsCommand.NotifyCanExecuteChanged();
        if (!value) LiveValuesText = "—";
        UpdateStepNavigation();
    }

    // ========== Step 3 - 计算与结果 ==========

    [ObservableProperty]
    private bool _isCalculating;

    [ObservableProperty]
    private string _calculationStatus = "尚未计算";

    [ObservableProperty]
    private OrthogonalityResult? _calculationResult;

    [RelayCommand]
    private async Task RunCalculation()
    {
        IsCalculating = true;
        CalculationStatus = "计算中...";
        // 每次重新计算都清掉旧结果，失败时不能留下上一次的矩阵。
        CalculationResult = null;
        SecondCalculationResult = null;
        FirstGroupResult = SecondGroupResult = null;

        try
        {
            var generation = Interlocked.Read(ref _collectedGeneration);
            var unit = _collectedUnit;
            if (unit.Length == 0) throw new ArgumentException("拟合数据单位未知，请重新采集或明确单位后导入。");
            var referenceField = ReferenceFieldStrength;
            List<double[]> first, second;
            lock (_sampleLock) { first = _collectedData.ToList(); second = _collectedDataSecondGroup.ToList(); }

            var rawData = ConvertToMatrix(first);
            var result = await Task.Run(() =>
                _orthogonalityService.Calculate(rawData, referenceField, unit));
            if (generation != Interlocked.Read(ref _collectedGeneration))
            {
                CalculationStatus = "拟合数据已更换，请重新计算。";
                return;
            }

            if (result.Success)
            {
                CalculationResult = result;
                FirstGroupResult = new OrthoGroupResult(result, rawData, ApplyAll(result, rawData));
                CalculationStatus = "计算完成";
            }
            else
            {
                CalculationStatus = $"计算失败: {result.ErrorMessage}";
            }

            // 双三轴：第二组独立计算
            if (_collectedChannelCount == 6)
            {
                if (second.Count < 3)
                {
                    CalculationStatus += "；第二组样本不足 3 个，未计算";
                }
                else
                {
                    var rawData2 = ConvertToMatrix(second);
                    var result2 = await Task.Run(() =>
                        _orthogonalityService.Calculate(rawData2, referenceField, unit));
                    if (generation != Interlocked.Read(ref _collectedGeneration))
                    {
                        CalculationStatus = "拟合数据已更换，请重新计算。";
                        return;
                    }
                    if (result2.Success)
                    {
                        SecondCalculationResult = result2;
                        SecondGroupResult = new OrthoGroupResult(result2, rawData2, ApplyAll(result2, rawData2));
                    }
                    else
                    {
                        CalculationStatus += $"；第二组计算失败: {result2.ErrorMessage}";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            CalculationStatus = $"计算异常: {ex.Message}";
        }
        finally
        {
            IsCalculating = false;
            UpdateStepNavigation();
        }
    }

    private static double[,] ApplyAll(OrthogonalityResult result, double[,] rawData)
    {
        var corrected = new double[rawData.GetLength(0), 3];
        for (int i = 0; i < rawData.GetLength(0); i++)
        {
            var c = result.Parameters.Apply(rawData[i, 0], rawData[i, 1], rawData[i, 2]);
            corrected[i, 0] = c[0]; corrected[i, 1] = c[1]; corrected[i, 2] = c[2];
        }
        return corrected;
    }

    private static double[,] ConvertToMatrix(List<double[]> data)
    {
        int n = data.Count;
        var matrix = new double[n, 3];
        for (int i = 0; i < n; i++)
        {
            matrix[i, 0] = data[i][0];
            matrix[i, 1] = data[i][1];
            matrix[i, 2] = data[i][2];
        }
        return matrix;
    }

    partial void OnCalculationResultChanged(OrthogonalityResult? value)
    {
        OnPropertyChanged(nameof(QualityRating));
        UpdateStepNavigation();
    }

    /// <summary>从任意线程排到界面线程执行（不等待）。</summary>
    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    partial void OnSecondCalculationResultChanged(OrthogonalityResult? value)
    {
        OnPropertyChanged(nameof(SecondQualityRating));
        UpdateStepNavigation();
    }

    [ObservableProperty]
    private OrthogonalityResult? _secondCalculationResult;

    /// <summary>第一组 / 第二组的结果显示数据；未计算或失败时为 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedResult))]
    private OrthoGroupResult? _firstGroupResult;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedResult))]
    private OrthoGroupResult? _secondGroupResult;

    /// <summary>结果页当前查看的组：0 = 第一组，1 = 第二组（仅双三轴）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayedResult))]
    private int _selectedResultGroup;

    public OrthoGroupResult? DisplayedResult => SelectedResultGroup == 1 ? SecondGroupResult : FirstGroupResult;

    partial void OnFirstGroupResultChanged(OrthoGroupResult? value)
    {
        OnPropertyChanged(nameof(VisualizationRawData));
        OnPropertyChanged(nameof(VisualizationCorrectedData));
    }

    /// <summary>第一组的可视化样本（未计算或换了拟合数据时为 null）。</summary>
    public double[,]? VisualizationRawData => FirstGroupResult?.RawData;
    public double[,]? VisualizationCorrectedData => FirstGroupResult?.CorrectedData;

    /// <summary>质量评级（第一组 / 第二组）：残差标准差折算到 nT 后分级。</summary>
    public string QualityRating => RateQuality(CalculationResult);
    public string SecondQualityRating => RateQuality(SecondCalculationResult);

    internal static double ResidualStdInNt(OrthogonalityResult? result)
    {
        if (result == null) return double.NaN;
        var scale = OrthogonalityParams.CanonicalUnit(result.Parameters.Unit) switch
        {
            "nT" => 1d, "uT" => 1e3, "mT" => 1e6, "T" => 1e9, _ => double.NaN
        };
        var residualNt = result.Quality.ResidualStd * scale;
        return double.IsFinite(residualNt) && residualNt >= 0 ? residualNt : double.NaN;
    }

    internal static string RateQuality(OrthogonalityResult? result)
    {
        if (result == null) return "—";
        var residualNt = ResidualStdInNt(result);
        if (double.IsNaN(residualNt)) return "未知";
        return residualNt switch { < 10 => "优秀", < 50 => "良好", < 200 => "一般", _ => "较差" };
    }


    // ========== Step 4 - 保存配置 ==========

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string _profileNotes = string.Empty;

    [ObservableProperty]
    private string _saveStatus = string.Empty;

    [RelayCommand]
    private async Task SaveProfileAsync()
    {
        if (CalculationResult?.Parameters == null)
        {
            SaveStatus = "无计算结果可保存";
            return;
        }

        if (string.IsNullOrWhiteSpace(ProfileName))
        {
            SaveStatus = "请输入配置名称";
            return;
        }

        try
        {
            SavedSecondProfile = null;
            // 防御性拷贝，避免直接修改 CalculationResult.Parameters 引用
            var src = CalculationResult.Parameters;
            var parameters = new OrthogonalityParams
            {
                Id = src.Id,
                Unit = src.Unit,
                Name = ProfileName,
                SensorSerial = SensorSerial,
                CreatedAt = src.CreatedAt,
                Offset = (double[])src.Offset.Clone(),
                CompensationMatrix = (double[])src.CompensationMatrix.Clone(),
                Notes = ProfileNotes,
                ResidualMean = CalculationResult.Quality?.ResidualMean,
                ResidualStd = CalculationResult.Quality?.ResidualStd,
                SampleCount = CalculationResult.Quality?.SampleCount,
            };

            // 持久化到数据库
            await _calibrationRepository.SaveOrthogonalityProfileAsync(parameters);
            SavedProfile = parameters;

            // Save second profile for DualTriaxial
            if (SecondCalculationResult?.Parameters != null)
            {
                var src2 = SecondCalculationResult.Parameters;
                var secondParameters = new OrthogonalityParams
                {
                    Id = src2.Id,
                    Unit = src2.Unit,
                    Name = $"{ProfileName}_第二组",
                    SensorSerial = SensorSerial,
                    CreatedAt = src2.CreatedAt,
                    Offset = (double[])src2.Offset.Clone(),
                    CompensationMatrix = (double[])src2.CompensationMatrix.Clone(),
                    Notes = ProfileNotes,
                    ResidualMean = SecondCalculationResult.Quality?.ResidualMean,
                    ResidualStd = SecondCalculationResult.Quality?.ResidualStd,
                    SampleCount = SecondCalculationResult.Quality?.SampleCount,
                };

                await _calibrationRepository.SaveOrthogonalityProfileAsync(secondParameters);
                SavedSecondProfile = secondParameters;
            }

            // 刷新已保存配置列表
            await LoadSavedProfilesAsync();

            // 保存校准记录到历史
            await SaveCalibrationRecordAsync(parameters);

            // 保存只写入配置库，不会自动作用于采集；在采集页“校正”面板选用后才影响曲线显示。
            SaveStatus = SavedSecondProfile is { } second
                ? $"已保存“{parameters.Name}”和“{second.Name}”到配置库。在采集页右侧「校正」中选用。"
                : $"已保存“{parameters.Name}”到配置库。在采集页右侧「校正」中选用。";
        }
        catch (Exception ex)
        {
            SaveStatus = $"保存失败: {ex.Message}";
        }
    }

    [ObservableProperty]
    private OrthogonalityParams? _savedProfile;

    [ObservableProperty]
    private OrthogonalityParams? _savedSecondProfile;

    private async Task SaveCalibrationRecordAsync(OrthogonalityParams parameters)
    {
        try
        {
            var matrixJson = System.Text.Json.JsonSerializer.Serialize(parameters.CompensationMatrix);
            var record = new OrthogonalityCalibrationRecord
            {
                DeviceId = SensorSerial,
                SessionId = $"calibration_{DateTime.Now:yyyyMMddHHmmss}",
                MatrixJson = matrixJson,
                CreatedAt = DateTime.Now,
                Operator = Environment.UserName,
                Notes = ProfileNotes
            };
            await _calibrationRepository.SaveOrthogonalityCalibrationAsync(record);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"保存校准记录失败: {ex.Message}");
        }
    }

    public void Cleanup()
    {
        Interlocked.Increment(ref _collectedGeneration);
        if (IsCollecting)
        {
            _dataBus.ReadingReceived -= OnCalibrationDataReceived;
            IsCollecting = false;
        }
        _dataBus.ManualOrthoRecordRequested -= OnManualOrthoRecordRequested;
        _dataBus.ConnectionChanged -= OnConnectionChanged;
        CloseRawCsv();
        lock (_sampleLock) { _manualPublishing = false; _dataBus.ManualOrthoState.Update(false, 0, null, "", false); }
        lock (_sampleLock)
        {
            _collectedData.Clear();
            _collectedDataSecondGroup.Clear();
        }
        CollectedData.Clear();
    }

    // ========== CSV 导入辅助 + 从数据库选 session ==========

    [RelayCommand]
    private void ShowCsvFormatHelp()
    {
        var dlg = new Views.Dialogs.CsvFormatHelpDialog
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        dlg.ShowDialog();
    }

    [RelayCommand]
    private void ShowProfileUsageHelp()
    {
        var dlg = new Views.Dialogs.ProfileUsageHelpDialog
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        dlg.ShowDialog();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSavedProfile))]
    private async Task ExportSelectedProfileJsonAsync()
    {
        if (SelectedSavedProfile == null)
        {
            System.Windows.MessageBox.Show("请先在表格中选中一个配置", "提示");
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出正交度配置 (JSON)",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"{SanitizeFileName(SelectedSavedProfile.Name)}.json",
            DefaultExt = ".json"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(SelectedSavedProfile,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(dlg.FileName, json);
            System.Windows.MessageBox.Show($"已导出: {dlg.FileName}", "成功");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"导出失败: {ex.Message}", "错误");
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedSavedProfile))]
    private async Task ExportSelectedProfileCsvAsync()
    {
        if (SelectedSavedProfile == null)
        {
            System.Windows.MessageBox.Show("请先在表格中选中一个配置", "提示");
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出正交度配置 (CSV)",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"{SanitizeFileName(SelectedSavedProfile.Name)}.csv",
            DefaultExt = ".csv"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var csv = BuildProfileCsv(SelectedSavedProfile);
            await File.WriteAllTextAsync(dlg.FileName, csv, new System.Text.UTF8Encoding(true));
            System.Windows.MessageBox.Show($"已导出: {dlg.FileName}", "成功");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"导出失败: {ex.Message}", "错误");
        }
    }

    /// <summary>
    /// 单个正交度配置的 CSV：名称与序列号按 RFC 4180 加引号（内部引号加倍，逗号与换行留在引号内），
    /// 数值用不变区域性的往返格式。
    /// </summary>
    internal static string BuildProfileCsv(OrthogonalityParams p)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("name,sensor_serial,created_at,unit,sample_count,residual_mean,residual_std," +
                      "offset_x,offset_y,offset_z," +
                      "m00,m01,m02,m10,m11,m12,m20,m21,m22");
        static string Text(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        static string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string DN(double? v) => v.HasValue ? D(v.Value) : "";
        sb.Append($"{Text(p.Name)},{Text(p.SensorSerial)},{p.CreatedAt:yyyy-MM-dd HH:mm:ss},");
        sb.Append($"{OrthogonalityParams.CanonicalUnit(p.Unit)},{p.SampleCount},{DN(p.ResidualMean)},{DN(p.ResidualStd)},");
        sb.Append($"{D(p.Offset[0])},{D(p.Offset[1])},{D(p.Offset[2])},");
        for (int i = 0; i < 9; i++)
        {
            sb.Append(D(p.CompensationMatrix[i]));
            if (i < 8) sb.Append(',');
        }
        sb.AppendLine();
        return sb.ToString();
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "profile";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
            if (Array.IndexOf(invalid, c) < 0) sb.Append(c);
        var s = sb.ToString().Trim();
        return s.Length == 0 ? "profile" : (s.Length > 80 ? s[..80] : s);
    }

    [RelayCommand]
    private async Task LoadFromSessionAsync()
    {
        var picker = new Views.Dialogs.SessionPickerDialog(_storageService)
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };
        if (picker.ShowDialog() != true || picker.SelectedSession == null) return;
        await LoadSessionDataAsync(picker.SelectedSession);
    }

    /// <summary>
    /// 从会话读取拟合样本：按“拟合通道”中所选的通道（换了会话时先按名称重新建议）。
    /// 通道元数据不一致、所选通道无效或单位不同时不加载，保留原有样本。
    /// </summary>
    private async Task LoadSessionDataAsync(SessionInfo session)
    {
        if (!ReferenceEquals(_loadedSession, session))
        {
            // 换了会话：来源切到“已保存会话”，通道选项换成该会话的通道并按名称重新建议。
            _loadedSession = session;
            if (DataSource != CalibrationDataSource.Session) DataSource = CalibrationDataSource.Session;
            else RefreshFittingChannels();
        }
        var version = ++_sessionLoadVersion;
        // 导入文件、开始实时采集等替换样本时都会推进这批数据的代号。
        var generation = Interlocked.Read(ref _collectedGeneration);
        try
        {
            if (session.ChannelNames.Length != session.ChannelCount || session.ChannelUnits.Length != session.ChannelCount)
                throw new ArgumentException("会话的通道名称、单位与通道数不一致，未加载拟合数据。");
            var map = SelectedFittingMap();
            bool dual = map.Length == 6;
            int requiredCols = map.Length;
            var sourceUnit = FittingChannelMap.Validate(map, session.ChannelUnits, FittingGroups);
            var readings = await _storageService.GetReadingsAsync(session.Id);
            // 读取期间又改了通道、换了会话或来源，或样本已被导入文件、实时采集替换：以后来的为准，丢弃这次结果。
            if (version != _sessionLoadVersion || generation != Interlocked.Read(ref _collectedGeneration) || IsCollecting
                || DataSource != CalibrationDataSource.Session || !ReferenceEquals(_loadedSession, session)) return;
            if (readings.Count == 0)
            {
                CollectionStatus = $"会话 '{session.Name}' 中没有数据";
                return;
            }

            var importedData = new List<double[]>();
            var importedDataSecond = new List<double[]>();
            foreach (var r in readings)
            {
                // 拟合用校正前的原始值；已校正的读数再拟合会叠加上一次校正。
                var v = r.OriginalChannelValues ?? r.ChannelValues;
                if (v.Length != session.ChannelCount)
                    throw new ArgumentException("会话读数与通道元数据不一致，未加载拟合数据。请按格式说明整理为明确三轴 CSV。");
                importedData.Add(new[] { v[map[0]], v[map[1]], v[map[2]] });
                if (dual)
                    importedDataSecond.Add(new[] { v[map[3]], v[map[4]], v[map[5]] });
            }

            ReplaceSamples(importedData, importedDataSecond, sourceUnit, requiredCols);
            RememberSamplesSource(map, CurrentFittingSourceKey());
            CollectionStatus = $"已从会话 '{session.Name}' 加载 {importedData.Count} 个样本（{sourceUnit}，{string.Join("、", map.Select(i => session.ChannelNames[i]))}）";

            UpdateCoverageEstimate();
            RunDataValidation();
            UpdateStepNavigation();
        }
        catch (Exception ex)
        {
            CollectionStatus = $"加载会话失败：{ex.Message}";
        }
    }

    private static string[] SplitCsvLine(string line) =>
        line.Split(new[] { ',', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim()).ToArray();

    private static bool TryParseDouble(string s, out double v) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static bool TryExtractByMap(string[] parts, int[] map, int start, int count, out double[] result)
    {
        result = new double[count];
        for (int i = 0; i < count; i++)
        {
            int colIdx = map[start + i];
            if (colIdx < 0 || colIdx >= parts.Length || !TryParseDouble(parts[colIdx], out result[i]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// 按 header 列名定位 X/Y/Z (双三轴: X1/Y1/Z1/X2/Y2/Z2)。
    /// 找不到的列返回 -1，TryExtractByMap 会因此返回 false 并跳行。
    /// </summary>
    private static int[] BuildColumnMap(string[] headers, bool dual)
    {
        var lower = headers.Select(h => h.ToLowerInvariant().Trim()).ToArray();

        int find(params string[] names)
        {
            foreach (var n in names)
            {
                int idx = Array.IndexOf(lower, n);
                if (idx >= 0) return idx;
            }
            return -1;
        }

        if (dual)
        {
            return new[]
            {
                find("x1", "bx1", "ch0"),
                find("y1", "by1", "ch1"),
                find("z1", "bz1", "ch2"),
                find("x2", "bx2", "ch3"),
                find("y2", "by2", "ch4"),
                find("z2", "bz2", "ch5"),
            };
        }
        return new[]
        {
            find("x", "bx", "ch0"),
            find("y", "by", "ch1"),
            find("z", "bz", "ch2"),
        };
    }
}
