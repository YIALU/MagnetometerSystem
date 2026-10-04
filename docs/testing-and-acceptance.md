# 测试与验收

本文说明如何验证“协议接入 → 接收 → 解析 → 自动保存 → 绘图/可选校正 → 导出”和“命令 → 实际写出 → 响应”的业务主线。**测试是否完整应按场景与证据判断，不能只看通过数量或代码覆盖率。**

## 验证层次

| 层次 | 验证内容 | 不能证明的内容 |
| --- | --- | --- |
| Core 单元测试 | 解析、帧构造、通道索引、校验、计算、原始值保护 | 串口真的打开、字节真的传输、UI/保存生命周期正确 |
| Infrastructure 集成测试 | 临时 SQLite、会话/读数、配置、CSV 文件内容 | App 是否及时创建/结束会话、连接中断后是否刷完尾批 |
| 业务链路 + TCP loopback | 真实本机套接字，连接与业务消费者，实际 SQLite/CSV，对端捕获发送字节 | USB 驱动、串口电气连接、设备固件的真实执行结果 |
| Windows WPF 验收 | 控件绑定、线程调度、单图/多图、布局、切页、关闭窗口 | 未连接设备时的硬件行为；HTML 原型不能替代正式 WPF 验收 |
| 虚拟/实体串口验收 | `SerialPort` 链路、分包节奏、设备或对端收发、断线与重连 | 虚拟串口不能替代实体适配器和设备的电气/驱动/固件验证 |
| 长时间与故障验收 | 高负载、持续采集、磁盘/权限/连接故障、数据计数一致性 | 短时测试不能宣称所有通道数、波特率和采样率都可靠 |

## 运行自动测试

在 Windows + .NET 8 SDK 环境，从仓库根执行：

```powershell
dotnet restore MagnetometerSystem.sln
dotnet build MagnetometerSystem.sln -c Debug
dotnet test MagnetometerSystem.sln -c Debug --logger trx --results-directory artifacts/test-results
```

分层运行现有项目：

```powershell
dotnet test tests/MagnetometerSystem.Core.Tests/MagnetometerSystem.Core.Tests.csproj -c Debug
dotnet test tests/MagnetometerSystem.Infrastructure.Tests/MagnetometerSystem.Infrastructure.Tests.csproj -c Debug
dotnet test tests/MagnetometerSystem.App.Tests/MagnetometerSystem.App.Tests.csproj -c Debug
```

需要覆盖率时使用测试项目已有的 collector：

```powershell
dotnet test MagnetometerSystem.sln -c Debug --collect:"XPlat Code Coverage" --results-directory artifacts/coverage
```

测试数据库、导出文件、监听端口必须由测试独立创建并清理。使用 loopback 地址与动态端口，避免依赖特定外部设备、固定机器路径或用户正在使用的数据库。保存 TRX、覆盖率报告和失败日志；记录实际提交、配置和环境。

## 当前证据与缺口

代码中已经存在下列自动测试：

- `Core.Tests`：ASCII/二进制/专用解析器、分包与错误帧、通道映射和顺序、命令帧/CRC、缓冲区、公式、统计、降采样、校正计算与读数模型。
- `Infrastructure.Tests`：临时 SQLite 的会话/读数读写与计数、配置保存、CSV 通道与时间筛选/空会话/取消，以及更新服务逻辑。
- `AcquisitionProtocolTests` / `TcpAcquisitionLoopbackTests`：协议边界、真实 TCP 分包解析、对端接收到的命令字节、并发发送完整帧、有限重连。
- `AcquisitionStorageWorkflowTests`：原始/显示数据隔离、存储提交与失败后重试、停止尾批、原始/校正导出，以及旧固定列数据保留。
- `App.Tests`：在共享 STA / WPF Dispatcher 环境运行实际 ViewModel。`ProtocolFlowTests` 通过真实 TCP → `ConnectionViewModel` → `SessionListViewModel` → SQLite，覆盖 21 通道、温度单位、首帧/定时批次/停止尾批，以及 `DeviceCommandViewModel` 对端字节和分片响应。`HistoryPlaybackViewModelTests` 使用真实临时 SQLite，验证通道/单位、显式校正索引、原始值保护、时间戳倍速、完成后复播、实时连接互斥，并加载实际历史视图检查内嵌曲线绑定和温度轴。
- `RealtimeWorkspaceTests` / `WorkspaceLayoutTests`：实际 WPF 工作台、单图温度轴、原始值统计、通道重排、65 通道、折叠/专注状态恢复和控件绑定。`ShutdownUpdateTests` 三项用例验证更新前等待保存和设置完成、取消更新、保存失败阻止安装以及重试；安装器与这组测试的连接使用替身，不执行真实安装。

