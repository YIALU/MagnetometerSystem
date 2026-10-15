# D-2: 会话管理

## 基本信息

| 属性     | 值                                |
| -------- | --------------------------------- |
| 任务编号 | D-2                               |
| 优先级   | P0                                |
| 阶段     | Phase 3 - 数据持久化              |
| 流       | Stream A (Agent 1)                |
| 前置依赖 | D-1 (SQLite 数据库存储服务)       |
| 预估工时 | 4-6 小时                          |

## 功能需求

### 概述
实现采集会话的完整管理功能，包括：自动跟随采集生命周期创建/结束会话、会话列表浏览与搜索、会话元数据编辑、会话删除。提供独立的 `SessionListView` 页面并集成到主窗口导航中。

### 详细需求

1. **自动会话生命周期**
   - 订阅 `DataBus.AcquisitionStarted` 事件：自动调用 `IDataStorageService.StartSessionAsync`，会话名称使用格式 `"采集_{yyyy-MM-dd_HH:mm:ss}"`
   - 订阅 `DataBus.AcquisitionStopped` 事件：自动调用 `IDataStorageService.EndSessionAsync`
   - 采集过程中，每收到 `DataBus.ReadingReceived` 事件，收集读数并定期调用 `SaveReadingsAsync` 批量保存
   - 当前活跃会话 ID 需保存在 ViewModel 中，供其他组件引用

2. **会话列表展示**
   - 使用 WPF `DataGrid` 展示所有历史会话
   - 显示列: 会话名称、开始时间、结束时间、传感器类型、采样率、数据点数、备注
   - 按开始时间降序排列（最新会话在最前）
   - 支持选中行高亮

3. **搜索与筛选**
   - 按名称关键字搜索（实时过滤，使用 `CollectionViewSource`）
   - 按日期范围筛选（DatePicker 选择起止日期）
   - 按传感器类型筛选（ComboBox 下拉选择）
   - 筛选条件可叠加

4. **会话操作**
   - **重命名**: 双击会话名称列可直接编辑，或通过右键菜单"重命名"弹出对话框
   - **添加备注**: 右键菜单"编辑备注"弹出多行文本输入对话框
   - **删除**: 右键菜单"删除"或键盘 Delete 键，弹出确认对话框 "确定要删除会话 '{Name}' 及其 {TotalReadings} 条数据吗？此操作不可恢复。"
   - **导出**: 右键菜单"导出 CSV"（调用 D-4 中的导出功能，若 D-4 未完成则按钮灰置）
   - **查看/回放**: 右键菜单"历史回放"（跳转到 D-3 的回放页面，若 D-3 未完成则按钮灰置）

5. **导航集成**
   - 在 `MainViewModel` 中新增 `SessionListVM` 属性和 `NavigateToSessionList` 命令
   - 在 `MainWindow.xaml` 左侧导航栏新增"数据管理"按钮
   - 在 `ContentControl.Resources` 中注册 `SessionListViewModel -> SessionListView` 的 DataTemplate

## 接口契约

### SessionListViewModel

```csharp
// 文件: src/MagnetometerSystem.App/ViewModels/SessionListViewModel.cs
namespace MagnetometerSystem.App.ViewModels;

public partial class SessionListViewModel : ObservableObject
{
    // ---- 数据集合 ----
    public ObservableCollection<SessionInfo> Sessions { get; }
    public ICollectionView SessionsView { get; }  // 支持排序和筛选的视图

    // ---- 选中项 ----
    [ObservableProperty] private SessionInfo? _selectedSession;

    // ---- 搜索/筛选 ----
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private DateTime? _filterStartDate;
    [ObservableProperty] private DateTime? _filterEndDate;
    [ObservableProperty] private SensorType? _filterSensorType;

    // ---- 当前活跃会话 ----
    [ObservableProperty] private string? _activeSessionId;
    [ObservableProperty] private bool _isRecording;

    // ---- 命令 ----
    [RelayCommand] private Task RefreshSessionsAsync();
    [RelayCommand] private Task RenameSessionAsync(SessionInfo session);
    [RelayCommand] private Task EditNotesAsync(SessionInfo session);
    [RelayCommand] private Task DeleteSessionAsync(SessionInfo session);
    [RelayCommand] private void ExportSession(SessionInfo session);
    [RelayCommand] private void PlaybackSession(SessionInfo session);

    // ---- 构造函数依赖 ----
    public SessionListViewModel(
        IDataStorageService storageService,
        DataBus dataBus);
}
```

