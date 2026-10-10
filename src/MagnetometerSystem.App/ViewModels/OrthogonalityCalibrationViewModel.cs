using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.App.Services;
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
    public string Rating => FitQualityRating.Rate(result);

    private double ResidualNt => FitQualityRating.ResidualStdInNt(result);

    /// <summary>评级对应的状态色：ok / warn / err。</summary>
    public string Level => FitQualityRating.Level(ResidualNt);

    public string RatingHint => FitQualityRating.Hint(ResidualNt);
}

public partial class OrthogonalityCalibrationViewModel : ObservableObject
{
    private readonly IOrthogonalityService _orthogonalityService;
    private readonly ICalibrationRepository _calibrationRepository;
    private readonly DataBus _dataBus;
    private readonly IDataStorageService _storageService;
    private readonly IDialogService _dialogs;

    // 拟合样本连同单位、来源与代号：实时采集、导入文件和会话都填充这一份。换一批时代号加一，旧批次的回调与计算不能混入。
    private readonly CalibrationSampleSet _samples = new();
    private readonly CalibrationRawCsvRecorder _rawRecorder = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MagnetometerSystem", "statistics", "calibration_raw"));
    private readonly LiveCalibrationCollector _collector;

    /// <summary>导入 CSV 时数值的磁场单位（不自动猜测或换算）；实时采集与会话导入按协议通道单位自动设置。</summary>
    [ObservableProperty] private string _fittingUnit = "";
    public string[] FittingUnits { get; } = ["nT", "uT", "mT", "T"];
    public string CollectedUnit => _samples.Unit;
    public string ReferenceUnit => CollectedUnit.Length > 0 ? CollectedUnit : FittingUnit;
    /// <summary>参考场强的单位文字（标题与输入框后缀共用）；单位未知时提示待定。</summary>
    public string ReferenceUnitText => ReferenceUnit.Length > 0 ? ReferenceUnit : "单位待定";
    partial void OnFittingUnitChanged(string? oldValue, string newValue)
    {
        if (CollectedUnit.Length == 0 && OrthogonalityParams.CanonicalUnit(oldValue) != OrthogonalityParams.CanonicalUnit(newValue))
            ReferenceFieldStrength = null;
        OnPropertyChanged(nameof(ReferenceUnit)); OnPropertyChanged(nameof(ReferenceUnitText));
    }

    /// <summary>
    /// 换一批拟合数据：样本、单位、通道数与来源一起替换，清掉上一批的结果，返回这一批的代号；旧批次的回调与计算不再生效。
    /// <paramref name="map"/> 是取样用的通道（导入文件时为 null），<paramref name="sourceKey"/> 为空表示不记来源。
    /// </summary>
    private long ReplaceBatch(IEnumerable<double[]> first, IEnumerable<double[]> second, string unit, int channelCount,
        int[]? map, string sourceKey)
    {
        if (OrthogonalityParams.CanonicalUnit(ReferenceUnit) != unit) ReferenceFieldStrength = null;
        var generation = _samples.Replace(first, second, unit, channelCount, sourceKey, map);
        FittingUnit = unit;
        CalculationResult = null;
        SecondCalculationResult = null;
        FirstGroupResult = SecondGroupResult = null;
        SavedProfile = null;
        SavedSecondProfile = null;
        OnPropertyChanged(nameof(CollectedUnit));
        OnPropertyChanged(nameof(ReferenceUnit)); OnPropertyChanged(nameof(ReferenceUnitText));
        return generation;
    }

    // ========== 拟合通道 ==========
    // 协议决定通道顺序、名称和单位：拟合用哪几个通道由用户在第 2 步选定（按名称自动建议），不取前缀。

    /// <summary>“拟合通道”：当前来源的通道选项、所选通道与提示。</summary>
    public FittingChannelSelectorViewModel Fitting { get; }

    /// <summary>导入文件有自己的列映射，不显示通道选择。</summary>
    public bool ShowsFittingChannels => DataSource != CalibrationDataSource.File;

    /// <summary>当前在通道选项中显示的会话（来源为“已保存会话”时）。</summary>
    private SessionInfo? _loadedSession;

    // 已有样本是按哪个来源、哪组通道取得的，随样本记在样本集上（导入文件时没有通道映射）。来源相同而所选通道不同时，
    // 下拉框显示的已不是样本实际使用的列，不能据此计算和保存校正。

