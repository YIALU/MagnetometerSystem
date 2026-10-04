using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;
using MagnetometerSystem.Infrastructure.Export;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 会话列表 ViewModel - 管理采集会话的生命周期、列表展示、搜索与操作
/// </summary>
public partial class SessionListViewModel : ObservableObject
{
    private readonly IDataStorageService _storageService;
    private readonly IDataExporter _dataExporter;
    private readonly DataBus _dataBus;
    private readonly OrthogonalityCorrector _orthogonalityCorrector;
    private readonly ICalibrationRepository _calibrationRepository;

    // ---- 读数缓冲 ----
    private readonly List<MagnetometerReading> _readingBuffer = new(500);
    private readonly object _bufferLock = new();
    private DateTime _lastFlushTime = DateTime.MinValue;
    private const int FlushBatchSize = 500;
    private const int FlushIntervalMs = 200;
    private System.Threading.Timer? _flushTimer;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private bool _acceptingReadings;
    private int _storageFaulted;
    private long _sessionGeneration;
    private long _savedBaseline;

    [ObservableProperty] private string _storageStatus = "就绪";
    [ObservableProperty] private string? _storageError;
    [ObservableProperty] private long _pendingReadingCount;
    [ObservableProperty] private long _savedReadingCount;
    [ObservableProperty] private string _firstCorrectionChannels = "";
    [ObservableProperty] private string _secondCorrectionChannels = "";
    [ObservableProperty] private DateTime? _exportStartTime;
    [ObservableProperty] private DateTime? _exportEndTime;
    [ObservableProperty] private string _exportChannelIndices = "";
    [ObservableProperty] private ExportDataSource _exportSource = ExportDataSource.Raw;
    [ObservableProperty] private bool _exportIncludeUnits = true;
    [ObservableProperty] private bool _exportIncludeHeader = true;
    [ObservableProperty] private int? _exportDecimalPlaces;
    public ExportDataSource[] ExportSources { get; } = Enum.GetValues<ExportDataSource>();

    // ---- 当前采集的传感器/连接配置（用于创建会话） ----
    private SensorConfig? _currentSensorConfig;

    // ---- 数据集合 ----
    public ObservableCollection<SessionInfo> Sessions { get; }
    public ICollectionView SessionsView { get; }

    // ---- 选中项 ----
    [ObservableProperty]
    private SessionInfo? _selectedSession;

    // ---- 搜索/筛选 ----
    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private DateTime? _filterStartDate;

    [ObservableProperty]
    private DateTime? _filterEndDate;

    [ObservableProperty]
    private SensorType? _filterSensorType;

    // ---- 当前活跃会话 ----
    [ObservableProperty]
    private string? _activeSessionId;

    [ObservableProperty]
    private bool _isRecording;

    /// <summary>
    /// 当前会话已接收的读数计数（实时递增）。UI 订阅此值显示录制中的点数，
    /// 比 RealtimeChart 的 DataPointCount 可靠（不受页面可见性影响）。
    /// </summary>
    [ObservableProperty]
    private long _activeSessionReadingCount;

    // ---- 批量校正 ----
    [ObservableProperty]
    private ObservableCollection<OrthogonalityParams> _availableProfiles = new();

    [ObservableProperty]
    private OrthogonalityParams? _selectedCorrectionProfile;

    /// <summary>双三轴第二组正交度配置（仅双三轴传感器会话使用）</summary>
    [ObservableProperty]
    private OrthogonalityParams? _selectedCorrectionProfileSecond;

    [ObservableProperty]
    private bool _isCorrecting;

    [ObservableProperty]
    private double _correctionProgress;

    // ---- 传感器类型列表（供 ComboBox 绑定） ----
    public SensorType[] SensorTypes { get; } = Enum.GetValues<SensorType>();

    /// <summary>
    /// 请求回放指定会话 (传递 session ID)
    /// </summary>
    public event Action<string>? PlaybackRequested;

