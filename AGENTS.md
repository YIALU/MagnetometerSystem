# Agent 统一入口

适用于整个仓库。开始前先读本文件、README 和本次涉及模块；只深入与任务相关的历史资料。

## 产品目标

- 面向日常调试的通用磁力仪上位机；用户定义协议，协议决定通道数量、名称和单位。
- 最高优先级：连接后可靠保存原始读数、实时绘图、通信可诊断。
- 标称采样率、设备实际输出频率、绘图刷新率是三个概念。标称值不隐式下发设备命令。
- 不按设备枚举限制通道或采样率。保留旧模型字段时仅用于兼容，不能让旧设备分类成为新的业务前置条件。
- 校正、正交度、偏移/增益及高级分析是按需使用的扩展；默认采集不得依赖它们。

## 最短导航

| 任务 | 入口 |
| --- | --- |
| 应用启动 / DI | `src/MagnetometerSystem.App/App.xaml.cs` |
| 主窗口 / 导航 | `src/MagnetometerSystem.App/MainWindow.xaml`、`ViewModels/MainViewModel.cs` |
| 连接 / 收包 / 解析 | `App/ViewModels/ConnectionViewModel.cs`、`Core/Communication/`、`Core/Protocol/` |
| 协议 / 命令定义 | `Core/Models/ProtocolConfig.cs`、`Core/Models/DeviceCommand.cs` |
| 命令发送 | `App/ViewModels/DeviceCommandViewModel.cs`、`Core/Communication/CommandFrameBuilder.cs` |
| 分发与采集生命周期 | `Core/Services/DataBus.cs` |
| 会话 / 保存 | `App/ViewModels/SessionListViewModel.cs`、`Infrastructure/Database/SqliteStorageService.cs` |
| 图表 / 分析 | `App/ViewModels/RealtimeChartViewModel.cs`、`App/Views/RealtimeChartView.xaml.cs`、`Core/Processing/` |
| 历史 / 导出 | `App/ViewModels/HistoryPlaybackViewModel.cs`、`Infrastructure/Export/CsvExporter.cs` |
| 校正扩展 | `Core/Calibration/`、对应校准 ViewModel |
| 验证 / 发布 | `docs/testing-and-acceptance.md`、`docs/发布流程.md`、`build.ps1` |

表中 `App/`、`Core/`、`Infrastructure/` 是 `src/MagnetometerSystem.*` 的简称。

数据主线：连接 → 字节流 → 协议解析 → 关键保存消费者接纳原始副本，成功后才计数并发布 `ReadingReceived`；可选处理结果通过 `ProcessedReadingReceived` 驱动图表。保存接纳与故障停收互斥，普通订阅者、UI 和实际写库在接纳锁外运行；接纳不等于事务已提交。两条流的订阅者各收到独立读数副本。扩展可生成校正值，但不得覆盖原始读数。发送主线：命令参数 → 帧构造 → 当前连接写出 → 协议响应判定。

## 架构边界与不变量

- `Core` 放协议、通信、数据模型、计算和接口，不引入 WPF、数据库或对话框依赖。
- `Infrastructure` 实现持久化、配置与导出；`App` 组合依赖、管理 UI 线程和用户操作。
- 会话及关键保存消费者先于连接就绪；断开和退出时等待尾批保存。入队、解析和事务提交的计数不能混用。
- 保存失败必须可诊断，不能清空未保存批次后继续显示成功；恢复/重试不能造成静默丢数或重复。
- 原始值是协议解析后、校正前的数值。传播 `OriginalChannelValues`、时间戳、会话与校正状态时保持关联；不要让复制对象或校正链丢失它们。
- 暂停显示、折叠面板、切页、显示偏移、滤波和显示降采样不能停止或减少原始保存。回放不能再次写入采集会话。
- 回放只发布显示流，不发布原始流；`CurrentConnection != null` 时禁止回放，包括 TCP 重连期间。历史通道与单位来自已加载会话，不按旧类型推断。
- 旧固定列数据保留在 legacy 表中并显式提示迁移，不能自动删除；内存待写队列不是断电恢复日志。
- 单图模式让所有选中通道共用绘图区；温度等异单位通道使用独立轴。多图模式按通道分图。
- 图表单位、名称和数据索引必须一致，通道重排不能造成数据库名值错配。
- 正交度参数记录拟合单位，应用时必须与对应三通道单位一致；旧参数未记录单位时保持未知并拒绝应用，不能根据 UI 标签推断。拟合数据与结果的单位随输入冻结，改正版本包含单位身份。
- 本地写出完成 ≠ 收到 ACK ≠ 设备执行成功。日志和 UI 只声称已得到证据的状态。
- 协议流必须处理分包、粘包、噪声、坏校验和重新同步；不能假设一次收包就是一帧。
- UI 更新需要合适的 Dispatcher/线程边界；不能通过增加刷新频率掩盖队列堵塞。

## 构建与测试

在 Windows、.NET 8 SDK 环境，从仓库根运行：

