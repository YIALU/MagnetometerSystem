using System.Collections.ObjectModel;
using System.ComponentModel;
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

    // ---- 批量校正 ----
    [ObservableProperty]
    private ObservableCollection<OrthogonalityParams> _availableProfiles = new();

    [ObservableProperty]
    private OrthogonalityParams? _selectedCorrectionProfile;

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

        // 订阅采集事件
        _dataBus.AcquisitionStarted += OnAcquisitionStarted;
        _dataBus.AcquisitionStopped += OnAcquisitionStopped;
        _dataBus.ReadingReceived += OnReadingReceived;

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

    private async void OnAcquisitionStarted(SensorConfig config)
    {
        if (_dataBus.IsPlaybackMode) return;
        _currentSensorConfig = config;

        var name = $"采集_{DateTime.Now:yyyy-MM-dd_HH:mm:ss}";

        // 创建一个默认的 ConnectionConfig 用于会话记录
        var connectionConfig = new ConnectionConfig();

        try
        {
            var sessionId = await _storageService.StartSessionAsync(name, config, connectionConfig);

            Application.Current?.Dispatcher.Invoke(() =>
            {
                ActiveSessionId = sessionId;
                IsRecording = true;
            });

            _dataBus.PublishSessionStarted(sessionId);
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"创建会话失败: {ex.Message}");
        }
    }

    private async void OnAcquisitionStopped()
    {
        // 先 flush 剩余缓冲数据
        await FlushBufferAsync();

        var sessionId = ActiveSessionId;
        if (sessionId != null)
        {
            try
            {
                await _storageService.EndSessionAsync(sessionId);
                _dataBus.PublishSessionEnded(sessionId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"结束会话失败: {ex.Message}");
            }

            Application.Current?.Dispatcher.Invoke(() =>
            {
                ActiveSessionId = null;
                IsRecording = false;
            });

            await RefreshSessionsAsync();
        }

        _currentSensorConfig = null;
    }

    private void OnReadingReceived(MagnetometerReading reading)
    {
        if (ActiveSessionId == null || _dataBus.IsPlaybackMode) return;

        // 设置会话 ID
        reading.SessionId = ActiveSessionId;

        lock (_bufferLock)
        {
            _readingBuffer.Add(reading);

            if (_readingBuffer.Count >= FlushBatchSize ||
                (DateTime.UtcNow - _lastFlushTime).TotalMilliseconds >= FlushIntervalMs)
            {
                var batch = _readingBuffer.ToList();
                _readingBuffer.Clear();
                _lastFlushTime = DateTime.UtcNow;

                // 异步写入，不阻塞事件处理
                _ = _storageService.SaveReadingsAsync(batch);
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

    // ---- 命令 ----

    [RelayCommand]
    private async Task RefreshSessionsAsync()
    {
        try
        {
            var sessions = await _storageService.GetSessionsAsync();

            Application.Current?.Dispatcher.Invoke(() =>
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
        var fileName = $"{timestamp}{suffix}.csv";

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
                IncludeHeader = true,
                IncludeCalibratedData = false
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

        var result = MessageBox.Show(
            $"将对会话 '{session.Name}' 应用正交度校正 '{SelectedCorrectionProfile.Name}'。\n原始数据将保留不变，校正结果单独存储。\n继续？",
            "批量校正",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

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

            // 2. 删除此配置的旧校正结果
            await _storageService.DeleteCorrectedReadingsAsync(session.Id, SelectedCorrectionProfile.Id);

            // 3. 批量应用校正
            var progress = new Progress<int>(processed =>
            {
                CorrectionProgress = processed;
            });

            var batchResult = await _orthogonalityCorrector.ApplyBatchAsync(
                SelectedCorrectionProfile, readings, progress);

            // 4. 映射为 CorrectedReading 并保存（保留原始数据）
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

            CorrectionProgress = 100;
            MessageBox.Show(
                $"校正完成！\n处理 {batchResult.ProcessedCount} 条数据。\n原始数据已保留，校正结果已单独保存。",
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

    // ---- 筛选 ----

    private bool FilterSession(object obj)
    {
        if (obj is not SessionInfo session) return false;

        // 名称搜索
        if (!string.IsNullOrWhiteSpace(SearchText) &&
            !session.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
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
}