    private const string FileSourceKey = "file";

    /// <summary>
    /// 界面显示的来源或拟合通道与已有样本的来源不一致：显示的是另一个来源（换了会话、切到别的有数据的来源），
    /// 或同一来源下改了拟合通道。当前来源没有可显示的通道（已断开、未选会话）时不拦截，它不描述别的样本。
    /// </summary>
    public bool FittingMapMismatch
    {
        get
        {
            var samplesSource = _samples.SourceKey;
            if (samplesSource.Length == 0) return false;
            var current = CurrentFittingSourceKey();
            if (current.Length == 0) return false;
            if (current != samplesSource) return true;
            return _samples.Map is { } samplesMap && !Fitting.SelectedMap().SequenceEqual(samplesMap);
        }
    }

    /// <summary>已有样本来自另一个来源（相对当前显示）。</summary>
    private bool SamplesFromOtherSource
    {
        get
        {
            var samplesSource = _samples.SourceKey;
            var current = CurrentFittingSourceKey();
            return samplesSource.Length > 0 && current.Length > 0 && current != samplesSource;
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

    private int _sessionLoadVersion;

    private int FittingGroups => SelectedSensorType == SensorType.DualTriaxialFluxgate ? 2 : 1;

    private (IReadOnlyList<string> Names, IReadOnlyList<string> Units) FittingSourceLayout() => DataSource switch
    {
        CalibrationDataSource.Live when _dataBus.CurrentConnection != null && !_dataBus.IsPlaybackMode
            => (_dataBus.AcquisitionChannelNames, _dataBus.AcquisitionChannelUnits),
        CalibrationDataSource.Session when _loadedSession is { } session => (session.ChannelNames, session.ChannelUnits),
        _ => ([], []),
    };

    /// <summary>当前来源的通道：“拟合通道”据此建立选项、建议和检查所选通道。</summary>
    private FittingChannelSource CurrentFittingSource()
    {
        var (names, units) = FittingSourceLayout();
        return new FittingChannelSource(CurrentFittingSourceKey(), names, units, DataSource == CalibrationDataSource.Session
            ? "选择会话后，按会话的通道选择 X、Y、Z。"
            : "连接设备后，按当前协议的通道选择 X、Y、Z。");
    }

    /// <summary>来源变化（新连接、选了会话、切换来源）：重建选项并按名称重新建议。</summary>
    private void RefreshFittingChannels() => Fitting.Refresh();

    /// <summary>用户改了拟合通道或点了“自动识别”：已选会话且选择有效时按新的通道重新读取。</summary>
    private void ReloadSessionIfSelected()
    {
        if (Fitting.IsValid && DataSource == CalibrationDataSource.Session && _loadedSession is { } session && !IsCollecting)
            _ = LoadSessionDataAsync(session);
    }

    public const int ManualPointTarget = 48;

    public OrthogonalityCalibrationViewModel(
        IOrthogonalityService orthogonalityService,
        ICalibrationRepository calibrationRepository,
        DataBus dataBus,
        IDataStorageService storageService,
        IDialogService? dialogs = null)
    {
        _orthogonalityService = orthogonalityService;
        _calibrationRepository = calibrationRepository;
        _dataBus = dataBus;
        _storageService = storageService;
        _dialogs = dialogs ?? new WpfDialogService();
        Library = new CalibrationLibraryViewModel(calibrationRepository, _dialogs);
        Fitting = new FittingChannelSelectorViewModel(CurrentFittingSource, () => FittingGroups);
        Fitting.Changed += UpdateStepNavigation;
        Fitting.Edited += ReloadSessionIfSelected;
        _collector = new LiveCalibrationCollector(dataBus, _samples, _rawRecorder, RunOnUi, PostToUiInBackground);
        _collector.SamplesAdded += OnCollectedSamplesAdded;
        _collector.LayoutChanged += OnCollectionLayoutChanged;
        _collector.LiveValuesChanged += text => LiveValuesText = text;

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
        await Library.LoadSavedProfilesAsync();
    }

    /// <summary>“配置库”页签：已保存配置的列表、删除与导出。</summary>
    public CalibrationLibraryViewModel Library { get; }

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
            var validation = CalibrationDataValidator.Validate(SnapshotSamples(), CollectedUnit);
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
        if (!IsCollecting) Fitting.ApplySuggestion();
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
        if (IsCollecting) return;
        // 拟合数据的单位来自当前连接的协议通道；未连接、回放中或通道布局不符时不开始。
        LiveCalibrationPlan plan;
        try
        {
            if (_dataBus.CurrentConnection == null || _dataBus.IsPlaybackMode)
                throw new ArgumentException("请先连接设备，再按当前协议通道单位采集拟合数据。");
            var names = _dataBus.AcquisitionChannelNames;
            var units = _dataBus.AcquisitionChannelUnits;
            var map = Fitting.SelectedMap();
            var unit = FittingChannelMap.Validate(map, units, FittingGroups);
            if (names.Count != units.Count)
                throw new ArgumentException("当前连接的通道名称与单位数量不一致，不能采集拟合数据。");
            // 换成空的一批并记下来源与所选通道；所选通道、显示名称和当时的协议通道布局随采集冻结。
            var generation = ReplaceBatch([], [], unit, map.Length, map, CurrentFittingSourceKey());
            plan = new LiveCalibrationPlan(map, map.Select(i => names[i]).ToArray(), names, units,
                SelectedMode == CalibrationCollectionMode.Manual48, generation);
        }
        catch (Exception ex) { CollectionStatus = ex.Message; return; }
        CollectedData.Clear();
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

        _rawRecorder.Open(new CalibrationRawCsvHeader(ProfileName, SelectedSensorType.ToString(), CollectedUnit,
            SelectedMode.ToString(), plan.Labels));
        RawFilePath = _rawRecorder.FilePath;
        UpdateRawFileStatus();

        // 订阅读数；手动模式同时发布链路条的“采集中”状态。
        _collector.Start(plan);
        UpdateStepNavigation();
    }

    /// <summary>界面线程：连续模式新增的样本并入显示集合，并按 50 个样本一次更新覆盖度与校验。</summary>
    private void OnCollectedSamplesAdded(IReadOnlyList<double[]> added, int count)
    {
        var before = CollectedSampleCount;
        foreach (var sample in added) CollectedData.Add(sample);
        CollectedSampleCount = count;
        UpdateRawFileStatus();
        if (before / 50 != count / 50)
        {
            UpdateCoverageEstimate();
            RunDataValidation();
        }
        UpdateStepNavigation();
    }

    /// <summary>界面线程：连接的通道布局或读数通道数与开始时不同，停止拟合数据采集并说明原因；设备采集与会话保存不受影响。</summary>
    private void OnCollectionLayoutChanged(string reason)
    {
        if (!IsCollecting) return;
        StopCollecting();
        CollectionStatus = reason;
    }

    private List<double[]> SnapshotSamples() => _samples.Snapshot().First;

    /// <summary>双三轴第二组样本的副本（诊断与测试用）。</summary>
    internal List<double[]> SnapshotSecondGroupSamples() => _samples.Snapshot().Second;

    private void UpdateCoverageEstimate() =>
        SphericityCoverage = SphericalCoverageEstimator.Estimate(SnapshotSamples());

    [RelayCommand]
    private void StopCollecting()
    {
        // 退订、结束链路条的手动状态，并把还没显示的样本并入界面。
        // 退订不会取消已在进行的读数回调：采集器在同一把锁内结束采集，这些回调不会再加入样本，也不会把状态改回“采集中”。
        _collector.Stop();
        IsCollecting = false;
        // 采集中途重连了别的协议时选项没有刷新；停止后若实时布局已变，按新布局重建，免得下拉框仍显示旧通道名。
        if (DataSource == CalibrationDataSource.Live && CurrentFittingSourceKey() != Fitting.OptionsSourceKey)
            RefreshFittingChannels();
        CollectionStatus = $"采集完成，共 {CollectedSampleCount} 个样本";

        _rawRecorder.Close();
        UpdateRawFileStatus();

        UpdateCoverageEstimate();
        RunDataValidation();
        UpdateStepNavigation();
    }

    /// <summary>
    /// 手动模式下，把最近 10 条读数对每通道求均值，作为 1 个点加入样本。
    /// </summary>
    [RelayCommand]
    private void RecordCurrentPoint()
    {
        if (SelectedMode != CalibrationCollectionMode.Manual48 || !IsCollecting) return;
        if (_collector.RecordPoint() is not { } point) return;
        if (point.Error != null)
        {
            CollectionStatus = point.Error;
            return;
        }

        CollectedData.Add(point.Sample);
        CollectedSampleCount = point.Count;
        CollectionStatus = $"已记录第 {point.Count} 点（最近 {point.Averaged} 条读数的均值）";
        UpdateRawFileStatus();
        UpdateManualState(point.Buffered);
        if (point.Count % 6 == 0)
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
        if (_collector.UndoLastPoint() is not { } count) return;
        if (CollectedData.Count > 0) CollectedData.RemoveAt(CollectedData.Count - 1);
        CollectedSampleCount = count;
        CollectionStatus = $"已撤销第 {count + 1} 点";
        UpdateRawFileStatus();
        UpdateManualState(null);
        UpdateCoverageEstimate();
        RunDataValidation();
    }

    /// <summary>清空已记录的手动点，继续采集。原始 CSV 追加注释说明之前的点已作废。</summary>
    [RelayCommand(CanExecute = nameof(CanEditManualPoints))]
    private void ClearManualPoints()
    {
        if (_collector.ClearPoints() is not { } removed) return;
        CollectedData.Clear();
        CollectedSampleCount = 0;
        SphericityCoverage = 0;
        DataValidation = null;
        HasValidationWarnings = false;
        ValidationStatusText = string.Empty;
        CollectionStatus = $"已清空 {removed} 点，可以重新记录";
        UpdateRawFileStatus();
        UpdateManualState(null);
    }

    /// <summary>记录、撤销或清空后更新链路条的手动采集状态；采集器只在手动采集进行中发布。</summary>
    private void UpdateManualState(int? buffered)
    {
        int bufferCount = buffered ?? _collector.BufferedCount;
        _collector.PublishManualState(CollectedSampleCount,
            CollectedSampleCount >= ManualPointTarget ? $"已达 {ManualPointTarget} 点，可以结束采集" : $"已记录 {CollectedSampleCount} 点",
            bufferCount >= LiveCalibrationCollector.RecentBufferSize);
    }

    partial void OnCollectedSampleCountChanged(int value)
    {
        OnPropertyChanged(nameof(ManualPointSlots));
        UndoLastPointCommand.NotifyCanExecuteChanged();
        ClearManualPointsCommand.NotifyCanExecuteChanged();
        UpdateStepNavigation();
    }

    // ---- raw CSV 写入 ----

    /// <summary>原始 CSV 的保存目录；测试改为临时目录，不写入用户数据。</summary>
    internal string RawDataDirectory
    {
        get => _rawRecorder.OutputDirectory;
        set => _rawRecorder.OutputDirectory = value;
    }

    /// <summary>原始 CSV 的写入（诊断与测试用：测试据此模拟写入器失效）。</summary>
    internal CalibrationRawCsvRecorder RawRecorder => _rawRecorder;

    /// <summary>实时拟合采集（测试用：直接调用读数回调，模拟停止采集后才执行完的回调）。</summary>
    internal LiveCalibrationCollector Collector => _collector;

    /// <summary>最近一次实时采集的原始 CSV（开始采集时确定）；导入文件或会话不改变它。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RawFileName))]
    [NotifyPropertyChangedFor(nameof(RawFileDirectory))]
    [NotifyCanExecuteChangedFor(nameof(OpenRawFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenRawFileFolderCommand))]
    private string? _rawFilePath;

    public string RawFileName => Path.GetFileName(RawFilePath) ?? "";
    public string RawFileDirectory => Path.GetDirectoryName(RawFilePath) ?? "";

    /// <summary>原始 CSV 的写入状态：写入中 / 已结束及已写入的行数，或创建、写入失败的原因。</summary>
    [ObservableProperty]
    private string _rawFileStatus = string.Empty;

    [ObservableProperty]
    private bool _rawFileFailed;

    /// <summary>界面线程：按已写入的行数和失败原因刷新原始 CSV 的状态行。</summary>
    private void UpdateRawFileStatus()
    {
        if (RawFilePath == null) return;
        var (rows, error) = _rawRecorder.Status;
        RawFileFailed = error != null;
        RawFileStatus = error ?? (IsCollecting ? $"写入中 · {rows:N0} 行" : $"采集已结束 · 共 {rows:N0} 行");
    }

    [RelayCommand(CanExecute = nameof(HasRawFile))]
    private void OpenRawFile()
    {
        if (RawFilePath is not { } path) return;
        if (!File.Exists(path)) { CollectionStatus = $"原始数据文件不存在：{path}"; return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch (Exception ex) { CollectionStatus = $"无法打开原始数据文件：{ex.Message}"; }
    }

    /// <summary>在资源管理器中打开所在文件夹并选中该文件；文件不存在时只打开文件夹。</summary>
    [RelayCommand(CanExecute = nameof(HasRawFile))]
    private void OpenRawFileFolder()
    {
        if (RawFilePath is not { } path) return;
        var dir = Path.GetDirectoryName(path);
        try
        {
            if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
            else if (Directory.Exists(dir))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dir, UseShellExecute = true });
            else
                CollectionStatus = $"文件夹不存在：{dir}";
        }
        catch (Exception ex) { CollectionStatus = $"无法打开文件夹：{ex.Message}"; }
    }