    public SessionListViewModel(
        IDataStorageService storageService, IDataExporter dataExporter, DataBus dataBus,
        OrthogonalityCorrector orthogonalityCorrector, ICalibrationRepository calibrationRepository)
    {
        _storageService = storageService;
        _dataExporter = dataExporter;
        _dataBus = dataBus;
        _orthogonalityCorrector = orthogonalityCorrector;
        _calibrationRepository = calibrationRepository;

        Sessions = new ObservableCollection<SessionInfo>();
        SessionsView = CollectionViewSource.GetDefaultView(Sessions);
        SessionsView.Filter = FilterSession;
        SessionsView.SortDescriptions.Add(
            new SortDescription(nameof(SessionInfo.StartedAt), ListSortDirection.Descending));

        // 订阅采集事件。
        // 会话创建走 AcquisitionStarting（连接打开前 await 完成），保证第一条数据到达时
        // ActiveSessionId 已就绪，消除启动丢数据窗口。
        _dataBus.AcquisitionStarting += OnAcquisitionStartingAsync;
        _dataBus.AcquisitionStopping += OnAcquisitionStoppingAsync;
        _dataBus.AcquisitionFaulted += OnAcquisitionFaulted;
        _dataBus.ReadingReceived += OnReadingReceived;
        _storageService.WriteStatusChanged += OnStorageWriteStatusChanged;

        // 会话列表延迟加载：等用户首次导航到此页面时再加载
    }

