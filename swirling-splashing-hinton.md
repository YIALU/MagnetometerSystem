# 功能缺口修复计划

## Context
Track 1-11 全部实施完成（191 测试通过），代码审查发现 9 个功能缺口需要修复（已排除 #3 ThemeName 和 #5 多格式导出）。

## 当前状态
- Build: 0 error, Tests: 191 通过
- 所有 Track 已完成，以下为遗留缺口修复

---

## Sprint A: 逻辑 Bug 修复 [P0] — 3 项可并行

### Fix-1: App.OnExit 覆盖用户设置 (Bug)
**文件**: `src/.../App.xaml.cs` (OnExit, ~111-139 行)
**问题**: `OnExit` 创建新 `AppSettings` 对象仅填充连接/图表字段，`DataStoragePath`/`AutoSaveEnabled`/`ThemeName` 被默认值覆盖
**修复**: 先 `LoadSettingsAsync()` 加载现有设置，再合并 UI 变更字段，再保存
```csharp
var settings = await configService.LoadSettingsAsync(); // 先读
settings.ChartRefreshRate = mainVm.RealtimeChartVM.RefreshRate;
settings.DefaultPortName = mainVm.ConnectionVM.SelectedPort;
// ... 只更新 UI 可变字段
await configService.SaveSettingsAsync(settings);
```

### Fix-2: DataStoragePath 设置项未生效
**文件**: `App.xaml.cs` (OnStartup), `DatabaseInitializer.cs`
**问题**: `DatabaseInitializer` 始终使用默认 AppData 路径，不读取用户配置的 `DataStoragePath`
**修复**:
1. `OnStartup` 中先加载 `AppSettings`（在 DI 注册之前）
2. 如果 `DataStoragePath` 非空，用它作为数据库目录传入 `DatabaseInitializer(customPath)`
3. 如果为空则保持默认行为
**注意**: 数据库路径变更需重启生效（已有提示）

### Fix-3: AutoSaveEnabled 设置项未生效
**文件**: `SessionListViewModel.cs` (OnReadingReceived, OnAcquisitionStarted)
**问题**: `OnReadingReceived` 无条件写入数据库，不检查 `AutoSaveEnabled`
**修复**:
1. `SessionListViewModel` 构造函数注入 `IAppConfigService`
2. 启动时加载 `AutoSaveEnabled` 到私有字段
3. `OnAcquisitionStarted` 中检查该字段：为 false 时不创建会话、不保存数据
4. `OnReadingReceived` 中检查该字段：为 false 时直接 return

---

## Sprint B: 数据管线集成 [P0] — 2 项有依赖

### Fix-4: 传感器校准 (CalibrationParams) 未集成到数据管线
**文件**: `ConnectionViewModel.cs` (OnDataReceived), `ConnectionView.xaml`, `RealtimeChartView.xaml`
**问题**: `CalibrationParams.Apply()` 已实现但从未在数据流中调用
**修复**:
1. `ConnectionViewModel` 注入 `ICalibrationRepository`
2. 新增属性: `AvailableCalibrationProfiles`, `ActiveCalibrationProfile`, `IsCalibrationEnabled`
3. 新增命令: `LoadCalibrationProfilesCommand`
4. `OnDataReceived` 中在正交度校正之前调用:
   ```csharp
   if (IsCalibrationEnabled && ActiveCalibrationProfile != null)
       processed.ChannelValues = ActiveCalibrationProfile.Apply(processed.ChannelValues);
   ```
5. `ConnectionView.xaml` 或 `RealtimeChartView.xaml` 右侧配置面板新增"传感器校准"区域（ComboBox + 启用开关）

### Fix-10: 实时采集正交度校正无配置选择 UI
**文件**: `RealtimeChartView.xaml` (正交度校正展开器), `ConnectionViewModel.cs`
**问题**: `ActiveOrthogonalityProfile` 属性存在但无 UI 选择
**修复**:
1. `ConnectionViewModel` 新增: `AvailableOrthogonalityProfiles`, `LoadOrthogonalityProfilesCommand`
2. `RealtimeChartView.xaml` 正交度校正展开器内添加:
   - ComboBox 绑定 `ConnectionVM.AvailableOrthogonalityProfiles`
   - SelectedItem 绑定 `ConnectionVM.ActiveOrthogonalityProfile`
   - 刷新按钮绑定 `ConnectionVM.LoadOrthogonalityProfilesCommand`
3. 参考 `HistoryPlaybackView.xaml` 的校正 UI 模式

---

## Sprint C: 导出与查看增强 [P1] — 2 项可并行

### Fix-6: 导出时 IncludeCalibratedData 始终 false
**文件**: `SessionListViewModel.cs` (ExportSessionAsync), `SessionListView.xaml`
**问题**: 导出选项硬编码，用户无法控制包含校准状态列
**修复**:
1. 导出前弹出选项对话框，或在导出面板添加 CheckBox:
   - `IncludeCalibratedData` (包含校准状态列)
   - `IncludeHeader` (包含表头)
