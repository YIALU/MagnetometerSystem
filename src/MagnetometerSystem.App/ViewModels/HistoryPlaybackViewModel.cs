using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 回放状态枚举
/// </summary>
public enum PlaybackState
{
    Ready,
    Loading,
    Playing,
    Paused,
    Completed
}

/// <summary>
/// 历史数据回放 ViewModel
/// </summary>
public partial class HistoryPlaybackViewModel : ObservableObject, IDisposable
{
    private readonly IDataStorageService _storageService;
    private readonly DataBus _dataBus;
    private readonly OrthogonalityCorrector _orthogonalityCorrector;
    private readonly ICalibrationRepository _calibrationRepository;

    // ---- 内部数据 ----
    private MagnetometerReading[] _readings = [];
    private DispatcherTimer? _playbackTimer;
    private bool _wasPlayingBeforeSeek;
    private readonly Stopwatch _playbackClock = new();
    private TimeSpan _positionAtTimerStart;
    private bool _ownsPlayback;

    // ---- 会话选择 ----
    public ObservableCollection<SessionInfo> AvailableSessions { get; } = new();

    [ObservableProperty]
    private SessionInfo? _selectedSession;

    [ObservableProperty]
    private SessionInfo? _loadedSession;

    public string LoadedChannelSummary => LoadedSession == null ? "" : string.Join(" · ",
        RebuildSensorConfig(LoadedSession).ChannelNames.Select((name, index) =>
            $"{index}: {name}" + (index < LoadedSession.ChannelUnits.Length &&
                !string.IsNullOrWhiteSpace(LoadedSession.ChannelUnits[index])
                ? $" ({LoadedSession.ChannelUnits[index]})" : "")));

    [ObservableProperty]
    private string _statusMessage = "选择已结束的会话并加载。回放不写入采集数据库。";

    // ---- 回放状态 ----
    [ObservableProperty]
    private PlaybackState _state = PlaybackState.Ready;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isLoading;

    // ---- 进度 ----
    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private int _currentIndex;

    [ObservableProperty]
    private int _totalReadings;

    [ObservableProperty]
    private string _currentTime = "";

    [ObservableProperty]
    private string _totalDuration = "";

    [ObservableProperty]
    private string _elapsedTime = "";

    // ---- 速度控制 ----
    [ObservableProperty]
    private double _playbackSpeed = 1.0;

    public double[] AvailableSpeeds { get; } = [0.5, 1.0, 2.0, 5.0, 10.0];

    // ---- 正交度校正 ----

    [ObservableProperty]
    private bool _isOrthogonalityCorrectionEnabled;

    [ObservableProperty]
    private ObservableCollection<OrthogonalityParams> _availableProfiles = new();

    [ObservableProperty]
    private OrthogonalityParams? _selectedOrthogonalityProfile;

    [ObservableProperty]
    private OrthogonalityParams? _selectedSecondProfile;

    [ObservableProperty]
    private string _firstChannelIndices = "0,1,2";

    [ObservableProperty]
    private string _secondChannelIndices = "3,4,5";

    public HistoryPlaybackViewModel(IDataStorageService storageService, DataBus dataBus,
        OrthogonalityCorrector orthogonalityCorrector, ICalibrationRepository calibrationRepository)
    {
        _storageService = storageService;
        _dataBus = dataBus;
        _orthogonalityCorrector = orthogonalityCorrector;
        _calibrationRepository = calibrationRepository;
        _dataBus.ConnectionChanged += OnConnectionChanged;
        _dataBus.AcquisitionStarting += OnLiveAcquisitionStartingAsync;
    }

