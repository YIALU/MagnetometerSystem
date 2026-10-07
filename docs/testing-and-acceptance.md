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
- `RealtimeWorkspaceTests` / `WorkspaceLayoutTests`：实际 WPF 工作台、单图温度轴、原始值统计、通道重排、65 通道、折叠/专注状态恢复和控件绑定。`ShutdownUpdateTests` 验证更新与正常关闭前等待尾批和设置保存、取消更新、真实 SQLite 写入失败阻止安装及显式重试；安装器与这组测试的连接使用替身，不执行真实安装。
- `MultiPlatformUpdateServiceTests`：真实更新解析、下载与文件校验调用链，使用隔离 HttpMessageHandler 和临时目录验证双平台版本不同步、单平台失败、指定平台、同版本镜像切换、取消和坏校验；不是外网下载证据。`UpdateSourceUiTests` 使用真实临时 SQLite 和 WPF 控件验证平台偏好持久化、下载选择、两项目链接；真实安装升级仍需人工验收。
- `CtmbsAcquisitionFlowTests`：真实 TCP 夹入状态/参数响应、坏长度头和合法推送，仅合法测量入库与绘图。`VariableLengthSegmentParserTests` 覆盖保留区、未映射尾部、动态校验和帧尾；`ZdzUnitAxesTests` / `UnitWorkflowTests` 覆盖各单位轴范围、回放单位、计算单位及非法来源。
- 界面重设计后新增（见 [交接](ui-redesign/交接.md)）：`NativeWorkspace…` 打开真实主窗口，切换全部页面、校正页三个页签和向导四步并断言无绑定错误；`DisconnectKeepsLastCurveVisible…` 经真实 TCP 连接 → 收帧 → 断开，验证未启用校正时曲线也有数据、断开后空状态不遮住曲线；`RawFramesDockShowsPerFrameResults…` 经真实 TCP 发送噪声、正确帧和坏校验帧，验证“原始报文”逐帧记录（通过 / XOR 校验失败及两个值 / 重新同步丢弃字节）和“收发”行；`DraggingOnPlot…` 验证拖动选区间、区间统计使用原始值、十字准线读数；`CalibrationWizardTests` 用总线读数走完正交度向导采集 → 计算 → 保存 → 配置库，覆盖手动 48 点的记录 / 撤销 / 清空 / 链路条记录，并用一个确定性用例防止校正采集在接收锁内同步等待界面线程（该用例在旧写法下会超时）；`AnalysisViewModelTests` 用真实临时 SQLite 的 65 分钟会话验证分块边界去重、原始值优先、闭区间时间段、空段 / 非法段提示、CSV 转义与精度、按块取消；`DialogRenderTests` 逐个打开六个对话框并检查绑定。`ProtocolFlowTests` 的两个命令用例同时断言结构化收发记录。Core 的 `ParseRecordLogTests` 覆盖三类解析器的逐帧记录、合并与有界缓冲。
- `AnalysisPerformanceTests`：只在设置 `MAGNETOMETER_TEST_PERF=1` 时运行，生成约百万条读数的临时库并记录分析耗时与内存（结果见下方记录），默认跳过。

这些条目表示测试代码的覆盖范围。最近一次完整运行结果见下面的日期记录；实体串口、真实设备 ACK/执行结果与长时间稳定性，在没有对应运行记录时一律视为未验证。

历史 `18-TASK-TESTS-单元测试与集成测试.md` 中的“零测试”“目标覆盖率 ≥ 80%”是早期规划，不能用作当前测试结论。

## 2026-10-04 首轮验证记录

环境为 Windows 本机、当前工作区修改、Debug 构建；Core/Infrastructure 目标为 `net8.0`，App 为 `net8.0-windows`。完成构建后，最终串行执行：

```powershell
dotnet test MagnetometerSystem.sln --no-build --no-restore -m:1 --verbosity minimal --logger trx --results-directory .codex_tmp/TestResults-final
```

