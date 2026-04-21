using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Calibration;
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
public partial class HistoryPlaybackViewModel : ObservableObject
{
    private readonly IDataStorageService _storageService;
    private readonly DataBus _dataBus;
    private readonly OrthogonalityCorrector _orthogonalityCorrector;
    private readonly ICalibrationRepository _calibrationRepository;

    // ---- 内部数据 ----
    private MagnetometerReading[] _readings = [];
    private DispatcherTimer? _playbackTimer;
    private bool _wasPlayingBeforeSeek;

    // ---- 会话选择 ----
    public ObservableCollection<SessionInfo> AvailableSessions { get; } = new();

    [ObservableProperty]
    private SessionInfo? _selectedSession;

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

    public HistoryPlaybackViewModel(IDataStorageService storageService, DataBus dataBus,
        OrthogonalityCorrector orthogonalityCorrector, ICalibrationRepository calibrationRepository)
    {
        _storageService = storageService;
        _dataBus = dataBus;
        _orthogonalityCorrector = orthogonalityCorrector;
        _calibrationRepository = calibrationRepository;

        // 延迟加载：用户首次切换到回放页面时才加载
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

            Application.Current?.Dispatcher.Invoke(() =>
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

    [RelayCommand]
    private async Task LoadSessionAsync()
    {
        if (SelectedSession == null) return;

        // 如果正在回放，先停止
        if (State == PlaybackState.Playing || State == PlaybackState.Paused)
        {
            Stop();
        }

        State = PlaybackState.Loading;
        IsLoading = true;

        try
        {
            var readings = await _storageService.GetReadingsAsync(SelectedSession.Id);
            _readings = readings.OrderBy(r => r.Timestamp).ToArray();

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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"加载会话数据失败: {ex.Message}");
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
        if (_readings.Length == 0) return Task.CompletedTask;

        _dataBus.IsPlaybackMode = true;

        if (State == PlaybackState.Ready || State == PlaybackState.Completed)
        {
            // 从头开始或从完成状态重新开始：发布 AcquisitionStarted
            if (State == PlaybackState.Completed)
            {
                CurrentIndex = 0;
                Progress = 0;
            }

            var sensorConfig = RebuildSensorConfig(SelectedSession!);
            _dataBus.PublishAcquisitionStarted(sensorConfig);
        }

        // 计算定时器间隔
        StartTimer();

        State = PlaybackState.Playing;
        IsPlaying = true;
        IsPaused = false;

        return Task.CompletedTask;
    }

    private bool CanPlay() =>
        _readings.Length > 0 && State != PlaybackState.Playing && State != PlaybackState.Loading;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _playbackTimer?.Stop();
        State = PlaybackState.Paused;
        IsPlaying = false;
        IsPaused = true;
    }

    private bool CanPause() => State == PlaybackState.Playing;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _playbackTimer?.Stop();
        _playbackTimer = null;

        _dataBus.IsPlaybackMode = false;
        _dataBus.PublishAcquisitionStopped();

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
        State == PlaybackState.Playing || State == PlaybackState.Paused;

    // ---- 进度条拖动 ----

    public void OnSeekDragStarted()
    {
        _wasPlayingBeforeSeek = State == PlaybackState.Playing;
        if (_wasPlayingBeforeSeek)
        {
            _playbackTimer?.Stop();
        }
    }

    public void OnSeekDragCompleted(double progress)
    {
        SeekTo(progress);
        if (_wasPlayingBeforeSeek)
        {
            // 恢复播放
            StartTimer();
            State = PlaybackState.Playing;
            IsPlaying = true;
            IsPaused = false;
        }
    }

    public void SeekTo(double progress)
    {
        if (_readings.Length == 0) return;

        progress = Math.Clamp(progress, 0.0, 1.0);
        var targetIndex = (int)(progress * (_readings.Length - 1));

        CurrentIndex = targetIndex;
        Progress = progress;
        CurrentTime = _readings[targetIndex].Timestamp.ToString("HH:mm:ss.fff");
        var elapsed = _readings[targetIndex].Timestamp - _readings[0].Timestamp;
        ElapsedTime = FormatTimeSpan(elapsed);
    }

    // ---- 定时器管理 ----

    private void StartTimer()
    {
        _playbackTimer?.Stop();

        var baseInterval = SelectedSession!.SampleRate > 0
            ? 1000.0 / SelectedSession.SampleRate
            : 100.0;
        var adjustedInterval = baseInterval / PlaybackSpeed;

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(adjustedInterval, 1))
        };
        _playbackTimer.Tick += OnPlaybackTick;
        _playbackTimer.Start();
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        if (CurrentIndex >= _readings.Length)
        {
            // 回放完成
            _playbackTimer?.Stop();
            _playbackTimer = null;
            _dataBus.IsPlaybackMode = false;
            State = PlaybackState.Completed;
            IsPlaying = false;
            IsPaused = false;
            return;
        }

        // 根据速度倍数，可能一次发布多条（高倍速时定时器精度不够）
        var count = PlaybackSpeed >= 5.0 ? (int)PlaybackSpeed : 1;

        for (var i = 0; i < count && CurrentIndex < _readings.Length; i++)
        {
            var reading = _readings[CurrentIndex];

            // 应用正交度校正
            if (IsOrthogonalityCorrectionEnabled && SelectedOrthogonalityProfile != null)
            {
                reading = _orthogonalityCorrector.ApplyToReading(
                    SelectedOrthogonalityProfile, SelectedSecondProfile, reading);
            }

            _dataBus.PublishReading(reading);
            CurrentIndex++;
        }

        // 更新进度
        Progress = TotalReadings > 0 ? (double)CurrentIndex / TotalReadings : 0;

        if (CurrentIndex < _readings.Length)
        {
            CurrentTime = _readings[CurrentIndex].Timestamp.ToString("HH:mm:ss.fff");
            var elapsed = _readings[CurrentIndex].Timestamp - _readings[0].Timestamp;
            ElapsedTime = FormatTimeSpan(elapsed);
        }
        else
        {
            // 刚好播完最后一批
            CurrentTime = _readings[^1].Timestamp.ToString("HH:mm:ss.fff");
            var elapsed = _readings[^1].Timestamp - _readings[0].Timestamp;
            ElapsedTime = FormatTimeSpan(elapsed);
            Progress = 1.0;

            _playbackTimer?.Stop();
            _playbackTimer = null;
            State = PlaybackState.Completed;
            IsPlaying = false;
            IsPaused = false;
        }
    }

    // ---- 速度动态切换 ----

    partial void OnPlaybackSpeedChanged(double value)
    {
        if (_playbackTimer != null && _playbackTimer.IsEnabled && SelectedSession != null)
        {
            var baseInterval = SelectedSession.SampleRate > 0
                ? 1000.0 / SelectedSession.SampleRate
                : 100.0;
            var adjustedInterval = baseInterval / value;
            _playbackTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(adjustedInterval, 1));
        }
    }

    // ---- 状态变化通知命令刷新 ----

    partial void OnStateChanged(PlaybackState value)
    {
        PlayCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    // ---- 辅助方法 ----

    private static SensorConfig RebuildSensorConfig(SessionInfo session)
    {
        return new SensorConfig
        {
            Type = session.SensorType,
            SampleRate = session.SampleRate,
            ChannelCountOverride = session.ChannelCount,
            ChannelNamesOverride = session.ChannelNames,
        };
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return ts.ToString(@"hh\:mm\:ss\.fff");
        return ts.ToString(@"mm\:ss\.fff");
    }
}
