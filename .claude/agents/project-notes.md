# MagnetometerSystem 项目知识

> 由人工 + Claude 维护。所有 agent 启动时主动 Read。
> 新坑/新约定随时追加。**不要写"通用 .NET 知���"，只写本项目的 quirk**。

---

## 数据流主干

```
串口 → ConfigurableAsciiParser → MagnetometerReading
       ↓
       DataBus.RaiseReadingReceived(reading)
       ↓
       订阅者：RealtimeChartViewModel / OrthogonalityCalibrationViewModel /
              SqliteStorageService / AeromagneticProcessor
```

新增数据消费者一律走 DataBus 订阅，**不要直接拿 SerialPort**。

---

## 通道模型

- 物理通道数由 `ProtocolConfig.ChannelCount` 决定（双三轴 = 6）
- `MagnetometerReading.Channels[i]` 按物理顺序存放
- `ChannelDisplayConfig` 是显示层概念：`ChannelIndex` 是物理索引，列表顺序是显示顺序
- **关键约束**：渲染图表时循环用 `ChannelDisplayConfig`，取数据用 `config.ChannelIndex`，不要混
  - 历史 bug：`for (int ch = 0; ch < count; ch++)` 用 ch 同时索引 configs 和 data → 拖拽后错位
  - 正确写法看 `RealtimeChartViewModel.RenderMultiPlot` foreach 循环

---

## ScottPlot 的坑

- 默认字体不带中文 glyph → 必须 `ChartFontHelper.Apply(plot)`
- `ApplyToAll()` 在 `App.OnStartup` 调一次设全局默认
- 但每个新 `WpfPlot` 的 `Plot` 实例还要再调一次 `Apply()`，否则 axis tick label 仍然乱码
- Annotation 用 `Alignment.UpperRight` 不要算坐标

---

## DataBus 模式

`Core/Services/DataBus.cs` 是 DI 注入的单例。

加新跨 VM 状态/事件的标准步骤：
1. 在 DataBus 加 `ObservableObject` 状态类（公开 `[ObservableProperty]` 字段）
2. 暴露 `public XxxState XxxState { get; } = new();`
3. 加事件 `public event Action? XxxRequested;` + `public void RaiseXxx() => XxxRequested?.Invoke();`
4. 生产方 ViewModel 在 ctor 订阅事件
5. 消费方（XAML）通过 MainViewModel 暴露的 passthrough 属性 binding

---

## 数据库迁移

- `Migrations/V<N>_<Name>.sql` 编号严格递增，不跳号
- 每个迁移在 `DatabaseInitializer.cs` 的 migration 列表里注册
- **已发布的 V*.sql 不可改** —— 改了用户升级会跳过
- SQLite 不支持 ALTER COLUMN / DROP COLUMN，结构变更走"新表 + INSERT SELECT + DROP 旧表 + RENAME"
- 当前最新：V6_AddOriginalChannelValues.sql

---

## 版本号 / 发布

- `Directory.Build.props` 的 `<Version>` 是唯一真源
- bump 后 `git commit` + `git tag -a vX.Y.Z`（必须 annotated）
- `InjectGitSha` target 自动把 short SHA 注入 `InformationalVersion`，运行时 `AppVersion.Display` 读取
- 工作树脏 → SHA 后缀加 `-dirty`，发布前确认 `git status` 干净
- 详细流程见全局 skill `dotnet-wpf-release-flow`

---

## .gitignore 必含

```
bin/
obj/
.vs/
*.user
publish/
publish*.zip
appsettings.*.local.json
```

publish.zip 曾被误提交过，**commit 前一定看 `git status`**。

---

## 运行时数据目录

- 数据库：`%LocalAppData%\MagnetometerSystem\magnetometer.db`
- 校准原始 CSV：`%LocalAppData%\MagnetometerSystem\statistics\calibration_raw\`
- 日志：尚未建立统一位置（待补）

---

## 测试

- `tests/MagnetometerSystem.Core.Tests/` 用 xUnit
- 不依赖 WPF / SerialPort / SQLite 文件
- 拖拽相关回归看 `ChannelReorderTests.cs`

---

## 历史踩坑（按时间倒序）

- **2026-05-02** 多图表拖拽错位：根因是 RenderMultiPlot 用物理 ch 索引同时取 configs 和 data。修法：foreach 配合 `config.ChannelIndex`。
- **2026-05-02** ScottPlot 中文乱码：要 `ChartFontHelper.Apply` 每个 Plot 实例。
- **2026-05-02** 误提交 publish.zip：补 .gitignore + `git rm --cached` + amend。
- **2026-05-02** 第一次发版忘 bump：流程定下来 commit 之前必须 bump + tag。

---

## 待补 / TODO

- 统一日志位置和级别约定
- E2E 测试方案（目前只有 Core 单元测试）
- CI 流水线