### MainViewModel 变更

```csharp
// 在 MainViewModel 中新增:
public SessionListViewModel SessionListVM { get; }

// 构造函数参数新增:
public MainViewModel(
    ConnectionViewModel connectionVm,
    RealtimeChartViewModel realtimeChartVm,
    SessionListViewModel sessionListVm)  // 新增

// 新增导航命令:
[RelayCommand]
private void NavigateToSessionList()
{
    CurrentView = SessionListVM;
}
```

## 文件清单

### 新建文件

| 文件路径 | 说明 |
| -------- | ---- |
| `src/MagnetometerSystem.App/ViewModels/SessionListViewModel.cs` | 会话列表 ViewModel |
| `src/MagnetometerSystem.App/Views/SessionListView.xaml` | 会话列表 View (DataGrid + 筛选控件) |
| `src/MagnetometerSystem.App/Views/SessionListView.xaml.cs` | View code-behind (最小化，仅用于必要的 UI 交互) |

### 修改文件

| 文件路径 | 修改内容 |
| -------- | -------- |
| `src/MagnetometerSystem.App/ViewModels/MainViewModel.cs` | 添加 `SessionListVM` 属性和 `NavigateToSessionList` 命令 |
| `src/MagnetometerSystem.App/MainWindow.xaml` | 添加"数据管理"导航按钮和 `SessionListView` 的 DataTemplate |
| `src/MagnetometerSystem.App/App.xaml.cs` | 注册 `SessionListViewModel` 到 DI 容器 |

## 实现指南

### 1. 自动会话生命周期管理

在 `SessionListViewModel` 构造函数中订阅 DataBus 事件:

```csharp
public SessionListViewModel(IDataStorageService storageService, DataBus dataBus)
{
    _storageService = storageService;
    _dataBus = dataBus;

    Sessions = new ObservableCollection<SessionInfo>();
    SessionsView = CollectionViewSource.GetDefaultView(Sessions);
    SessionsView.Filter = FilterSession;
    SessionsView.SortDescriptions.Add(
        new SortDescription(nameof(SessionInfo.StartedAt), ListSortDirection.Descending));

    // 订阅采集事件
    _dataBus.AcquisitionStarted += OnAcquisitionStarted;
    _dataBus.AcquisitionStopped += OnAcquisitionStopped;
    _dataBus.ReadingReceived += OnReadingReceived;

    // 初始加载会话列表
    _ = RefreshSessionsAsync();
}

private async void OnAcquisitionStarted(SensorConfig config)
{
    var name = $"采集_{DateTime.Now:yyyy-MM-dd_HH:mm:ss}";
    // 需要从 ConnectionViewModel 获取当前 ConnectionConfig
    // 通过 DI 或 DataBus 传递
    ActiveSessionId = await _storageService.StartSessionAsync(name, config, _currentConnectionConfig);
    IsRecording = true;
    await RefreshSessionsAsync();
}

private async void OnAcquisitionStopped()
{
    if (ActiveSessionId != null)
    {
        await _storageService.EndSessionAsync(ActiveSessionId);
        ActiveSessionId = null;
        IsRecording = false;
        await RefreshSessionsAsync();
    }
}
```

### 2. 读数缓冲与批量保存

采集过程中需对 `ReadingReceived` 事件进行缓冲，避免逐条保存：

```csharp
private readonly List<MagnetometerReading> _readingBuffer = new(500);
private readonly object _bufferLock = new();
private DateTime _lastFlushTime = DateTime.MinValue;
private const int FlushIntervalMs = 200;  // 每 200ms 刷盘一次
private const int FlushBatchSize = 500;   // 或满 500 条刷盘

private void OnReadingReceived(MagnetometerReading reading)
{
    if (ActiveSessionId == null) return;

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
```

### 3. 搜索与筛选

使用 `ICollectionView.Filter` 实现客户端筛选：