| 项目 | 通过 | 跳过 | 失败 | 最终 TRX |
| --- | ---: | ---: | ---: | --- |
| Core | 297 | 1 | 0 | `03_54_59.trx`（首轮本机记录） |
| Infrastructure | 65 | 0 | 0 | `03_55_00.trx`（首轮本机记录） |
| App | 20 | 0 | 0 | `03_55_11.trx`（首轮本机记录） |
| 合计 | **382** | **1** | **0** | 三个项目均已发现并执行 |

唯一跳过项是 `OptionalSerialLoopbackTests.ConnectedSerialPair_ReceivesDataAndWritesExactCommandBytes`，原因是未配置互连串口环境；这不代表串口验收通过。此前一次运行中 Infrastructure 测试发现器临时未加载依赖，该次漏发现没有计入通过结果；其后独立运行 65 项通过，并由上述最终串行全套运行再次确认。

已加载并检查实际 WPF 主窗口，保存了专注曲线截图 `workspace-focus.png` 和辅助面板展开截图 `workspace-expanded.png`，并完成目视核验。TRX 与截图是本机 `.codex_tmp` 下的验证产物，不作为源码提交，也不会随新克隆自动出现。

构建保留已有 `NU1701` 警告：`SkiaSharp.Views.WPF 3.119.0` 使用 .NET Framework 兼容资源还原；本次 WPF 测试通过不消除此依赖兼容性警告。本轮没有执行虚拟/实体串口验收、真实安装器升级、设备执行确认或长时间吞吐测试，也未给出代码覆盖率百分比。TCP 对端模拟响应证明响应处理路径，不能替代真实设备证据。

## 2026-10-04 PR 审查修复与 V0.5.0 验证

在独立 worktree、Windows / SDK 9.0.312（目标 .NET 8）中完成构建与串行回归。构建禁用编译服务器并串行执行；WPF DLL 临时被 360 进程占用时，等待后重试构建成功再运行测试，未使用失败构建作为通过证据。

```powershell
dotnet build MagnetometerSystem.sln -c Debug --no-restore --disable-build-servers -m:1 -nr:false -p:UseSharedCompilation=false -p:BuildInParallel=false
dotnet test MagnetometerSystem.sln -c Debug --no-build --no-restore -m:1 --verbosity minimal --logger trx --results-directory .codex_tmp/TestResults-v050-commands
```

| 项目 | 通过 | 跳过 | 失败 | TRX 时间 |
| --- | ---: | ---: | ---: | --- |
| Core | 414 | 1 | 0 | 22_19_45 |
| Infrastructure | 113 | 0 | 0 | 22_19_46 |
| App | 89 | 0 | 0 | 22_19_53 |
| 合计 | **616** | **1** | **0** | 2026-10-04，本机 `.codex_tmp/TestResults-v050-commands` |

命令生命周期补充 4 项真实 TCP 回归：前条命令响应或超时前，后条不能进入连接写出；相同 ACK 的两条命令各自等待完整响应，实际超时释放排队，断连或释放取消在途等待并拒绝旧排队写出，重新连接后可再发送。对端实际接收字节及同步发送计数提供顺序证据；未配置响应判据的自由发送等待至超时或取消。协议没有请求 ID 时，超时后迟到 ACK 的归属仍无法保证，不能声称设备执行成功。

发布前图表性能回归补充 7 例：活动刷新只从环形缓冲区复制显示与统计窗口的并集，保留总点数；暂停时一次性冻结完整历史，支持扩窗。测试覆盖显示/统计独立窗口、零窗口语义、隐藏来源计算、100,000 点环绕及暂停后继续来数。65 通道在相同短窗口下比较 1,000 与 100,000 点历史的实际刷新分配量，验证不会因全部保留历史增长而每帧复制全量；分配测试没有连接绘图控件，不代表完整绘制耗时或设备长时间吞吐已通过。

串口回调补充 3 例：生产接收边界在 I/O 锁内读取、锁外通知；受控阻塞订阅者时，并发断开或释放可先取得锁并完成。测试还验证读取异常通知及抛错订阅者隔离，后续有效字节仍可分发。这些测试未打开端口，只验证托管锁边界；不能替代实际 SerialPort 驱动关闭、尾包和硬件验收。