这些条目表示测试代码的覆盖范围。最近一次完整运行结果见下面的日期记录；实体串口、真实设备 ACK/执行结果与长时间稳定性，在没有对应运行记录时一律视为未验证。

历史 `18-TASK-TESTS-单元测试与集成测试.md` 中的“零测试”“目标覆盖率 ≥ 80%”是早期规划，不能用作当前测试结论。

## 2026-10-04 验证记录

环境为 Windows 本机、当前工作区修改、Debug 构建；Core/Infrastructure 目标为 `net8.0`，App 为 `net8.0-windows`。完成构建后，最终串行执行：

```powershell
dotnet test MagnetometerSystem.sln --no-build --no-restore -m:1 --verbosity minimal --logger trx --results-directory .codex_tmp/TestResults-final
```

| 项目 | 通过 | 跳过 | 失败 | 最终 TRX |
| --- | ---: | ---: | ---: | --- |
| Core | 297 | 1 | 0 | [03_54_59.trx](../.codex_tmp/TestResults-final/22109_LAPTOP-O21DL5NQ_2026-10-04_03_54_59.trx) |
| Infrastructure | 65 | 0 | 0 | [03_55_00.trx](../.codex_tmp/TestResults-final/22109_LAPTOP-O21DL5NQ_2026-10-04_03_55_00.trx) |
| App | 20 | 0 | 0 | [03_55_11.trx](../.codex_tmp/TestResults-final/22109_LAPTOP-O21DL5NQ_2026-10-04_03_55_11.trx) |
| 合计 | **382** | **1** | **0** | 三个项目均已发现并执行 |

唯一跳过项是 `OptionalSerialLoopbackTests.ConnectedSerialPair_ReceivesDataAndWritesExactCommandBytes`，原因是未配置互连串口环境；这不代表串口验收通过。此前一次运行中 Infrastructure 测试发现器临时未加载依赖，该次漏发现没有计入通过结果；其后独立运行 65 项通过，并由上述最终串行全套运行再次确认。

已加载并检查实际 WPF 主窗口，保存了[专注曲线截图](../.codex_tmp/ui-verification/workspace-focus.png)和[辅助面板展开截图](../.codex_tmp/ui-verification/workspace-expanded.png)，并完成目视核验。TRX 与截图是本机 `.codex_tmp` 下的验证产物，不作为源码提交，也不会随新克隆自动出现。

构建保留已有 `NU1701` 警告：`SkiaSharp.Views.WPF 3.119.0` 使用 .NET Framework 兼容资源还原；本次 WPF 测试通过不消除此依赖兼容性警告。本轮没有执行虚拟/实体串口验收、真实安装器升级、设备执行确认或长时间吞吐测试，也未给出代码覆盖率百分比。TCP 对端模拟响应证明响应处理路径，不能替代真实设备证据。

## 核心业务验收矩阵