    private bool _isLoaded;
    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded) return;
        _isLoaded = true;
        await LoadAvailableSessionsAsync();
        await LoadOrthogonalityProfilesAsync();
    }

    [RelayCommand]
    private async Task LoadOrthogonalityProfilesAsync()
    {
        try
        {
            var profiles = await _calibrationRepository.GetOrthogonalityProfilesAsync();
            AvailableProfiles.Clear();
            foreach (var p in profiles)
                AvailableProfiles.Add(p);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"加载校正配置列表失败: {ex.Message}");
        }
    }

    // ---- 加载可用会话 ----

    private async Task LoadAvailableSessionsAsync()
    {
        try
        {
            var sessions = await _storageService.GetSessionsAsync();

            RunOnUi(() =>
            {
                AvailableSessions.Clear();
                foreach (var session in sessions)
                {
                    // 仅显示已结束的会话
                    if (session.EndedAt != null)
                    {
                        AvailableSessions.Add(session);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"加载会话列表失败: {ex.Message}");
        }
    }

    // ---- 命令 ----

    [RelayCommand]
    private async Task RefreshSessionsAsync()
    {
        await LoadAvailableSessionsAsync();
    }

    public async Task LoadSessionByIdAsync(string sessionId)
    {
        await LoadAvailableSessionsAsync();
        SelectedSession = AvailableSessions.FirstOrDefault(session => session.Id == sessionId);
        if (SelectedSession == null)
        {
            StatusMessage = "该会话不存在或尚未结束，无法回放。";
            return;
        }
        await LoadSessionAsync();
    }

    [RelayCommand]
    private async Task LoadSessionAsync()
    {
        if (SelectedSession == null || IsLoading) return;
        var session = SelectedSession;

        // 如果正在回放，先停止
        Stop();

        State = PlaybackState.Loading;
        IsLoading = true;

        try
        {
            var readings = await _storageService.GetReadingsAsync(session.Id);
            _readings = readings.OrderBy(r => r.Timestamp).ToArray();
            LoadedSession = session;

            TotalReadings = _readings.Length;
            CurrentIndex = 0;
            Progress = 0;

            if (_readings.Length > 0)
            {
                var duration = _readings[^1].Timestamp - _readings[0].Timestamp;
                TotalDuration = FormatTimeSpan(duration);
                CurrentTime = _readings[0].Timestamp.ToString("HH:mm:ss.fff");
                ElapsedTime = FormatTimeSpan(TimeSpan.Zero);
            }
            else
            {
                TotalDuration = "";
                CurrentTime = "";
                ElapsedTime = "";
            }

            State = PlaybackState.Ready;
            StatusMessage = _readings.Length == 0
                ? "此会话没有可回放的读数。"
                : $"已加载 {session.Name}，{TotalReadings:N0} 条。回放不写入采集数据库。";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"加载会话数据失败: {ex.Message}");
            _readings = [];
            LoadedSession = null;
            TotalReadings = 0;
            CurrentTime = TotalDuration = ElapsedTime = "";
            StatusMessage = $"加载失败：{ex.Message}";
            State = PlaybackState.Ready;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private Task PlayAsync()
    {
        if (_readings.Length == 0 || LoadedSession == null) return Task.CompletedTask;
        if (_dataBus.CurrentConnection != null)
        {
            StatusMessage = "请先断开实时连接，再播放历史数据。";
            return Task.CompletedTask;
        }
        if (!ValidateCorrection()) return Task.CompletedTask;

        if (State == PlaybackState.Ready || State == PlaybackState.Completed)
        {
            if (State == PlaybackState.Completed)
            {
                CurrentIndex = 0;
                Progress = 0;
                _positionAtTimerStart = TimeSpan.Zero;
            }
            BeginPlaybackDisplay();
        }

        // 计算定时器间隔
        StartTimer();

        State = PlaybackState.Playing;
        IsPlaying = true;
        IsPaused = false;
        StatusMessage = "正在回放历史数据；原始采集数据库保持不变。";

        return Task.CompletedTask;
    }

    private bool CanPlay() =>
        _readings.Length > 0 && _dataBus.CurrentConnection == null &&
        State != PlaybackState.Playing && State != PlaybackState.Loading;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _positionAtTimerStart = CurrentPlaybackPosition();
        _playbackClock.Reset();
        _playbackTimer?.Stop();
        State = PlaybackState.Paused;
        IsPlaying = false;
        IsPaused = true;
    }

    private bool CanPause() => State == PlaybackState.Playing;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        EndPlaybackDisplay();
        _positionAtTimerStart = TimeSpan.Zero;

        CurrentIndex = 0;
        Progress = 0;
        State = PlaybackState.Ready;
        IsPlaying = false;
        IsPaused = false;

        if (_readings.Length > 0)
        {
            CurrentTime = _readings[0].Timestamp.ToString("HH:mm:ss.fff");
            ElapsedTime = FormatTimeSpan(TimeSpan.Zero);
        }
    }

    private bool CanStop() =>
        _ownsPlayback || State == PlaybackState.Playing || State == PlaybackState.Paused;

    // ---- 进度条拖动 ----

    public void OnSeekDragStarted()
    {
        _wasPlayingBeforeSeek = State == PlaybackState.Playing;
        if (_wasPlayingBeforeSeek)
        {
            _positionAtTimerStart = CurrentPlaybackPosition();
            _playbackClock.Reset();
            _playbackTimer?.Stop();
        }
    }

    public void OnSeekDragCompleted(double progress)
    {
        SeekTo(progress);
        if (_wasPlayingBeforeSeek && _ownsPlayback && State == PlaybackState.Paused &&
            _dataBus.CurrentConnection == null && ValidateCorrection())
        {
            // 恢复播放
            StartTimer();
            State = PlaybackState.Playing;
            IsPlaying = true;
            IsPaused = false;
        }
        _wasPlayingBeforeSeek = false;
    }

    public void SeekTo(double progress)
    {
        if (_readings.Length == 0 || !double.IsFinite(progress)) return;
        if (_dataBus.CurrentConnection != null)
        {
            StatusMessage = "实时连接期间不能将历史数据送入当前曲线。";
            return;
        }
        if (!ValidateCorrection()) return;

        _playbackTimer?.Stop();
        _playbackClock.Reset();

        progress = Math.Clamp(progress, 0.0, 1.0);
        var targetIndex = (int)(progress * (_readings.Length - 1));

        CurrentIndex = targetIndex;
        Progress = progress;
        CurrentTime = _readings[targetIndex].Timestamp.ToString("HH:mm:ss.fff");
        var elapsed = _readings[targetIndex].Timestamp - _readings[0].Timestamp;
        ElapsedTime = FormatTimeSpan(elapsed);
        _positionAtTimerStart = elapsed;
        BeginPlaybackDisplay();
        try
        {
            PublishPlaybackReading(_readings[targetIndex]);
        }
        catch (Exception ex) when (ex is ArgumentException or ArithmeticException)
        {
            EndPlaybackDisplay();
            State = PlaybackState.Ready;
            IsPlaying = IsPaused = false;
            StatusMessage = $"校正配置或读数无效：{ex.Message}";
            return;
        }
        // 已在定位预览中发布目标点，恢复播放从下一点继续。
        CurrentIndex = targetIndex + 1;
        State = PlaybackState.Paused;
        IsPlaying = false;
        IsPaused = true;
    }

    // ---- 定时器管理 ----

    private void StartTimer()
    {
        _playbackTimer?.Stop();

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _playbackTimer.Tick += OnPlaybackTick;
        _playbackClock.Restart();
        _playbackTimer.Start();
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        if (_dataBus.CurrentConnection != null)
        {
            StopForLiveAcquisition();
            return;
        }
        var position = CurrentPlaybackPosition();
        while (CurrentIndex < _readings.Length &&
               _readings[CurrentIndex].Timestamp - _readings[0].Timestamp <= position)
        {
            try
            {
                PublishPlaybackReading(_readings[CurrentIndex]);
            }
            catch (Exception ex) when (ex is ArgumentException or ArithmeticException)
            {
                Pause();
                StatusMessage = $"校正配置无效：{ex.Message}";
                return;
            }
            CurrentIndex++;
        }

        // 更新进度
        Progress = TotalReadings > 0 ? (double)CurrentIndex / TotalReadings : 0;

        if (CurrentIndex < _readings.Length)
        {
            var displayedIndex = Math.Max(0, CurrentIndex - 1);
            CurrentTime = _readings[displayedIndex].Timestamp.ToString("HH:mm:ss.fff");
            var elapsed = _readings[displayedIndex].Timestamp - _readings[0].Timestamp;
            ElapsedTime = FormatTimeSpan(elapsed);
        }
        else
        {
            // 刚好播完最后一批
            CurrentTime = _readings[^1].Timestamp.ToString("HH:mm:ss.fff");
            var elapsed = _readings[^1].Timestamp - _readings[0].Timestamp;
            ElapsedTime = FormatTimeSpan(elapsed);
            Progress = 1.0;

            EndPlaybackDisplay();
            State = PlaybackState.Completed;
            IsPlaying = false;
            IsPaused = false;
            StatusMessage = "回放完成。历史读数未写入采集数据库。";
        }
    }

    // ---- 速度动态切换 ----

    partial void OnPlaybackSpeedChanging(double oldValue, double newValue)
    {
        if (_playbackClock.IsRunning && double.IsFinite(oldValue) && oldValue > 0)
        {
            _positionAtTimerStart = CurrentPlaybackPosition();
            _playbackClock.Restart();
        }
    }

    partial void OnPlaybackSpeedChanged(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            PlaybackSpeed = 1;
            StatusMessage = "回放速度必须为有限正数，已恢复为 1x。";
        }
    }

    // ---- 状态变化通知命令刷新 ----

    partial void OnStateChanged(PlaybackState value)
    {
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    partial void OnLoadedSessionChanged(SessionInfo? value) => OnPropertyChanged(nameof(LoadedChannelSummary));

    [RelayCommand]
    private void ClearSecondProfile() => SelectedSecondProfile = null;

    // ---- 辅助方法 ----

    private SensorConfig RebuildSensorConfig(SessionInfo session)
    {
        var count = Math.Max(session.ChannelCount, _readings.Select(r => r.ChannelValues.Length).DefaultIfEmpty().Max());
        return new SensorConfig
        {
            Type = SensorType.Generic,
            SampleRate = session.SampleRate,
            ChannelCountOverride = count,
            ChannelNamesOverride = Enumerable.Range(0, count)
                .Select(i => i < session.ChannelNames.Length ? session.ChannelNames[i] : $"CH{i}").ToArray(),
            ChannelUnitsOverride = Enumerable.Range(0, count)
                .Select(i => i < session.ChannelUnits.Length ? session.ChannelUnits[i] : "未知单位").ToArray(),
        };
    }

    private void BeginPlaybackDisplay()
    {
        _dataBus.IsPlaybackMode = true;
        _ownsPlayback = true;
        _dataBus.PublishAcquisitionStarted(RebuildSensorConfig(LoadedSession!));
    }

    private void EndPlaybackDisplay(bool notifyChart = true)
    {
        _playbackTimer?.Stop();
        _playbackTimer = null;
        _playbackClock.Reset();
        if (!_ownsPlayback) return;
        _ownsPlayback = false;
        _dataBus.IsPlaybackMode = false;
        if (notifyChart && _dataBus.CurrentConnection == null)
            _dataBus.PublishPlaybackStopped();
    }

    private TimeSpan CurrentPlaybackPosition()
    {
        var duration = _readings.Length == 0 ? TimeSpan.Zero : _readings[^1].Timestamp - _readings[0].Timestamp;
        var speed = double.IsFinite(PlaybackSpeed) && PlaybackSpeed > 0 ? PlaybackSpeed : 1;
        var milliseconds = _positionAtTimerStart.TotalMilliseconds + _playbackClock.Elapsed.TotalMilliseconds * speed;
        // Preserve the exact final timestamp even when a double conversion would lose a tick.
        return milliseconds >= duration.TotalMilliseconds ? duration : TimeSpan.FromMilliseconds(milliseconds);
    }

    private void PublishPlaybackReading(MagnetometerReading source)
    {
        var reading = source.DeepClone();
        if (IsOrthogonalityCorrectionEnabled && SelectedOrthogonalityProfile != null)
            reading = _orthogonalityCorrector.ApplyToReading(SelectedOrthogonalityProfile,
                SelectedSecondProfile, reading, ReadMagneticChannelIndices(FirstChannelIndices, SelectedOrthogonalityProfile),
                SelectedSecondProfile == null ? null : ReadMagneticChannelIndices(SecondChannelIndices, SelectedSecondProfile));
        // 历史数据只发送到显示总线，不能依赖全局标志阻止它进入原始保存链路。
        _dataBus.PublishProcessedReading(reading);
    }

    private bool ValidateCorrection()
    {
        if (!IsOrthogonalityCorrectionEnabled) return true;
        if (SelectedOrthogonalityProfile == null)
        {
            StatusMessage = "请选择校正参数，或关闭正交度校正。";
            return false;
        }
        try
        {
            var first = ReadMagneticChannelIndices(FirstChannelIndices, SelectedOrthogonalityProfile);
            var second = SelectedSecondProfile == null ? null : ReadMagneticChannelIndices(SecondChannelIndices, SelectedSecondProfile);
            foreach (var count in _readings.Select(r => r.ChannelValues.Length).Distinct())
                _orthogonalityCorrector.ApplyToReading(SelectedOrthogonalityProfile,
                    SelectedSecondProfile, new MagnetometerReading { ChannelValues = new double[count] }, first, second);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or ArithmeticException)
        {
            StatusMessage = $"校正配置无效：{ex.Message}";
            return false;
        }
    }

    private static int[] ParseChannelIndices(string text)
    {
        var parts = text.Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts.Any(part => !int.TryParse(part, out _)))
            throw new ArgumentException("每组请输入三个从 0 开始的通道索引，例如 0,1,2。");
        return parts.Select(int.Parse).ToArray();
    }

    private int[] ReadMagneticChannelIndices(string text, OrthogonalityParams profile)
    {
        var indices = ParseChannelIndices(text);
        var units = LoadedSession?.ChannelUnits ?? [];
        if (indices.Any(index => index < 0 || index >= units.Length || string.IsNullOrWhiteSpace(units[index])))
            throw new ArgumentException("所选通道缺少有效单位，不能确认磁场三轴；请关闭回放校正。");
        var selectedUnits = indices.Select(index => OrthogonalityParams.CanonicalUnit(units[index])).ToArray();
        if (selectedUnits.Distinct().Count() != 1 || string.IsNullOrEmpty(selectedUnits[0]))
            throw new ArgumentException("所选三个通道必须使用相同的磁场单位，不能包含温度等辅助通道。");
        profile.ValidateUnit(selectedUnits[0]);
        return indices;
    }

    private Task OnLiveAcquisitionStartingAsync(SensorConfig config)
    {
        RunOnUi(StopForLiveAcquisition);
        return Task.CompletedTask;
    }

    private void OnConnectionChanged(IDeviceConnection? connection)
    {
        RunOnUi(() =>
        {
            if (connection != null) StopForLiveAcquisition();
            PlayCommand.NotifyCanExecuteChanged();
        });
    }

    private void StopForLiveAcquisition()
    {
        if (!_ownsPlayback) return;
        EndPlaybackDisplay(notifyChart: false);
        _positionAtTimerStart = TimeSpan.Zero;
        CurrentIndex = 0;
        Progress = 0;
        State = PlaybackState.Ready;
        IsPlaying = false;
        IsPaused = false;
        StatusMessage = "已停止历史回放，当前曲线切换到实时采集。";
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        EndPlaybackDisplay(notifyChart: false);
        _dataBus.ConnectionChanged -= OnConnectionChanged;
        _dataBus.AcquisitionStarting -= OnLiveAcquisitionStartingAsync;
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return ts.ToString(@"hh\:mm\:ss\.fff");
        return ts.ToString(@"mm\:ss\.fff");
    }
}