CTMBS 简单响应再补充 3 例：11 种未知或损坏响应遍历全部分割位置，错误只计一次且后续测量仍可解析；四种完整合法 token 逐字节输入不报错。登录 ACK 后的仪器 ID、clock 和标准带长度响应保留原规则。

通道身份补充 7 项 Core 回归：按协议通道码映射 D/H/Z/T，交换和逆序逐字节输入仍得到相同通道值；未知或重复码拒绝一次后恢复，合法多组历史响应仍不生成实时读数。拟合保护补充 7 项 WPF/真实 SQLite 回归：只接受完整同单位三轴/双三轴数据源，拒绝额外、混合或不一致布局；标准连续/手动采集保持可用，运行中布局变化只停止拟合，历史导入拒绝时保留原数据集和原始库。任意拟合通道选择尚未实现，该保护不代表支持任意协议布局直接拟合；需先整理为明确三轴 CSV。

新增证据包括：默认采集/改正失败时真实 TCP 同时到达 SQLite 与图表；CTMBS 状态响应不入测量库、坏长头即时恢复；变长载荷的动态校验与帧尾；单位迁移、回放与异单位轴范围；真实 SQLite 写入失败后的安装阻止、恢复重试、正常关闭尾帧。`InstallerHandoffTests` 的三个真实进程测试验证应用及互斥锁退出后才启动替身安装器、超时不启动、失效进程不能完成交接。

复审补充覆盖：CTMBS 无数据 ERR 不结束命令等待，损坏简单响应后重新同步，UTC 午夜/跨年日期选择；CSV 在导出中、末尾及空会话确定性取消并清理临时文件。`StorageFaultAcquisitionTests` 使用真实 SQLite 故障，验证 UI 阻塞/断连延迟时停止接收、同包故障后的剩余帧拒收、定时尾批屏障、旧任务不影响新会话，以及重试成功后恰一次停止通知和单击重连。

保存接纳再补充 5 项回归：第二帧已经解析、第一批恰在后台 SQLite 失败时，第二帧被拒绝且不增加接收计数或进入显示流；首条已接纳数据保留、重试恰好保存一次，之后可重新连接。四项 Core 用例验证故障关闭接纳、关键消费者拒绝或抛错不发布普通事件，以及准备期间发生故障不能重新开放接收。已接纳后才发生故障的读数仍保留计数、显示和待写责任；接纳不是事务提交。

另有两项三 ViewModel/SQLite 回归，在实时会话异步准备的确定位置结束或手动停止历史回放，确认只结束历史显示，不取消实时连接或清空其单位，立即到达的首帧完整入库且历史会话不变。

进一步覆盖匹配但巨大的 CTMBS 长度头后立即恢复、`dat+0` 收到完整有效实时帧后不再误报超时，以及两个 ZDZ 预设的设备存储下载被拒发（以随后普通命令的 TCP 字节顺序证明未写出下载前缀）。设备内部历史下载未实现隔离流程，不属于已支持功能。区间导出的名称和单位与数值在同一数据锁内复制，后台写出不再读取当前会话单位。

回放计时新增 6 项真实 SQLite/WPF 回归：标称 1000 Hz 与不均匀 0/4/10 秒记录、不同倍速、暂停续播、定位、动态及非法速度，以及 3000001 ticks 的精确末帧完成。会话标称采样率不用于重写记录时间轴。

CTMBS 通用重复长度封装中的合法状态/参数响应及多组 dat+5 响应不发布实时测量，也不计解析失败；损坏封装及可识别测量结构中的无效字段继续报错。单组 dat+5 若与实时帧完全相同且无请求标识，解析器无法区分来源，不能宣称实现批量请求隔离。新增播放/暂停转实时的完整三 ViewModel 回归，验证连接返回前的首帧进入新 SQLite 会话，历史数据不混入。

