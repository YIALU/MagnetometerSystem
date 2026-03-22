# D-3: 历史数据回放

## 基本信息

| 属性     | 值                                          |
| -------- | ------------------------------------------- |
| 任务编号 | D-3                                         |
| 优先级   | P0                                          |
| 阶段     | Phase 3 - 数据持久化                        |
| 流       | Stream B (Agent 2)                          |
| 前置依赖 | D-1 (SQLite 数据库存储服务), D-2 (会话管理) |
| 预估工时 | 6-8 小时                                    |

## 功能需求

### 概述
实现历史采集数据的回放功能。从数据库加载指定会话的数据，通过 `DataBus` 以可控速度重新发布，复用 `RealtimeChartViewModel` 进行可视化展示。用户可调整回放速度、暂停/继续、拖动进度条跳转到任意位置。

### 详细需求

1. **会话选择**
   - 提供下拉列表展示所有已结束的会话（`EndedAt != null`）
   - 显示会话名称、传感器类型、数据点数等关键信息
   - 选择会话后自动加载数据（异步，显示加载进度）

2. **回放控制**
   - **播放 (Play)**: 开始或恢复回放，按时间戳间隔通过 `DataBus.PublishReading` 发布数据
   - **暂停 (Pause)**: 暂停回放，保持当前进度位置
   - **停止 (Stop)**: 停止回放，重置进度到起始位置，发布 `AcquisitionStopped`
   - 播放开始时发布 `DataBus.PublishAcquisitionStarted`（使用会话对应的 `SensorConfig`），使 `RealtimeChartVM` 自动配置通道

3. **速度控制**
   - 支持倍速: 0.5x, 1x, 2x, 5x, 10x
   - 默认 1x（按原始采样率回放）
   - 可在回放过程中动态切换速度

4. **进度控制**
   - 进度条显示当前回放位置（0% ~ 100%）
   - 显示当前时间戳和总时长
   - 支持拖动进度条跳转到任意位置（drag-to-seek）
   - 拖动时暂停回放，释放后从新位置继续

5. **数据发布机制**
   - 使用 `DispatcherTimer` 或 `System.Threading.Timer` 驱动回放
   - 定时器间隔 = 原始采样间隔 / 速度倍数
   - 每次定时器触发，从已加载的数据数组中取出下一条（或多条）读数通过 DataBus 发布
   - 确保数据发布在 UI 线程（因为 RealtimeChartVM 需要更新 UI 绑定属性）

6. **状态指示**
   - 显示当前回放状态: 就绪 / 加载中 / 播放中 / 已暂停 / 已完成
   - 回放完成时自动停止并显示"已完成"状态

## 接口契约

### HistoryPlaybackViewModel

```csharp
// 文件: src/MagnetometerSystem.App/ViewModels/HistoryPlaybackViewModel.cs
namespace MagnetometerSystem.App.ViewModels;

public partial class HistoryPlaybackViewModel : ObservableObject
{
    // ---- 会话选择 ----
    public ObservableCollection<SessionInfo> AvailableSessions { get; }
    [ObservableProperty] private SessionInfo? _selectedSession;

    // ---- 回放状态 ----
    [ObservableProperty] private PlaybackState _state = PlaybackState.Ready;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _isLoading;

    // ---- 进度 ----
    [ObservableProperty] private double _progress;        // 0.0 ~ 1.0
    [ObservableProperty] private int _currentIndex;       // 当前数据索引
    [ObservableProperty] private int _totalReadings;      // 总读数
    [ObservableProperty] private string _currentTime = "";     // 当前时间戳显示
    [ObservableProperty] private string _totalDuration = "";   // 总时长显示
    [ObservableProperty] private string _elapsedTime = "";     // 已回放时长

    // ---- 速度控制 ----
    [ObservableProperty] private double _playbackSpeed = 1.0;
    public double[] AvailableSpeeds { get; } = [0.5, 1.0, 2.0, 5.0, 10.0];

    // ---- 命令 ----
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private Task PlayAsync();

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause();

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop();

    [RelayCommand]
    private Task LoadSessionAsync(SessionInfo session);

    // ---- 进度条拖动 ----
    public void SeekTo(double progress);  // 0.0 ~ 1.0

    // ---- 构造函数依赖 ----
    public HistoryPlaybackViewModel(
        IDataStorageService storageService,
        DataBus dataBus);
}

public enum PlaybackState
{
    Ready,      // 初始/已停止，可选择会话
    Loading,    // 正在从数据库加载数据
    Playing,    // 回放中
    Paused,     // 已暂停
    Completed   // 回放完成
}
```