```csharp
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

// 当筛选属性变化时刷新视图
partial void OnSearchTextChanged(string value) => SessionsView.Refresh();
partial void OnFilterStartDateChanged(DateTime? value) => SessionsView.Refresh();
partial void OnFilterEndDateChanged(DateTime? value) => SessionsView.Refresh();
partial void OnFilterSensorTypeChanged(SensorType? value) => SessionsView.Refresh();
```

### 4. SessionListView.xaml 布局

```xml
<UserControl x:Class="MagnetometerSystem.App.Views.SessionListView"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <DockPanel Margin="10">
        <!-- 顶部: 筛选栏 -->
        <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,10">
            <TextBox Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged}"
                     Width="200" Margin="0,0,10,0"
                     Tag="搜索会话名称..."/>
            <TextBlock Text="从:" VerticalAlignment="Center" Margin="0,0,5,0"/>
            <DatePicker SelectedDate="{Binding FilterStartDate}" Width="130" Margin="0,0,10,0"/>
            <TextBlock Text="到:" VerticalAlignment="Center" Margin="0,0,5,0"/>
            <DatePicker SelectedDate="{Binding FilterEndDate}" Width="130" Margin="0,0,10,0"/>
            <ComboBox SelectedValue="{Binding FilterSensorType}" Width="150" Margin="0,0,10,0"
                      DisplayMemberPath="." SelectedValuePath="."/>
            <Button Content="刷新" Command="{Binding RefreshSessionsCommand}" Padding="10,3"/>
        </StackPanel>

        <!-- 状态: 当前采集指示 -->
        <Border DockPanel.Dock="Top" Visibility="{Binding IsRecording, Converter=...}"
                Background="#FFF3E0" Padding="8,4" Margin="0,0,0,10" CornerRadius="3">
            <TextBlock>
                <Run Text="正在采集中 - 会话 ID: "/>
                <Run Text="{Binding ActiveSessionId, Mode=OneWay}" FontWeight="Bold"/>
            </TextBlock>
        </Border>

        <!-- 主体: DataGrid -->
        <DataGrid ItemsSource="{Binding SessionsView}"
                  SelectedItem="{Binding SelectedSession}"
                  AutoGenerateColumns="False"
                  IsReadOnly="True"
                  SelectionMode="Single"
                  CanUserSortColumns="True">
            <DataGrid.Columns>
                <DataGridTextColumn Header="会话名称" Binding="{Binding Name}" Width="200"/>
                <DataGridTextColumn Header="开始时间" Binding="{Binding StartedAt, StringFormat=yyyy-MM-dd HH:mm:ss}" Width="160"/>
                <DataGridTextColumn Header="结束时间" Binding="{Binding EndedAt, StringFormat=yyyy-MM-dd HH:mm:ss}" Width="160"/>
                <DataGridTextColumn Header="传感器" Binding="{Binding SensorType}" Width="120"/>
                <DataGridTextColumn Header="采样率" Binding="{Binding SampleRate, StringFormat={}{0} Hz}" Width="80"/>
                <DataGridTextColumn Header="数据点数" Binding="{Binding TotalReadings, StringFormat=N0}" Width="100"/>
                <DataGridTextColumn Header="备注" Binding="{Binding Notes}" Width="*"/>
            </DataGrid.Columns>

            <DataGrid.ContextMenu>
                <ContextMenu>
                    <MenuItem Header="重命名" Command="{Binding RenameSessionCommand}"
                              CommandParameter="{Binding SelectedSession}"/>
                    <MenuItem Header="编辑备注" Command="{Binding EditNotesCommand}"
                              CommandParameter="{Binding SelectedSession}"/>
                    <Separator/>
                    <MenuItem Header="导出 CSV" Command="{Binding ExportSessionCommand}"
                              CommandParameter="{Binding SelectedSession}"/>
                    <MenuItem Header="历史回放" Command="{Binding PlaybackSessionCommand}"
                              CommandParameter="{Binding SelectedSession}"/>
                    <Separator/>
                    <MenuItem Header="删除" Command="{Binding DeleteSessionCommand}"
                              CommandParameter="{Binding SelectedSession}" Foreground="Red"/>
                </ContextMenu>
            </DataGrid.ContextMenu>
        </DataGrid>
    </DockPanel>
</UserControl>
```