`pmr+clock` 使用文档明确规定的不重复长度格式 `$14\nYYYYMMDDHHMMSS\nack\n`，有效时间响应仅被消费，不产测量或报错；其他封装仍校验重复长度与帧尾。测试覆盖分割位置、逐字节输入、合法时间边界、无效时间/帧尾和损坏响应后的同步。真实 TCP 用逐片接收屏障验证时钟响应不增加计数或进入显示流，前后仅两条实际测量入库。

改正版本新增真实 SQLite/CSV 验证：两组配置、通道重排、同 ID 的偏移/矩阵修改分别形成独立版本；同版本重试不重复，旧 ID 可读，原始导出不变，CSV 写入完整版本标识。计算前冻结参数，异步操作不受随后编辑影响。5 项 WPF 回归覆盖会话切换后的过期成功/失败、清除选择、并发刷新和旧 ID；下拉只查版本 ID，不加载整组改正数据。

正交度单位新增 28 项回归：nT 参数拒绝 uT/mT/T 数据，µT/μT/uT 别名兼容；真实 TCP → ViewModel → SQLite 验证拒绝改正时仍保存和显示原始数据；历史回放及批量映射同样校验两组参数。真实 SQLite 旧表迁移和 JSON 保持缺单位参数为未知，不能自动标为 nT。50 uT 球样本执行实际拟合；WPF 验证输入单位冻结、参数保存、在途换数据后丢弃过期结果和清空旧参考场强，各单位下相同物理残差得到一致质量评级。

更新验证新增 20 项 HTTP handler/临时文件测试：有效 SHA256 是缓存复用及新包改名的必要条件，缺清单/条目、坏哈希、HTTP 失败和取消均不接受包。另有 9 项发布附件选择测试，验证精确版本与 win-x64 文件名，避免误选旧包、其他产品或架构。没有访问真实更新服务或运行安装器。

串口仍是环境跳过；未运行真实安装器、实体设备或长时间吞吐验收。便携 ZIP 另用 `build.ps1` 的实际 `Compress-Archive` 命令进行临时目录归档，检查可执行文件与 `portable.marker` 位于 ZIP 根目录并保留子目录；该检查不是一次正式发布。

## 2026-10-07 验证记录（界面重设计，v0.5.3）

环境为 Windows 本机，分支 `codex/ui-redesign`（基于 `master` v0.5.2，版本号升至 0.5.3，经 GitHub PR 审查），Debug 构建。`dotnet build MagnetometerSystem.sln -c Debug --no-restore` 0 错误（只有原有 `NU1701`），`dotnet test MagnetometerSystem.sln -c Debug --no-build --no-restore -m:1`：

| 项目 | 通过 | 跳过 | 失败 |
| --- | ---: | ---: | ---: |
| Core | 430 | 1 | 0 |
| Infrastructure | 134 | 0 | 0 |
| Feedback.Server | 11 | 0 | 0 |
| App | 122 | 2 | 0 |
| 合计 | **697** | **3** | **0** |

跳过项：两个串口测试（未设端口变量）与 `AnalysisPerformanceTests`。`WpfTestHost` 现在打开 WPF 绑定跟踪（未附加调试器时默认关闭），各测试“无绑定错误”的断言此前实际上收不到任何输出；打开后全部通过。

虚拟串口：本机 ELTIMA Virtual Serial Port 的 COM1 ↔ COM2，115200 8N1。设 `MAGNETOMETER_TEST_RX_PORT=COM2`、`MAGNETOMETER_TEST_TX_PORT=COM1` 后，`OptionalSerialLoopbackTests` 与 `OptionalSerialChainTests` 均通过（后者见上文“可选串口与设备验收”）；链路条、原始报文与数据页截图经目视检查。这是虚拟串口业务链路，**不是实体适配器或真实数采卡验证**；数采卡的 CRC 参数仍待固件确认，内置预设因此仍默认禁止采集。没有手动运行程序逐项操作。

## 2026-10-07 验证记录（界面重设计续做，旧基线）