    private bool _isLoaded;
    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded) return;
        _isLoaded = true;
        await RefreshSessionsAsync();
    }

    // ---- 采集生命周期管理 ----

    /// <summary>
    /// 采集即将开始：在连接打开之前创建会话并就绪 ActiveSessionId。
    /// 返回 Task（非 async void），供 DataBus.PublishAcquisitionStartingAsync 等待完成，
    /// 确保连接打开后第一条数据到达时会话已存在，不丢数据。
    /// </summary>
    private async Task OnAcquisitionStartingAsync(SensorConfig config)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (ActiveSessionId != null)
                throw new InvalidOperationException("上一个会话尚未完成保存，请先重试存储。");
            _currentSensorConfig = config;
            var name = $"采集_{DateTime.Now:yyyy-MM-dd_HH:mm:ss}";
            var connectionConfig = _dataBus.AcquisitionConnectionConfig ?? new ConnectionConfig();
            _savedBaseline = _storageService.WriteStatus.SavedReadings;
            var sessionId = await _storageService.StartSessionAsync(name, config, connectionConfig);

            OnUi(() =>
            {
                ActiveSessionId = sessionId;
                IsRecording = true;
                ActiveSessionReadingCount = 0;
                SavedReadingCount = 0;
                StorageError = null;
                StorageStatus = "自动保存中";
            });
            long generation = Interlocked.Increment(ref _sessionGeneration);
            Interlocked.Exchange(ref _storageFaulted, 0);
            lock (_bufferLock) _acceptingReadings = true;
            _flushTimer = new System.Threading.Timer(_ => _ = ObserveWriteAsync(FlushBufferAsync, generation),
                null, FlushIntervalMs, FlushIntervalMs);
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"创建会话失败: {ex.Message}");
            // 重新抛出：让连接流程在打开端口之前中止。否则数据库不可用时仍会打开连接，
            // 而 ActiveSessionId 为 null 导致读数被静默丢弃。
            throw;
        }
        finally { _lifecycleGate.Release(); }
    }

    private Task OnAcquisitionStoppingAsync() => StopSessionAsync();

    private async Task StopSessionAsync(string? expectedSessionId = null)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (expectedSessionId != null && ActiveSessionId != expectedSessionId) return;
            lock (_bufferLock) _acceptingReadings = false;
            var flushTimer = Interlocked.Exchange(ref _flushTimer, null);
            if (flushTimer != null) await flushTimer.DisposeAsync();
            OnUi(() => IsRecording = false);
            if (ActiveSessionId is not { } sessionId) return;
            await FlushBufferAsync();
            await _storageService.WaitForPendingWritesAsync();
            await _storageService.EndSessionAsync(sessionId);
            OnUi(() =>
            {
                ActiveSessionId = null;
                StorageStatus = "全部保存完成";
            });
            _currentSensorConfig = null;
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            ReportStorageError(ex);
            throw;
        }
        finally { _lifecycleGate.Release(); }
    }

    private void OnReadingReceived(MagnetometerReading reading)
    {
        lock (_bufferLock)
        {
            if (!Volatile.Read(ref _acceptingReadings) || ActiveSessionId == null) return;
            var snapshot = reading.DeepClone();
            snapshot.SessionId = ActiveSessionId;
            _readingBuffer.Add(snapshot);
            ActiveSessionReadingCount++;

            if (_readingBuffer.Count >= FlushBatchSize ||
                (DateTime.UtcNow - _lastFlushTime).TotalMilliseconds >= FlushIntervalMs)
            {
                var batch = _readingBuffer.ToList();
                _readingBuffer.Clear();
                _lastFlushTime = DateTime.UtcNow;

                // 异步写入，不阻塞事件处理
                _ = ObserveWriteAsync(() => _storageService.SaveReadingsAsync(batch), Volatile.Read(ref _sessionGeneration));
            }
        }
    }

    private Task FlushBufferAsync()
    {
        List<MagnetometerReading> batch;
        lock (_bufferLock)
        {
            if (_readingBuffer.Count == 0) return Task.CompletedTask;
            batch = _readingBuffer.ToList();
            _readingBuffer.Clear();
            _lastFlushTime = DateTime.UtcNow;
        }
        return _storageService.SaveReadingsAsync(batch);
    }

    private static void OnUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.Invoke(action);
        else action();
    }

    private async Task ObserveWriteAsync(Func<Task> write, long generation)
    {
        try { await write().ConfigureAwait(false); }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _sessionGeneration) && Volatile.Read(ref _storageFaulted) == 0)
                ReportStorageError(ex, generation);
        }
    }

    private void OnAcquisitionFaulted(Exception error)
    {
        if (ActiveSessionId == null || Interlocked.Exchange(ref _storageFaulted, 1) != 0) return;
        // This must run on the faulting thread, even while the UI is busy.
        Volatile.Write(ref _acceptingReadings, false);
        // Keep the timer reference: the stop path awaits DisposeAsync so any
        // callback already taking a tail batch must finish enqueueing it first.
        try { _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
        ReportStorageError(error);
    }

    private void ReportStorageError(Exception ex, long? writeGeneration = null)
    {
        long generation = writeGeneration ?? Volatile.Read(ref _sessionGeneration);
        if (generation != Volatile.Read(ref _sessionGeneration)) return;
        if (Volatile.Read(ref _acceptingReadings)) _dataBus.PublishAcquisitionFault(ex);
        // 不同步阻塞后台写入线程：UI 可能正在等待停止/退出。
        void Update()
        {
            if (generation != Volatile.Read(ref _sessionGeneration) || ActiveSessionId == null) return;
            IsRecording = false;
            StorageError = ex.Message;
            StorageStatus = "保存失败 · 采集已停止，数据待重试";
        }
        if (Application.Current?.Dispatcher is { } dispatcher) dispatcher.BeginInvoke(Update);
        else Update();
    }

    private void OnStorageWriteStatusChanged(StorageWriteStatus ignored)
    {
        void Update()
        {
            var status = _storageService.WriteStatus;
            SavedReadingCount = Math.Max(0, status.SavedReadings - _savedBaseline);
            lock (_bufferLock) PendingReadingCount = status.PendingReadings + _readingBuffer.Count;
            StorageError = status.LastError;
            StorageStatus = status.LastError != null ? "保存失败 · 数据待重试"
                : PendingReadingCount > 0 ? "正在保存" : IsRecording ? "自动保存中" : "全部保存完成";
        }
        if (Application.Current?.Dispatcher is { } dispatcher) dispatcher.BeginInvoke(Update);
        else Update();
    }

    [RelayCommand]
    private async Task RetryStorageAsync()
    {
        long generation = Volatile.Read(ref _sessionGeneration);
        string? recoveredSessionId = ActiveSessionId;
        try
        {
            await _storageService.RetryPendingWritesAsync();
            if (generation != Volatile.Read(ref _sessionGeneration)) return;
            await FlushBufferAsync();
            if (generation != Volatile.Read(ref _sessionGeneration)) return;
            if (!Volatile.Read(ref _acceptingReadings) && recoveredSessionId != null)
            {
                await StopSessionAsync(recoveredSessionId);
                // StopSessionAsync has released _lifecycleGate. The connection
                // owner can now take its gate and complete the same session's stop.
                await _dataBus.PublishAcquisitionRecoveryCompletedAsync(recoveredSessionId);
            }
            OnStorageWriteStatusChanged(_storageService.WriteStatus);
        }
        catch (Exception ex) { ReportStorageError(ex, generation); }
    }

    // ---- 命令 ----

    [RelayCommand]
    private async Task RefreshSessionsAsync()
    {
        try
        {
            var sessions = await _storageService.GetSessionsAsync();

            OnUi(() =>
            {
                Sessions.Clear();
                foreach (var session in sessions)
                {
                    Sessions.Add(session);
                }
                SessionsView.Refresh();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"加载会话列表失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RenameSessionAsync(SessionInfo? session)
    {
        if (session == null) return;

        // 弹出简单的输入对话框
        var newName = PromptInput("重命名会话", "请输入新的会话名称:", session.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == session.Name) return;

        try
        {
            await _storageService.UpdateSessionAsync(session.Id, newName, session.Notes);
            session.Name = newName;
            SessionsView.Refresh();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"重命名失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task EditNotesAsync(SessionInfo? session)
    {
        if (session == null) return;

        var newNotes = PromptInput("编辑备注", "请输入备注内容:", session.Notes ?? "");
        if (newNotes == null) return;

        if (newNotes.Length > 60)
        {
            MessageBox.Show("备注最多 60 字符，已自动截断", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            newNotes = newNotes[..60];
        }

        try
        {
            await _storageService.UpdateSessionAsync(session.Id, session.Name, newNotes);
            session.Notes = newNotes;
            SessionsView.Refresh();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"编辑备注失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(SessionInfo? session)
    {
        if (session == null) return;
        if (session.Id == ActiveSessionId)
        {
            MessageBox.Show("该会话仍在采集或等待保存，请先断开连接并完成保存。", "无法删除", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"确定要删除会话 '{session.Name}' 及其 {session.TotalReadings} 条数据吗？此操作不可恢复。",
            "确认删除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            await _storageService.DeleteSessionAsync(session.Id);
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除会话失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ExportSessionAsync(SessionInfo? session)
    {
        if (session == null) return;

        var suffix = session.ChannelCount switch
        {
            3 => "_3C",
            6 => "_3CG",
            _ => "_Custom"
        };
        var timestamp = session.StartedAt.ToString("yyyy-MM-dd_HH-mm-ss");
        var notes = SanitizeForFileName(session.Notes);
        var fileName = string.IsNullOrEmpty(notes)
            ? $"{timestamp}{suffix}.csv"
            : $"{timestamp}_{notes}{suffix}.csv";

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出会话数据",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = fileName,
            DefaultExt = ".csv"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var options = new ExportOptions
            {
                IncludeHeader = ExportIncludeHeader,
                IncludeCalibratedData = ExportSource != ExportDataSource.Raw,
                IncludeUnits = ExportIncludeUnits,
                Source = ExportSource,
                CorrectionProfileId = SelectedCorrectionProfile?.Id,
                StartTime = ExportStartTime,
                EndTime = ExportEndTime,
                DecimalPlaces = ExportDecimalPlaces,
                ChannelIndices = string.IsNullOrWhiteSpace(ExportChannelIndices) ? null : ParseIndices(ExportChannelIndices)
            };

            await _dataExporter.ExportAsync(session.Id, dialog.FileName, options);

            MessageBox.Show($"导出完成: {dialog.FileName}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void PlaybackSession(SessionInfo? session)
    {
        if (session == null) return;
        PlaybackRequested?.Invoke(session.Id);
    }

    // ---- 批量校正命令 ----

    [RelayCommand]
    private async Task LoadCorrectionProfilesAsync()
    {
        try
        {
            var profiles = await _calibrationRepository.GetOrthogonalityProfilesAsync();
            Application.Current?.Dispatcher.Invoke(() =>
            {
                AvailableProfiles.Clear();
                foreach (var p in profiles)
                    AvailableProfiles.Add(p);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"加载校正配置失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ApplyBatchCorrectionAsync(SessionInfo? session)
    {
        if (session == null || SelectedCorrectionProfile == null) return;

        var isDual = SelectedCorrectionProfileSecond != null;
        int[] firstChannels;
        int[]? secondChannels;
        try
        {
            firstChannels = CorrectionMapping(FirstCorrectionChannels, session, false);
            secondChannels = isDual ? CorrectionMapping(SecondCorrectionChannels, session, true) : null;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "请确认改正通道", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var profileDesc = isDual
            ? $"'{SelectedCorrectionProfile.Name}' + '{SelectedCorrectionProfileSecond!.Name}'"
            : $"'{SelectedCorrectionProfile.Name}'";

        var result = MessageBox.Show(
            $"将对会话 '{session.Name}' 应用正交度校正 {profileDesc}。\n" +
            "原始数据将保留不变，校正结果会：\n" +
            "  1) 单独保存到数据库\n" +
            "  2) 导出到你选择的 CSV 文件\n继续？",
            "批量校正",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        // 让用户先选导出路径，避免跑完才发现用户取消
        var csvDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "选择校正结果 CSV 保存位置",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"{SanitizeForFileName(session.Name)}_corrected_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            DefaultExt = ".csv"
        };
        if (csvDialog.ShowDialog() != true) return;

        IsCorrecting = true;
        CorrectionProgress = 0;

        try
        {
            // 1. 加载原始读数
            var readings = await _storageService.GetReadingsAsync(session.Id);
            if (readings.Count == 0)
            {
                MessageBox.Show("会话中没有数据。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 3. 批量应用校正（双三轴时传第二组）
            var progress = new Progress<int>(processed =>
            {
                CorrectionProgress = processed;
            });

            var batchResult = await _orthogonalityCorrector.ApplyBatchAsync(
                SelectedCorrectionProfile, SelectedCorrectionProfileSecond, readings,
                firstChannels, secondChannels, progress);

            // 4. 映射为 CorrectedReading 并保存
            var correctedReadings = new List<CorrectedReading>();
            for (int i = 0; i < readings.Count; i++)
            {
                var cr = CorrectedReading.FromOriginal(
                    readings[i],
                    batchResult.CorrectedReadings[i].ChannelValues,
                    SelectedCorrectionProfile.Id);
                correctedReadings.Add(cr);
            }

            await _storageService.SaveCorrectedReadingsAsync(correctedReadings);

            // 5. 导出 CSV 到用户选定的路径
            await _dataExporter.ExportAsync(session.Id, csvDialog.FileName, new ExportOptions
            {
                Source = ExportDataSource.Corrected,
                CorrectionProfileId = SelectedCorrectionProfile.Id,
                IncludeUnits = true,
                IncludeCalibratedData = true
            });

            CorrectionProgress = 100;
            MessageBox.Show(
                $"校正完成！\n处理 {batchResult.ProcessedCount} 条数据。\n" +
                $"• 数据库：已保存到 corrected_readings 表\n" +
                $"• 文件：{csvDialog.FileName}",
                "成功",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"批量校正失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsCorrecting = false;
        }
    }

    private static int[] ParseIndices(string value)
    {
        return value.Split([',', '，', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }

    private static int[] CorrectionMapping(string text, SessionInfo session, bool second)
    {
        int[] indices;
        if (!string.IsNullOrWhiteSpace(text)) indices = ParseIndices(text);
        else if (session.SensorType is SensorType.TriaxialFluxgate or SensorType.DualTriaxialFluxgate)
            indices = second ? [3, 4, 5] : [0, 1, 2];
        else throw new ArgumentException("请明确填写要改正的三个磁场通道索引，例如 0,1,2；不会自动把温度等辅助通道用于正交度改正。");
        if (indices.Length != 3 || indices.Distinct().Count() != 3 || indices.Any(i => i < 0 || i >= session.ChannelCount))
            throw new ArgumentException("正交度改正需要三个不重复且有效的通道索引。");
        if (session.ChannelUnits.Length == session.ChannelCount)
        {
            var units = indices.Select(i => session.ChannelUnits[i]).ToArray();
            if (units.Distinct().Count() != 1 || !new[] { "nT", "uT", "µT", "μT", "mT", "T" }.Contains(units[0]))
                throw new ArgumentException("所选三个通道必须使用相同的磁场单位，不能包含温度等辅助通道。");
        }
        return indices;
    }

    // ---- 筛选 ----

    private bool FilterSession(object obj)
    {
        if (obj is not SessionInfo session) return false;

        // 名称搜索
        if (!string.IsNullOrWhiteSpace(SearchText) &&
            !session.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) &&
            !(session.Notes?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false))
            return false;

        // 日期范围
        if (FilterStartDate.HasValue && session.StartedAt < FilterStartDate.Value)
            return false;
        if (FilterEndDate.HasValue && session.StartedAt > FilterEndDate.Value.AddDays(1))
            return false;

        // 传感器类型
        if (FilterSensorType.HasValue && session.SensorType != FilterSensorType.Value)
            return false;

        return true;
    }

    partial void OnSearchTextChanged(string value) => SessionsView.Refresh();
    partial void OnFilterStartDateChanged(DateTime? value) => SessionsView.Refresh();
    partial void OnFilterEndDateChanged(DateTime? value) => SessionsView.Refresh();
    partial void OnFilterSensorTypeChanged(SensorType? value) => SessionsView.Refresh();

    // ---- 辅助方法 ----

    /// <summary>
    /// 简易输入对话框（使用 MessageBox 风格的输入提示）
    /// </summary>
    private static string? PromptInput(string title, string prompt, string defaultValue)
    {
        // 使用简单的 WPF Window 作为输入对话框
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
            ResizeMode = ResizeMode.NoResize
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(15) };
        var label = new System.Windows.Controls.TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) };
        var textBox = new System.Windows.Controls.TextBox { Text = defaultValue, Margin = new Thickness(0, 0, 0, 12) };
        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var okButton = new System.Windows.Controls.Button
        {
            Content = "确定",
            Width = 75,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "取消",
            Width = 75,
            IsCancel = true
        };

        string? result = null;
        okButton.Click += (s, e) => { result = textBox.Text; dialog.DialogResult = true; };
        cancelButton.Click += (s, e) => { dialog.DialogResult = false; };

        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        panel.Children.Add(label);
        panel.Children.Add(textBox);
        panel.Children.Add(buttonPanel);
        dialog.Content = panel;

        dialog.ShowDialog();
        return result;
    }

    private static string SanitizeForFileName(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return string.Empty;

        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var c in notes)
        {
            if (c == '\n' || c == '\r' || c == '\t') { sb.Append(' '); continue; }
            if (Array.IndexOf(invalidChars, c) >= 0) continue;
            sb.Append(c);
        }

        // 压缩连续空白为单空格
        var result = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        // 截断到 60 字符
        if (result.Length > 60) result = result[..60];
        // 空格替换为下划线
        return result.Replace(' ', '_');
    }
}