### 5. MainWindow.xaml 修改

在左侧导航栏中添加按钮:

```xml
<!-- 在 "实时采集" 按钮之后添加 -->
<Button Content="数据管理" Margin="3" Padding="8,6" HorizontalContentAlignment="Left"
        Command="{Binding NavigateToSessionListCommand}"/>
```

在 `ContentControl.Resources` 中注册 DataTemplate:

```xml
<DataTemplate DataType="{x:Type vm:SessionListViewModel}">
    <views:SessionListView/>
</DataTemplate>
```

### 6. DI 注册

```csharp
// App.xaml.cs 中添加:
services.AddTransient<SessionListViewModel>();
```

### 7. IDataStorageService 扩展（可选）

如果重命名和备注编辑需要更新会话元数据，`IDataStorageService` 可能需要新增方法。有两种方案：

- **方案 A**（推荐）: 在 `SqliteStorageService` 中新增非接口方法 `UpdateSessionNameAsync` 和 `UpdateSessionNotesAsync`，由 `SessionListViewModel` 通过具体类型调用
- **方案 B**: 扩展 `IDataStorageService` 接口新增 `UpdateSessionAsync(SessionInfo session)` 方法

建议采用方案 A，避免修改核心接口。后续如有更多更新需求再考虑接口泛化。

## 验收标准

| # | 验收项 | 通过条件 |
| - | ------ | -------- |
| 1 | 自动创建会话 | 启动采集后，数据库中自动出现新会话记录，名称格式为 `采集_yyyy-MM-dd_HH:mm:ss` |
| 2 | 自动结束会话 | 停止采集后，会话的 `ended_at` 和 `total_readings` 正确更新 |
| 3 | 读数自动保存 | 采集过程中产生的读数自动写入数据库，与会话 ID 关联 |
| 4 | 会话列表展示 | 导航到"数据管理"页面可看到所有历史会话，按时间降序排列 |
| 5 | 名称搜索 | 在搜索框输入关键字后，列表实时过滤 |
| 6 | 日期筛选 | 设置日期范围后，仅显示范围内的会话 |
| 7 | 传感器类型筛选 | 选择传感器类型后，仅显示对应类型的会话 |
| 8 | 重命名 | 通过右键菜单可修改会话名称，修改后持久化到数据库 |
| 9 | 备注编辑 | 通过右键菜单可编辑备注，修改后持久化到数据库 |
| 10 | 删除确认 | 删除会话弹出确认对话框，确认后会话及关联数据全部清除 |
| 11 | 导航集成 | 主窗口左侧出现"数据管理"按钮，点击可切换到会话列表页 |
| 12 | 采集中指示 | 正在采集时，页面顶部显示采集状态提示 |

## 单元测试要求

测试项目: `tests/MagnetometerSystem.App.Tests/`

### 测试类: `SessionListViewModelTests`

| 测试方法 | 验证内容 |
| -------- | -------- |
| `OnAcquisitionStarted_CreatesSession` | 发布 `AcquisitionStarted` 事件后，`ActiveSessionId` 非空且 `IsRecording = true` |
| `OnAcquisitionStopped_EndsSession` | 发布 `AcquisitionStopped` 事件后，`ActiveSessionId` 为空且 `IsRecording = false` |
| `ReadingReceived_BuffersAndFlushes` | 发送 600 条读数，验证 `SaveReadingsAsync` 至少被调用一次且总数正确 |
| `RefreshSessions_LoadsFromStorage` | Mock `IDataStorageService.GetSessionsAsync` 返回 3 条记录，刷新后 `Sessions.Count == 3` |
| `SearchText_FiltersSessionsByName` | 设置 `SearchText = "测试"`，验证 `SessionsView` 仅包含名称含"测试"的会话 |
| `FilterStartDate_FiltersSessionsByDate` | 设置起始日期，验证仅返回该日期之后的会话 |
| `FilterSensorType_FiltersCorrectly` | 设置传感器类型筛选，验证仅返回对应类型的会话 |
| `DeleteSession_RemovesFromList` | 删除会话后，`Sessions` 集合中不再包含该项 |
| `MultipleFilters_CombineCorrectly` | 同时设置名称搜索和日期范围，验证筛选条件叠加生效 |