环境为 Windows 本机、当前工作区修改（未提交）、Debug 构建。运行 `dotnet build MagnetometerSystem.sln -c Debug`（0 错误，只有原有 `NU1701` 警告）后执行 `dotnet test MagnetometerSystem.sln -c Debug --no-build`：

| 项目 | 通过 | 跳过 | 失败 |
| --- | ---: | ---: | ---: |
| Core | 314 | 1 | 0 |
| Infrastructure | 65 | 0 | 0 |
| App | 33 | 1 | 0 |
| 合计 | **412** | **2** | **0** |

跳过项：`OptionalSerialLoopbackTests`（未配置互连串口）与 `AnalysisPerformanceTests`（可选性能实测，需 `MAGNETOMETER_TEST_PERF=1`；单独运行一次通过，数值见[需求池 REQ-005](需求池.md)）。本轮没有保存 TRX；界面截图来自 `MAGNETOMETER_TEST_SCREENSHOTS` 输出并经目视检查，没有手动运行程序逐项操作，没有串口或真实设备验证。

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
| 保存失败 | 模拟写入异常/数据库锁；首错立即停收、保留已接受批次，UI阻塞不延误门控，重试恢复无重复且不影响新会话 | 故障集成测试 + UI |
| 默认不校正 | 不选校正配置，保存与绘图完整可用 | 业务链路 + UI |
| 校正保护原始值 | 用已知非单位矩阵产生不同结果，验证校正前数值仍可查询并导出；额外通道如温度不被三轴算法改变 | Core + SQLite + CSV |
| 显示处理与保存隔离 | 暂停显示、滤波、显示偏移、清图、降采样、折叠和切页，数据库仍按原始数值记录 | 业务链路 + UI |
| 单图/多图 | 选中磁场与温度；单图只有一个绘图区且温度右轴，多图按通道绘制 | Windows WPF |
| 折叠/专注 | 右侧任务面板与底部停靠区独立开合，停靠区高度可拖动；专注模式增大曲线，退出恢复开合及输入，链路条的连接/保存状态仍可见 | Windows WPF |
| 分析功能 | 滚动统计/区间、计算通道、单位与索引、图形选择和导出保持一致 | Core + UI |
| 历史回放 | 加载指定会话、暂停/定位/倍速/切页；通道与单位从会话恢复，温度轴正确；实时连接存在时禁止回放，回放仅发布显示流 | 业务链路 + UI |
| CSV 一致性 | 与数据库逐条对账；验证通道顺序、时间筛选、逗号/引号名称、小数精度和不同系统区域设置 | SQLite + CSV |
| 命令真实写出 | 通过业务发送入口发送参数化 ASCII/HEX/二进制命令，由 TCP 服务端或串口另一端捕获；逐字节比较行尾、长度、字节序、校验 | TCP + 串口 |
| 命令响应 | 覆盖无响应、延迟、拒绝、无关帧、匹配 ACK；只有协议明确成功时才称执行成功 | 协议集成 + 设备 |
| 退出应用 | 收包期间关闭窗口，确认连接释放、尾批完成，重新打开可看到最后记录 | Windows WPF + SQLite |
| 旧库保护 | 识别旧固定列表并保留 legacy 表、会话计数和迁移提示；未转换数据不得显示为空库成功 | SQLite |

矩阵是验收要求。某行存在单元测试，并不意味着该行所有层次都已覆盖；报告必须给出实际执行层次。

### 界面验收（2026-10 重设计后）

以下需要在 Windows 上 `dotnet run` 后手动操作，自动测试只覆盖其中的绑定和 ViewModel 逻辑。记录时写明结果、日期和环境；没有记录的视为未验证。