```powershell
dotnet restore MagnetometerSystem.sln
dotnet build MagnetometerSystem.sln -c Debug
dotnet test MagnetometerSystem.sln -c Debug
dotnet run --project src/MagnetometerSystem.App/MagnetometerSystem.App.csproj
```

WPF 程序及涉及 WPF 的测试需要 Windows；Linux 上 Core 测试通过不能替代 Windows 应用验证。需要缩小验证范围时，直接对相应测试项目或 `--filter` 运行；业务链路与串口验收见测试文档。

变更验证应对应风险：

- 协议/命令：有效与异常帧、分包/粘包、索引/缩放/字节序/校验；对端实际接收的发送字节。
- 保存/导出：真实临时 SQLite、原始/校正值、启动首帧与停止尾批、时间/通道筛选、CSV 精度和特殊名称。
- 生命周期：首帧立即到达、断开/重连、保存失败、回放隔离；通过真实业务调用链验证。
- 图表/UI：单图温度轴、多图与通道开关、折叠和专注的状态恢复、暂停不影响保存、统计/计算/区间仍可用。
- 小范围文案与布局调整使用适当构建和交互检查，不为纯静态改动添加只复述实现的测试。

## 协作与真源

- 编辑前看 `git status` 和相关 diff；保留已有未提交、未跟踪的用户文件及其他代理改动。只修改本任务范围，禁止以清理为由整体还原工作区。
- 不把生成目录、测试数据库、日志、个人工具配置写入源码；测试使用独立临时数据库和本地端口，不能触及真实用户数据。
- 发布版本源是 `Directory.Build.props`；`AppVersion.cs` 是运行时读取入口。遵循现有发布脚本和文档。
- README 说明用户工作流；本文件说明工程入口和约束；`docs/testing-and-acceptance.md` 说明测试证据及边界。
- 旧项目计划、TASK 文档和原型是参考，不是“功能已实现/已验证”的证明。遇到旧文档中的设备类型约束，以当前产品目标为准。
- 报告改动时区分：未运行、构建通过、单元通过、业务链路通过、UI 检查通过、真实设备通过。没有串口环境就明确未做串口验证，不用 mock 或 TCP 结果替代。

## 提交与审查

遵循 [CONTRIBUTING.md](CONTRIBUTING.md)：功能分支提交到 GitHub PR；每次推送后核对最新提交的 Windows CI 与 Codex 审查。人工确认合并，未经明确要求不推送 Gitee。

## Code Review Rules

审查本次变更引入或加重的问题，并沿调用链确认影响。以下是审查约束，不代表历史代码已全部满足；不要把无关旧问题、代码风格偏好或未验证的猜测作为本次缺陷。问题应附具体触发条件、受影响代码和后果，优先报告数据丢失、数据错配、崩溃与通信错误。

### 数据采集与保存

- 原始读数是协议解析后、校正前的值；校正和显示处理不得覆盖原始值。检查 `OriginalChannelValues`、时间戳、会话和校正状态在复制与分发中保持关联。
- 会话及保存消费者需要在数据到达前就绪；断开、停止和退出需要等待尾批保存。不要把入队、解析和事务提交数量当成同一个计数。
- 保存失败必须可诊断，不能丢弃未保存批次后宣称成功；检查重试、重连及恢复是否导致静默丢数或重复。
- 暂停图表、切换页面、隐藏面板、滤波和显示降采样不得减少原始保存；历史回放不得再次写入采集会话。
- 通道名称、单位、索引与数据库列值保持对应，通道重排和导出不能造成名值错配。

### 通信与协议

- 流式协议需要处理分包、粘包、噪声、坏校验和重新同步；一次收包不能假定为一帧。
- 校验通道索引、缩放、字节序和校验范围；涉及命令时检查最终发出的字节与响应判定。
- 区分本地写出完成、收到 ACK 和设备执行成功；UI 与日志只表达已有证据支持的状态。
- 协议配置决定通道含义；新增逻辑不应把固定设备枚举或默认通道数变成新的业务限制。标称采样率、实际输出频率和绘图刷新率不可混用；修改标称值不应隐式发送设备命令。

### 架构与 UI

- `Core` 承载模型、协议、通信、计算和接口，不引入 WPF、数据库实现或对话框依赖；`Infrastructure` 实现保存、配置与导出；`App` 负责组合与 UI 线程。
- UI 更新遵守 Dispatcher 和线程边界；检查异步异常、取消、释放及事件订阅生命周期，不能用更高刷新频率掩盖积压。
- 校正与高级分析是可选扩展；默认原始采集不应依赖它们。单图中的异单位通道应有合适的独立轴。

### 变更证据

- 协议与命令变更覆盖正常和异常帧、分包/粘包；保存变更覆盖真实临时 SQLite、首帧/尾批、失败恢复及原始值；生命周期变更沿实际业务调用链验证。
- 小范围文案和静态布局修改无需添加只复述实现的测试；构建通过不代表 UI 或真实串口设备已验证，mock/TCP 测试也不能代替真实设备验收。
- 不提交生成目录、数据库、日志、密钥和个人工具配置；旧计划与原型不能作为已实现或已验证的证据。