| 场景 | 操作与断言 | 首选验证层 |
| --- | --- | --- |
| 自由协议与通道 | 不选择设备类型；检查不同通道数协议的名称、单位、索引、数量；验证 65 通道不会被旧 64 通道上限截断；采样率仅要求合法数值 | Core + 业务链路 + UI |
| 连接后立即收包 | 对端一建立连接就发首帧；会话先准备好，首帧存在数据库 | 业务链路 |
| ASCII 分包/粘包 | 按任意字节边界拆一行，一次发多行，跨读缓冲区发送；解析顺序和值一致 | Core + TCP/串口 |
| 二进制同步 | 噪声、截断帧、坏校验、正确帧连续发送；错误可诊断，后续帧可恢复 | Core + TCP/串口 |
| 帧定义与通道重排 | 修改帧段顺序、通道索引、缩放和字节序；数据库列名与值、曲线标题一致 | Core + 业务链路 |
| 正常自动保存 | 发送已知 N 条有效记录；停止并刷盘后数据库记录数、会话总数与期望相同 | 业务链路 + SQLite |
| 停止尾批 | 未达到批量阈值时立即停止；最后一批不丢失、不重复 | 业务链路 |
| 断开/重连 | 自动重连保留当前会话；手动断开再连接创建新会话；远端断开、串口拔出后数据归属正确，停止时尾批完成 | TCP + 实体串口 + UI |
| 保存失败 | 模拟不可写目录/写入异常/数据库锁；显示失败，未保存读数不算已落库 | 故障集成测试 + UI |
| 默认不校正 | 不选校正配置，保存与绘图完整可用 | 业务链路 + UI |
| 校正保护原始值 | 用已知非单位矩阵产生不同结果，验证校正前数值仍可查询并导出；额外通道如温度不被三轴算法改变 | Core + SQLite + CSV |
| 显示处理与保存隔离 | 暂停显示、滤波、显示偏移、清图、降采样、折叠和切页，数据库仍按原始数值记录 | 业务链路 + UI |
| 单图/多图 | 选中磁场与温度；单图只有一个绘图区且温度右轴，多图按通道绘制 | Windows WPF |
| 折叠/专注 | 各辅助面板独立开合；小窗口展开多个面板后可滚动访问；专注模式增大曲线，退出恢复开合及输入，连接/保存状态仍可见 | Windows WPF |
| 分析功能 | 滚动统计/区间、计算通道、单位与索引、图形选择和导出保持一致 | Core + UI |
| 历史回放 | 加载指定会话、暂停/定位/倍速/切页；通道与单位从会话恢复，温度轴正确；实时连接存在时禁止回放，回放仅发布显示流 | 业务链路 + UI |
| CSV 一致性 | 与数据库逐条对账；验证通道顺序、时间筛选、逗号/引号名称、小数精度和不同系统区域设置 | SQLite + CSV |
| 命令真实写出 | 通过业务发送入口发送参数化 ASCII/HEX/二进制命令，由 TCP 服务端或串口另一端捕获；逐字节比较行尾、长度、字节序、校验 | TCP + 串口 |
| 命令响应 | 覆盖无响应、延迟、拒绝、无关帧、匹配 ACK；只有协议明确成功时才称执行成功 | 协议集成 + 设备 |
| 退出应用 | 收包期间关闭窗口，确认连接释放、尾批完成，重新打开可看到最后记录 | Windows WPF + SQLite |
| 旧库保护 | 识别旧固定列表并保留 legacy 表、会话计数和迁移提示；未转换数据不得显示为空库成功 | SQLite |

矩阵是验收要求。某行存在单元测试，并不意味着该行所有层次都已覆盖；报告必须给出实际执行层次。

写库失败后保留的是进程内批次，可在修复后重试；未提交队列不保证断电或强杀恢复。对这类故障需要持久化恢复日志的场景，必须另行设计并验证，不能根据正常停止测试推断已有保证。

图表每通道只保留最近 100,000 点，区间分析以当前保留数据为范围；完整会话查询与导出以 SQLite 为准。65 通道回归验证了取消旧显示截断，不构成任意通道数、速率和持续时间的性能保证；大规模吞吐仍需设备实测。

## TCP loopback 业务测试应怎样写