| 场景 | 操作与断言 | 状态 |
| --- | --- | --- |
| 链路条计数 | 串口真实设备连接后，接收 / 解析 / 保存三段各自增长，实测频率与设备输出一致；断开后事件出现“会话已结束，尾批已提交”，数据页条数一致 | 未执行 |
| TCP 断线重连 | 重连期间链路条显示“连接中断”，数据页回放按钮禁用并说明原因；重连后计数继续 | 未执行 |
| 保存失败横幅 | 用其他程序独占锁住数据库：出现红色横幅、待写条数不清零；“重试写入”恢复后横幅消失，事件记“保存已恢复”，无重复或丢失 | 未执行 |
| 显示操作不影响保存 | 暂停、折叠侧栏 / 停靠区、专注、切页、显示偏移、滤波前后，保存条数与设备发送数一致 | 未执行 |
| 断开后曲线保留 | 停止采集后曲线仍在，空状态只在清空曲线或从未采集时出现 | 业务链路通过（TCP），UI 未手动检查 |
| 原始报文逐帧记录 | 改坏设备一帧的校验字节（或用 TCP 工具发送），看到“✗ 校验失败：计算 xx，帧内 yy，已重新同步”和随后的“⚠ 丢弃 N 字节” | 业务链路通过（TCP），未用真实设备 |
| 解析测试 | 粘贴真实设备的一段 HEX 输出，结果与实时解析一致；“解析过程”列出拒绝原因 | 未执行 |
| 曲线拖动选区间 / 十字准线 | 左键拖动后右侧“区间”页显示统计；悬停时读数为原始值；滚轮仍缩放时间窗口 | ViewModel 通过，鼠标操作未手动检查 |
| 收发记录 | 发送有应答的命令：发送行“已写出 N 字节”，应答行“应答匹配，x ms”；无应答时出现超时说明 | 业务链路通过（TCP 对端模拟），未用真实设备 |
| 正交度手动 48 点 | 在任意页面用链路条“记录当前点”，向导里的格子与计数同步；撤销 / 清空后原始 CSV 追加注释行 | ViewModel 通过，未用真实设备 |
| 数据页导出 | 只勾部分通道，CSV 列名、单位、精度正确；旧格式会话显示“需迁移”且不能回放 / 导出 | 未执行 |
| 分析页 | 已知漂移的长会话，漂移速率与预期一致；取消可用；导出 CSV 首行带时间段与设置 | ViewModel 通过（临时 SQLite），UI 未手动检查 |
| 最小窗口 | 1100×680 下各页没有被截断或重叠的控件（含对话框） | 测试截图检查，未手动检查 |

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

同样的两个变量也启用 App 层的 `OptionalSerialChainTests`（RX 为上位机端，TX 为模拟设备端）：先确认内置“磁梯度数采卡-pt (仅磁场6通道)”在 CRC 未配置时拒绝连接、不打开串口、不建会话；再把 CRC 占位段换成校验段（测试取 CRC-16/MODBUS，从“信息ID”段算起，低字节在前——这是测试假设，不是已确认的固件参数），从 TX 口随机切块发送 400 个有效 101 字节帧并混入噪声、半帧和坏 CRC 帧，经真实 `ConnectionViewModel` 断言解析数、接收字节数、逐帧解析记录、图表最新值、断开后 SQLite 中每个原始值与通道名/单位，以及数据页显示的条数：

```powershell
dotnet test tests/MagnetometerSystem.App.Tests/MagnetometerSystem.App.Tests.csproj -c Debug --filter FullyQualifiedName~OptionalSerialChainTests
```

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

## 2026-10-05 双平台更新开发验证

在基于 `459f881` 的独立 `codex/dual-platform-updates` 工作区，Windows、本机 Debug 构建后执行：

```powershell
dotnet test MagnetometerSystem.sln -c Debug --no-restore -m:1 --logger trx --results-directory .codex_tmp/test-results/final-dual-platform
```

| 项目 | 通过 | 跳过 | 失败 | 本机 TRX 时间 |
| --- | ---: | ---: | ---: | --- |
| Core | 414 | 1 | 0 | 01_02_25 |
| Infrastructure | 134 | 0 | 0 | 01_02_27 |
| App | 92 | 0 | 0 | 01_02_36 |
| 合计 | **640** | **1** | **0** | 本机 `.codex_tmp/test-results/final-dual-platform` |