2. `SessionListViewModel` 新增属性 `ExportIncludeCalibratedData`
3. `ExportSessionAsync` 使用该属性值代替硬编码

### Fix-9: 校正后数据 (CorrectedReading) 无查看/导出入口
**文件**: `SessionListViewModel.cs`, `SessionListView.xaml`, `CsvExporter.cs`
**问题**: 批量校正结果已存储但用户无法查看或导出
**修复**:
1. `SessionListViewModel` 新增 `ExportCorrectedSessionCommand`:
   - 调用 `GetCorrectedReadingsAsync(sessionId)` 获取校正数据
   - 映射 `CorrectedReading` → 临时 `MagnetometerReading` 后调用 `CsvExporter`
   - 或直接写 CSV（CorrectedReading 字段简单明确）
2. `SessionListView.xaml` DataGrid 新增列 `HasCorrectedData`（显示"已校正"标记）
3. 右键菜单新增"导出校正数据" MenuItem
4. `SessionListViewModel` 批量校正完成后更新 `HasCorrectedReadingsAsync` 状态

---

## Sprint D: 体验优化 [P1] — 2 项可并行

### Fix-7: 回放页面无嵌入图表
**文件**: `HistoryPlaybackView.xaml`, `HistoryPlaybackViewModel.cs`
**问题**: 回放页面需切换到实时采集页面才能看图表
**修复**:
1. `HistoryPlaybackView.xaml` 中央区域替换灰色提示为嵌入式 ScottPlot 图表
2. 新增 `<scottplot:WpfPlot x:Name="PlaybackPlot"/>` 控件
3. `HistoryPlaybackViewModel` 新增简单的图表渲染逻辑:
   - 维护 CircularBuffer 存储回放数据
   - `OnPlaybackTick` 时更新图表
   - 复用 `LttbDownsampler` 降采样
4. 保留 DataBus 发布（实时图表仍可同步显示）
5. 图表功能可简化（不需要完整的通道配置/统计，只需基本波形显示）

### Fix-11: Settings 修改连接参数不实时同步
**文件**: `DataBus.cs`, `SettingsViewModel.cs`, `ConnectionViewModel.cs`
**问题**: Settings 页面保存后连接参数不同步到 ConnectionViewModel
**修复**:
1. `DataBus` 新增事件: `event Action<AppSettings> SettingsChanged`
2. `DataBus` 新增方法: `PublishSettingsChanged(AppSettings settings)`
3. `SettingsViewModel.SaveSettingsAsync` 保存成功后调用 `_dataBus.PublishSettingsChanged(settings)`
4. `ConnectionViewModel` 订阅 `_dataBus.SettingsChanged`:
   ```csharp
   _dataBus.SettingsChanged += settings => {
       SelectedPort = settings.DefaultPortName ?? SelectedPort;
       BaudRate = settings.DefaultBaudRate > 0 ? settings.DefaultBaudRate : BaudRate;
       // ...
   };
   ```
5. `SettingsViewModel` 构造函数注入 `DataBus`

---

## 实施顺序与依赖关系

```
Sprint A (并行):  Fix-1, Fix-2, Fix-3     ← 无依赖，立即可做
                       ↓
Sprint B (顺序):  Fix-4 → Fix-10          ← Fix-4 先做（共享 ConnectionVM 修改）
                       ↓
Sprint C (并行):  Fix-6, Fix-9            ← 无依赖
Sprint D (并行):  Fix-7, Fix-11           ← 无依赖
```

Sprint A 和 Sprint D 可同时启动（不涉及相同文件）。

---

## 验证清单

1. **Fix-1**: Settings 页面保存 DataStoragePath → 退出 → 重启 → 值保持不变
2. **Fix-2**: 设置 DataStoragePath 为自定义路径 → 重启 → 数据库文件出现在新路径
3. **Fix-3**: 关闭 AutoSave → 开始采集 → 停止 → 会话列表无新增
4. **Fix-4**: 选择校准配置 → 启用 → 开始采集 → 数据经过 offset+gain 校准
5. **Fix-6**: 导出时勾选"包含校准状态" → CSV 含 IsCalibrated/IsOrthoCorrected 列
6. **Fix-7**: 回放页面直接显示波形图，无需切换页面
7. **Fix-9**: 批量校正后 → 右键"导出校正数据" → CSV 包含校正后的值
8. **Fix-10**: 实时图表右侧面板 → 选择正交度配置 → 启用 → 数据经过校正
9. **Fix-11**: Settings 修改默认端口 → 保存 → 连接页面立即更新端口值
10. Build 0 error，全部 191+ 测试通过