    private bool HasRawFile() => RawFilePath != null;

    [RelayCommand]
    private void ImportFromFile()
    {
        if (ImportUnitOrReport() == null) return;
        var path = _dialogs.PickOpenFile("导入三轴校正数据",
            "CSV 文件 (*.csv)|*.csv|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*", ".csv");
        if (path == null) return;
        ImportCsvFile(path);
    }

    /// <summary>导入 CSV 时数值的单位；用户未明确选择时在状态里说明原因并返回 null。</summary>
    private string? ImportUnitOrReport()
    {
        var importUnit = OrthogonalityParams.CanonicalUnit(FittingUnit);
        if (importUnit.Length > 0) return importUnit;
        CollectionStatus = "请先明确选择 CSV 数值的磁场单位；不会自动猜测或换算。";
        return null;
    }

    /// <summary>从已选定的文件导入拟合样本：文件对话框之后的全部步骤（测试直接调用）。</summary>
    internal void ImportCsvFile(string path)
    {
        if (ImportUnitOrReport() is not { } importUnit) return;
        try
        {
            bool dual = SelectedSensorType == SensorType.DualTriaxialFluxgate;
            var imported = CalibrationCsvImporter.Parse(File.ReadAllLines(path), dual);

            if (imported.First.Count == 0)
            {
                CollectionStatus = "导入失败：文件中未找到有效的三轴数据。点击 [格式说明] 查看支持的格式。";
                return;
            }

            ReplaceSamples(imported.First, imported.Second, importUnit, dual ? 6 : 3, null, FileSourceKey);

            string skipInfo = imported.SkippedLines > 0 ? $"（跳过 {imported.SkippedLines} 行）" : "";
            CollectionStatus = $"已从文件导入 {imported.First.Count} 个样本{skipInfo}";

            UpdateCoverageEstimate();
            RunDataValidation();
            UpdateStepNavigation();
        }
        catch (Exception ex)
        {
            CollectionStatus = $"导入失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 用导入的样本替换当前样本（文件或会话来源），连同来源与所用通道一起记下：导入文件没有通道映射，
    /// <paramref name="sourceKey"/> 为空表示不记来源。只在未采集时调用。
    /// </summary>
    internal void ReplaceSamples(List<double[]> first, List<double[]> second, string unit, int channelCount,
        int[]? map = null, string sourceKey = "")
    {
        if (IsCollecting) StopCollecting();
        ReplaceBatch(first, second, unit, channelCount, map, sourceKey);
        CollectedData.Clear();
        foreach (var d in first) CollectedData.Add(d);
        CollectedSampleCount = first.Count;
    }

    private void RunDataValidation()
    {
        var samples = SnapshotSamples();
        if (samples.Count < 3) return;

        var validation = CalibrationDataValidator.Validate(samples, CollectedUnit);
        DataValidation = validation;
        HasValidationWarnings = validation.Warnings.Count > 0;

        if (validation.Warnings.Count > 0)
        {
            ValidationStatusText = string.Join("\n", validation.Warnings);
        }
        else
        {
            ValidationStatusText = $"数据质量良好（总场 {validation.MeanTotalField:G6} {CollectedUnit}，覆盖度 {validation.SphericityCoverage:P0}）";
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
            // 样本、单位与代号一起取得；计算期间换了一批数据时丢弃结果。
            var samples = _samples.Snapshot();
            var generation = samples.Generation;
            var unit = samples.Unit;
            if (unit.Length == 0) throw new ArgumentException("拟合数据单位未知，请重新采集或明确单位后导入。");
            var referenceField = ReferenceFieldStrength;
            List<double[]> first = samples.First, second = samples.Second;

            var rawData = OrthoMatrixHelpers.ToMatrix(first);
            var result = await Task.Run(() =>
                _orthogonalityService.Calculate(rawData, referenceField, unit));
            if (generation != _samples.Generation)
            {
                CalculationStatus = "拟合数据已更换，请重新计算。";
                return;
            }

            if (result.Success)
            {
                CalculationResult = result;
                FirstGroupResult = new OrthoGroupResult(result, rawData, OrthoMatrixHelpers.ApplyAll(result, rawData));
                CalculationStatus = "计算完成";
            }
            else
            {
                CalculationStatus = $"计算失败: {result.ErrorMessage}";
            }

            // 双三轴：第二组独立计算
            if (samples.ChannelCount == 6)
            {
                if (second.Count < 3)
                {
                    CalculationStatus += "；第二组样本不足 3 个，未计算";
                }
                else
                {
                    var rawData2 = OrthoMatrixHelpers.ToMatrix(second);
                    var result2 = await Task.Run(() =>
                        _orthogonalityService.Calculate(rawData2, referenceField, unit));
                    if (generation != _samples.Generation)
                    {
                        CalculationStatus = "拟合数据已更换，请重新计算。";
                        return;
                    }
                    if (result2.Success)
                    {
                        SecondCalculationResult = result2;
                        SecondGroupResult = new OrthoGroupResult(result2, rawData2, OrthoMatrixHelpers.ApplyAll(result2, rawData2));
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

    /// <summary>同 <see cref="RunOnUi"/>，但排在输入与渲染之后：高速读数时新增样本的刷新不挤占界面响应。</summary>
    private static void PostToUiInBackground(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Background);
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
    public string QualityRating => FitQualityRating.Rate(CalculationResult);
    public string SecondQualityRating => FitQualityRating.Rate(SecondCalculationResult);


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
            await Library.LoadSavedProfilesAsync();

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
        // 作废这一批并停止采集：在途的读数回调与计算都不再生效，链路条不再显示手动采集。
        _samples.Invalidate();
        _collector.Dispose();
        IsCollecting = false;
        _dataBus.ManualOrthoRecordRequested -= OnManualOrthoRecordRequested;
        _dataBus.ConnectionChanged -= OnConnectionChanged;
        _rawRecorder.Close();
        UpdateRawFileStatus();
        CollectedData.Clear();
    }

    // ========== CSV 导入辅助 + 从数据库选 session ==========

    [RelayCommand]
    private void ShowCsvFormatHelp() => _dialogs.ShowHelp(HelpTopic.CalibrationCsvFormat);

    [RelayCommand]
    private async Task LoadFromSessionAsync()
    {
        if (_dialogs.PickSession(_storageService) is not { } session) return;
        await LoadSessionDataAsync(session);
    }

    /// <summary>
    /// 从会话读取拟合样本：按“拟合通道”中所选的通道（换了会话时先按名称重新建议）。
    /// 通道元数据不一致、所选通道无效或单位不同时不加载，保留原有样本。
    /// </summary>
    internal async Task LoadSessionDataAsync(SessionInfo session)
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
        var generation = _samples.Generation;
        try
        {
            if (session.ChannelNames.Length != session.ChannelCount || session.ChannelUnits.Length != session.ChannelCount)
                throw new ArgumentException("会话的通道名称、单位与通道数不一致，未加载拟合数据。");
            var map = Fitting.SelectedMap();
            bool dual = map.Length == 6;
            int requiredCols = map.Length;
            var sourceUnit = FittingChannelMap.Validate(map, session.ChannelUnits, FittingGroups);
            var readings = await _storageService.GetReadingsAsync(session.Id);
            // 读取期间又改了通道、换了会话或来源，或样本已被导入文件、实时采集替换：以后来的为准，丢弃这次结果。
            if (version != _sessionLoadVersion || generation != _samples.Generation || IsCollecting
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

            ReplaceSamples(importedData, importedDataSecond, sourceUnit, requiredCols, map, CurrentFittingSourceKey());
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
}