1. 在 `127.0.0.1` 的动态端口启动模拟设备端，使用真实 `TcpDeviceConnection`。
2. 通过应用实际使用的连接/会话入口运行采集。存储消费者应使用真实临时 SQLite；不要直接往数据库塞期望结果后声称验证了接收链路。
3. 在连接建立后立即发送确定性数据；分别制造分包、粘包、无效帧和正常结束。
4. 等待真实业务完成信号并停止采集，查询记录数、会话信息和具体通道值，再从数据库导出 CSV 对账。
5. 命令测试由对端读取实际到达的字节；只断言 `CommandFrameBuilder` 的返回数组，属于帧构造单元测试。
6. 可选校正使用已知参数，明确比对原始值与派生值；默认关闭路径也必须通过。

尽量用事件、完成任务和有界超时等待；不要靠任意固定延时掩盖竞态。涉及 WPF ViewModel 的测试需正确的 STA/Dispatcher 环境。不能因为测试使用了假 UI 调度器，就声称验证了真实窗口行为。

## 可选串口与设备验收

准备虚拟串口对（例如 COM11 ↔ COM12），或者两只实际串口适配器按电气标准连接。上位机使用一端，独立发送/监听程序使用另一端。测试前记录端口、波特率、数据位、停止位、校验、适配器/驱动及设备固件版本。

`OptionalSerialLoopbackTests` 使用两个互连端口；未设置端口环境变量时明确跳过，不算串口通过。在已准备好的测试端口上运行：

```powershell
$env:MAGNETOMETER_TEST_RX_PORT = 'COM11'
$env:MAGNETOMETER_TEST_TX_PORT = 'COM12'
$env:MAGNETOMETER_TEST_BAUD_RATE = '115200'
dotnet test tests/MagnetometerSystem.Core.Tests/MagnetometerSystem.Core.Tests.csproj -c Debug --filter FullyQualifiedName~OptionalSerialLoopbackTests
```

端口变量只指向本轮验收用的互连端口，不应指向正在进行正式采集的设备。该测试验证 `SerialPort` 传输；App + SQLite + 导出的完整串口流程还需按下列步骤核对。

仓库已有 `magnetometer_test.py` 可用来发送示例流；需要 Python 和 `pyserial`：

```powershell
python -m pip install pyserial
python magnetometer_test.py --port COM12 --baud 115200 --rate 100 --sensor triaxial --protocol ascii --count 1000
```

上位机连接 COM11，使用三个数值字段、逗号分隔、CRLF 行尾。脚本的 `--sensor` 只是旧版示例波形选择器，不是上位机设备类型限制。该脚本仅是人工联调工具，不自动证明入库、导出或命令响应正确。

验收至少包括：

1. 连续发送已知数量与序号的数据，结束后将发送记录、数据库、CSV 三方对账。
2. 发送端改变写入块大小与节奏，混入坏帧，确认接收恢复、错误可见。
3. 在低速和较高通道/速率组合下运行；记录持续时间、有效记录数、失败/丢弃数、CPU/内存、待写入峰值。不要用标称速率替代实测吞吐。
4. 上位机发送命令，对端记录精确字节并回送 ACK/拒绝/无关响应；设备执行结果由读取设备状态或实际行为确认。
5. 采集中拔出实体适配器、恢复连接，检查错误提示、会话边界和尾批；测试前使用可丢弃的验收数据。

虚拟串口无需每次日常单元测试运行，但发布前应按实际使用场景执行。没有串口环境时记录“未执行”，不能将跳过视为通过。

## 运行记录模板

```text
版本/提交：
日期、Windows/.NET/驱动版本：
执行命令与配置：
通过 / 失败 / 跳过：
链路：单元 / SQLite / TCP loopback / 虚拟串口 / 实体设备 / WPF
协议、通道数、标称与实测速率、持续时间：
发送数 / 解析数 / 已落库数 / 导出数 / 错误数：
命令：对端字节是否一致、ACK 证据、设备执行证据：
TRX/日志/截图/数据对账文件：
未覆盖范围与后续验收项：
```