### MainViewModel 变更

```csharp
// 新增属性:
public HistoryPlaybackViewModel HistoryPlaybackVM { get; }

// 构造函数参数新增:
public MainViewModel(
    ConnectionViewModel connectionVm,
    RealtimeChartViewModel realtimeChartVm,
    SessionListViewModel sessionListVm,
    HistoryPlaybackViewModel historyPlaybackVm)  // 新增

// 新增导航命令:
[RelayCommand]
private void NavigateToHistoryPlayback()
{
    CurrentView = HistoryPlaybackVM;
}
```

## 文件清单

### 新建文件

| 文件路径 | 说明 |
| -------- | ---- |
| `src/MagnetometerSystem.App/ViewModels/HistoryPlaybackViewModel.cs` | 历史回放 ViewModel |
| `src/MagnetometerSystem.App/Views/HistoryPlaybackView.xaml` | 历史回放 View |
| `src/MagnetometerSystem.App/Views/HistoryPlaybackView.xaml.cs` | View code-behind |

### 修改文件

| 文件路径 | 修改内容 |
| -------- | -------- |
| `src/MagnetometerSystem.App/ViewModels/MainViewModel.cs` | 添加 `HistoryPlaybackVM` 属性和 `NavigateToHistoryPlayback` 命令 |
| `src/MagnetometerSystem.App/MainWindow.xaml` | 启用"历史回放"导航按钮，绑定命令；注册 DataTemplate |
| `src/MagnetometerSystem.App/App.xaml.cs` | 注册 `HistoryPlaybackViewModel` 到 DI 容器 |

## 实现指南

### 1. 数据加载

选中会话后异步加载所有读数到内存数组，供回放时快速顺序访问：

```csharp
private MagnetometerReading[] _readings = [];

private async Task LoadSessionDataAsync()
{
    if (SelectedSession == null) return;

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
        }

        State = PlaybackState.Ready;
    }
    finally
    {
        IsLoading = false;
    }
}
```

### 2. 回放定时器

使用 `DispatcherTimer` 确保回放数据在 UI 线程发布：

```csharp
private DispatcherTimer? _playbackTimer;

private Task PlayAsync()
{
    if (_readings.Length == 0) return Task.CompletedTask;

    if (State == PlaybackState.Ready || State == PlaybackState.Completed)
    {
        // 从头开始：发布 AcquisitionStarted
        var sensorConfig = RebuildSensorConfig(SelectedSession!);
        _dataBus.PublishAcquisitionStarted(sensorConfig);
        CurrentIndex = 0;
    }

    // 计算定时器间隔
    var baseInterval = 1000.0 / SelectedSession!.SampleRate;  // 毫秒
    var adjustedInterval = baseInterval / PlaybackSpeed;

    _playbackTimer = new DispatcherTimer
    {
        Interval = TimeSpan.FromMilliseconds(Math.Max(adjustedInterval, 1))
    };
    _playbackTimer.Tick += OnPlaybackTick;
    _playbackTimer.Start();

    State = PlaybackState.Playing;
    IsPlaying = true;
    IsPaused = false;

    return Task.CompletedTask;
}

private void OnPlaybackTick(object? sender, EventArgs e)
{
    if (CurrentIndex >= _readings.Length)
    {
        // 回放完成
        Stop();
        State = PlaybackState.Completed;
        return;
    }

    // 根据速度倍数，可能一次发布多条（高倍速时定时器精度不够）
    var count = PlaybackSpeed >= 5.0 ? (int)PlaybackSpeed : 1;

    for (var i = 0; i < count && CurrentIndex < _readings.Length; i++)
    {
        _dataBus.PublishReading(_readings[CurrentIndex]);
        CurrentIndex++;
    }

    // 更新进度
    Progress = (double)CurrentIndex / TotalReadings;
    if (CurrentIndex < _readings.Length)
    {
        CurrentTime = _readings[CurrentIndex].Timestamp.ToString("HH:mm:ss.fff");
        var elapsed = _readings[CurrentIndex].Timestamp - _readings[0].Timestamp;
        ElapsedTime = FormatTimeSpan(elapsed);
    }
}
```

### 3. 暂停与停止

```csharp
private void Pause()
{
    _playbackTimer?.Stop();
    State = PlaybackState.Paused;
    IsPlaying = false;
    IsPaused = true;
}

private void Stop()
{
    _playbackTimer?.Stop();
    _playbackTimer = null;

    _dataBus.PublishAcquisitionStopped();

    CurrentIndex = 0;
    Progress = 0;
    State = PlaybackState.Ready;
    IsPlaying = false;
    IsPaused = false;
}
```