新增 21 项更新服务与 3 项 WPF 回归：自动比较版本、指定平台、同版本附件完整性、两边不同步不降级、部分/全部接口失败诊断、草稿/预发布过滤、GitHub 发布时间、网络下载失败/中途断流的同版本镜像切换、重新校验镜像自身清单、坏校验拒绝、取消不切换；WPF 与真实 SQLite 验证平台偏好重启保持、退出时全局设置写回不覆盖、更新窗口选源与下载中禁选、两项目链接。其余更新安装交接与采集保存回归仍通过。

使用修改后的更新器匿名访问真实发布：Gitee 正常识别 V0.5.0；GitHub 返回 HTTP 403 `rate limit exceeded`，自动模式仍从 Gitee 获取更新并保留 GitHub 诊断，指定 GitHub 模式如实报告失败。这不是 GitHub 匿名查询成功的证据。两平台正常响应与镜像切换由隔离 Handler 的完整解析/下载/校验调用链验证；本次未重新做两平台全量发布包外网下载。

渲染并目视检查了关于、平台设置及更新选择窗口；截图为本机 `.codex_tmp/update-ui` 中的 `about-project-links.png`、`settings-update-sources.png`、`update-github.png`。上述 TRX、截图与临时联网探针不提交源码。构建保留原有 NU1701 警告；没有串口环境、没有执行真实安装升级。此为开发分支验证，未修改或重新发布既有 V0.5.0 发布包。

## 2026-10-05 匿名反馈实现与部署验证

新增桌面表单、Core 反馈契约、独立草稿、匿名 HttpClient、ASP.NET Core 接收服务及 SQLite 待同步任务。姓名和联系方式保存在私有数据中，不加入 GitHub 正文或公开回执。真实临时 HTTP/SQLite 测试验证重复提交、丢失响应、服务重启、中文最大长度、并发去重、限流、授权失败与未知 POST 结果核对；WPF 测试验证两项必填、可选信息、非模态窗口、重启重试及草稿保存失败时不发送。

部署验证通过 Windows 真实 FeedbackViewModel → FeedbackClient → 公网可信 HTTPS → 服务器 SQLite，并验证相同编号重试、服务器重启后保存及公开回执隐私。测试 CA 续期和部署钩子演练通过；新接收、代理、续期定时器及原有 Caddy 均保持运行。两条专用部署测试记录已定点删除，未向正式 GitHub 仓库建测试 Issue。

上述初次部署验证时尚未配置 GitHub 授权；同日维护者完成配置后，补充真实建单验证，见下文。串口对跳过不等于真实设备通过。维护与授权见 [反馈部署](feedback-deployment.md)。本地 TRX 证据在忽略目录 .codex_tmp/test-results/final-feedback-validated/。

最终 Windows 验证：构建通过；Core 414 通过 / 1 串口环境跳过，Infrastructure 134 通过，App 96 通过，Feedback.Server 11 通过，总计 **655 通过、0 失败、1 跳过**。默认构建已注入公开 HTTPS 反馈地址。公网最大允许中文场景（5000 字）与描述（20000 字）通过真实桌面调用链保存，未截断；这些部署测试记录已清除。没有实际串口验证。

维护者配置服务端 GitHub 令牌后，Windows `FeedbackViewModel → FeedbackClient → 公网可信 HTTPS → SQLite → GitHub` 完整链路通过：编号 `6e3a76b9-bb5c-40b4-bc8e-986d5d1d654a` 生成 [Issue #5](https://github.com/YIALU/MagnetometerSystem/issues/5)，回执状态为 synced。通过服务器内部 GitHub API 核验场景及描述一致、姓名和联系方式未公开、标签为 feedback / needs-triage；首次重复提交与重启反馈后台后的相同编号重试均对应一条数据库记录和一个 Issue。测试单已关闭，数据库回执保留用于追溯；接收、代理、续期定时器及原有 Caddy 均正常运行。该验证使用真实桌面 ViewModel 调用链，不代表已经发布新版安装包，也未同时连接实际串口设备。