### 4. 进度条拖动（Seek）

```csharp
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
```

在 XAML 中，`Slider` 的拖动事件处理:

```xml
<Slider Minimum="0" Maximum="1" Value="{Binding Progress}"
        Thumb.DragStarted="OnSeekDragStarted"
        Thumb.DragCompleted="OnSeekDragCompleted"/>
```

code-behind:
```csharp
private void OnSeekDragStarted(object sender, DragStartedEventArgs e)
{
    // 拖动时暂停回放
    if (ViewModel.IsPlaying) ViewModel.Pause();
}

private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e)
{
    var slider = (Slider)sender;
    ViewModel.SeekTo(slider.Value);
}
```

### 5. 速度动态切换

当回放过程中切换速度，需重新设置定时器间隔:

```csharp
partial void OnPlaybackSpeedChanged(double value)
{
    if (_playbackTimer != null && _playbackTimer.IsEnabled)
    {
        var baseInterval = 1000.0 / SelectedSession!.SampleRate;
        var adjustedInterval = baseInterval / value;
        _playbackTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(adjustedInterval, 1));
    }
}
```

### 6. 从 SessionInfo 重建 SensorConfig

回放开始时需构造 `SensorConfig` 传递给 `DataBus.PublishAcquisitionStarted`:

```csharp
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
```

### 7. HistoryPlaybackView.xaml 布局

```xml
<UserControl x:Class="MagnetometerSystem.App.Views.HistoryPlaybackView" ...>
    <DockPanel Margin="10">
        <!-- 顶部: 会话选择 + 状态 -->
        <StackPanel DockPanel.Dock="Top" Margin="0,0,0,10">
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="选择会话:" VerticalAlignment="Center" Margin="0,0,10,0"/>
                <ComboBox ItemsSource="{Binding AvailableSessions}"
                          SelectedItem="{Binding SelectedSession}"
                          DisplayMemberPath="Name" Width="300" Margin="0,0,10,0"/>
                <TextBlock VerticalAlignment="Center">
                    <Run Text="状态: "/>
                    <Run Text="{Binding State, Mode=OneWay}" FontWeight="Bold"/>
                </TextBlock>
            </StackPanel>
        </StackPanel>

        <!-- 底部: 控制栏 -->
        <StackPanel DockPanel.Dock="Bottom" Margin="0,10,0,0">
            <!-- 进度条 -->
            <DockPanel Margin="0,0,0,5">
                <TextBlock DockPanel.Dock="Left" Text="{Binding ElapsedTime}" Width="80"/>
                <TextBlock DockPanel.Dock="Right" Text="{Binding TotalDuration}" Width="80"
                           TextAlignment="Right"/>
                <Slider Minimum="0" Maximum="1" Value="{Binding Progress}"
                        Thumb.DragStarted="OnSeekDragStarted"
                        Thumb.DragCompleted="OnSeekDragCompleted"/>
            </DockPanel>

            <!-- 控制按钮 + 速度选择 -->
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Center">
                <Button Content="播放" Command="{Binding PlayCommand}" Width="60" Margin="5"/>
                <Button Content="暂停" Command="{Binding PauseCommand}" Width="60" Margin="5"/>
                <Button Content="停止" Command="{Binding StopCommand}" Width="60" Margin="5"/>
                <TextBlock Text="速度:" VerticalAlignment="Center" Margin="20,0,5,0"/>
                <ComboBox ItemsSource="{Binding AvailableSpeeds}"
                          SelectedItem="{Binding PlaybackSpeed}" Width="80">
                    <ComboBox.ItemTemplate>
                        <DataTemplate>
                            <TextBlock Text="{Binding StringFormat={}{0}x}"/>
                        </DataTemplate>
                    </ComboBox.ItemTemplate>
                </ComboBox>
                <TextBlock VerticalAlignment="Center" Margin="20,0,0,0">
                    <Run Text="{Binding CurrentIndex, Mode=OneWay, StringFormat=N0}"/>
                    <Run Text=" / "/>
                    <Run Text="{Binding TotalReadings, Mode=OneWay, StringFormat=N0}"/>
                </TextBlock>
            </StackPanel>
        </StackPanel>

        <!-- 中央: 图表区 (复用 RealtimeChartView) -->
        <!-- 由于数据通过 DataBus 发布，RealtimeChartVM 会自动响应 -->
        <ContentControl Content="{Binding DataContext.RealtimeChartVM,
            RelativeSource={RelativeSource AncestorType=Window}}"
            Margin="0,10"/>
    </DockPanel>
</UserControl>
```

> **关于图表复用**: 回放数据通过 `DataBus.PublishReading` 发布后，已订阅 `DataBus` 的 `RealtimeChartViewModel` 会自动接收并更新图表。无需额外集成代码。但需注意：回放前应调用 `Stop` 确保实时采集已停止，避免回放数据与实时数据混淆。

### 8. MainWindow.xaml 修改

将已有的灰置"历史回放"按钮启用并绑定命令:

```xml
<!-- 替换原有的 IsEnabled="False" 按钮 -->
<Button Content="历史回放" Margin="3" Padding="8,6" HorizontalContentAlignment="Left"
        Command="{Binding NavigateToHistoryPlaybackCommand}"/>
```

注册 DataTemplate:

```xml
<DataTemplate DataType="{x:Type vm:HistoryPlaybackViewModel}">
    <views:HistoryPlaybackView/>
</DataTemplate>
```

### 9. CanExecute 逻辑

```csharp
private bool CanPlay() =>
    _readings.Length > 0 && State != PlaybackState.Playing && State != PlaybackState.Loading;

private bool CanPause() => State == PlaybackState.Playing;

private bool CanStop() =>
    State == PlaybackState.Playing || State == PlaybackState.Paused;
```

每次 `State` 变化时需通知命令刷新:

```csharp
partial void OnStateChanged(PlaybackState value)
{
    PlayCommand.NotifyCanExecuteChanged();
    PauseCommand.NotifyCanExecuteChanged();
    StopCommand.NotifyCanExecuteChanged();
}
```

## 验收标准

| # | 验收项 | 通过条件 |
| - | ------ | -------- |
| 1 | 会话列表 | 下拉框展示所有已结束的会话，包含名称和基本信息 |
| 2 | 数据加载 | 选择会话后异步加载数据，加载过程中显示 Loading 状态 |
| 3 | 播放功能 | 点击播放后，图表开始按原始采样率显示历史数据 |
| 4 | 暂停功能 | 点击暂停后，图表停止更新，进度保持不变 |
| 5 | 继续播放 | 暂停后再次点击播放，从当前位置继续回放 |
| 6 | 停止功能 | 点击停止后，图表清空，进度重置为 0 |
| 7 | 速度调节 | 0.5x 时回放明显变慢，10x 时明显加速，速度切换立即生效 |
| 8 | 进度条显示 | 回放过程中进度条平滑移动，时间标签正确更新 |
| 9 | 拖动跳转 | 拖动进度条到任意位置，回放从该位置继续 |
| 10 | 回放完成 | 数据全部回放后自动停止，状态显示"已完成" |
| 11 | 图表复用 | 回放数据通过 DataBus 发布，RealtimeChartVM 正确渲染 |
| 12 | 导航集成 | 主窗口"历史回放"按钮可用，点击切换到回放页面 |

## 单元测试要求

测试项目: `tests/MagnetometerSystem.App.Tests/`

### 测试类: `HistoryPlaybackViewModelTests`

| 测试方法 | 验证内容 |
| -------- | -------- |
| `LoadSession_SetsReadingsAndProgress` | 加载会话后 `TotalReadings > 0`，`Progress == 0`，`State == Ready` |
| `Play_PublishesAcquisitionStarted` | 播放时 DataBus 收到 `AcquisitionStarted` 事件，SensorConfig 匹配会话参数 |
| `Play_PublishesReadingsSequentially` | 播放后 DataBus 按顺序收到 readings，时间戳递增 |
| `Pause_StopsPublishing` | 暂停后不再收到新的 reading 事件 |
| `Stop_ResetsProgress` | 停止后 `CurrentIndex == 0`，`Progress == 0`，`State == Ready` |
| `Stop_PublishesAcquisitionStopped` | 停止时 DataBus 收到 `AcquisitionStopped` 事件 |
| `SpeedChange_AdjustsInterval` | 从 1x 切换到 2x，定时器间隔减半 |
| `SeekTo_UpdatesCurrentIndex` | 调用 `SeekTo(0.5)` 后 `CurrentIndex` 约等于 `TotalReadings / 2` |
| `PlaybackCompleted_AutoStops` | 回放到最后一条数据后自动变为 Completed 状态 |
| `CanPlay_FalseWhenPlaying` | 正在播放时 `CanPlay()` 返回 false |
| `CanPause_TrueOnlyWhenPlaying` | 仅在播放状态下 `CanPause()` 返回 true |
| `AvailableSessions_ExcludesActiveSessions` | 列表中不包含 `EndedAt == null` 的会话 |
