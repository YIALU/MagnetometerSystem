# DJI MSDK Android 数据采集软件 — 设计规格

> 日期: 2026-08-29
> 状态: 设计已确认,待写实现计划
> 关联参考: 本仓库 `MagnetometerSystem`(WPF/.NET 磁力仪采集系统)架构平移

## 1. 目标与范围

### 1.1 目标
开发一款运行于 Android(带屏遥控器,如 DJI RC Plus)的大疆 MSDK v5 应用,功能对标本仓库 `MagnetometerSystem`:从挂在无人机上的 **PSDK 自定义载荷**(磁力仪/数采卡)接收二进制数据帧 → 解析 → 存储 → 实时可视化,并可向载荷发送命令。

### 1.2 范围(纳入)
- PSDK 载荷二进制帧的接收、解析、存储、可视化
- 向载荷发送命令(`SendDataToPSDK`)
- 会话管理(开始/停止采集、会话列表)
- 历史回放
- 配置化协议(JSON 定义帧结构,与仓库 `ProtocolConfig` 同构)

### 1.3 范围(排除)
- 飞行控制(起飞/降落/返航/VirtualStick)
- 航点任务规划与自动飞行
- 合并无人机自身遥测(载荷帧内已含 GPS,不并入 MSDK `FlightControllerState`)
- 校准/正交性校正(本仓库的 `OrthogonalityCalibration` 子系统不纳入 v1;载荷自带校正时按帧内字段直接使用)
- 应用内更新、反馈、双模式打包(本仓库 v0.4 的发布侧功能不纳入)

### 1.4 非功能目标
- 解析/命令/图表算法层与 Android、DJI SDK 解耦,可 JVM 单测
- 单一 PSDK 触点:`:core` 不 import 任何 `android.*` / `dji.*`,MSDK 仅出现在 `:app` 的 `PsdkDeviceConnection` 一个文件
- 仓库现有 `Protocols/*.json` 协议配置文件原样复用(JSON schema 逐字兼容)

## 2. 技术栈与目标机型

| 项 | 选型 |
|---|---|
| 平台 | Android,MSDK v5(Kotlin-first) |
| 机型 | M300 RTK / M350 RTK + PSDK 载荷 |
| 语言 | Kotlin |
| UI | Jetpack Compose |
| 图表 | Compose 图表库(Vico;用 `:core` 的 `CircularBuffer` + LTTB 驱动) |
| 持久化 | Room(SQLite),`readings.data` 列存 JSON-blob |
| 异步 | Kotlin 协程 + Flow |
| DI | Hilt(或 Koin;Hilt 为默认) |
| 序列化 | kotlinx.serialization(JSON 协议配置 + 存储 blob) |
| 日志 | Timber |

## 3. 模块架构

三个 Gradle 模块,依赖方向单向:`:app` → `:data` → `:core`。

```
:app   (Compose 应用, MSDK v5, PSDK 传输实现, DataBus, ViewModels)
  ├─ :data   (Room/SQLite + 批量入库队列, SqliteStorageService)
  │    └─ :core  (纯 Kotlin: 协议/命令/传输接口/图表算法/存储接口)
```

### 3.1 `:core` — 纯 Kotlin/JVM 模块
无 `android.*` / `dji.*` 依赖。从仓库 `MagnetometerSystem.Core` 平移:

- **传输接口** `DeviceConnection`:进来 `val incoming: Flow<ByteArray>`,出去 `suspend fun send(bytes: ByteArray)`;生命周期 `suspend fun connect()` / `suspend fun disconnect()`;状态 `val state: StateFlow<ConnectionState>`。对应仓库 `IDeviceConnection` 的 `DataReceived` 事件 + `SendAsync`,事件改成 `Flow`。
- **协议层**:`ProtocolConfig`(POCO + `@Serializable`,与仓库 JSON schema 逐字兼容)、`FrameSegment`、`ConfigurableBinaryParser` + `ByteRingBuffer`(黏包/长度字段/校验/固定值锚点全保留)、`ConfigurableAsciiParser`、`ParserFactory`、`Crc16`。
- **命令层**:`DeviceCommand` / `CommandGroup` / `CommandCatalog` + `CommandFrameBuilder`(ASCII 模板 / 二进制帧,带类型化参数 `U8..Float64`、`EnumMap` 下拉、Sum8/Xor8/CRC16-Modbus 校验)。
- **图表算法**:`CircularBuffer`(每通道上限 10 万点)、`LttbDownsampler`(目标 ~2000 点)、`FormulaEvaluator`(计算通道:`sqrt(x²+y²+z²)` 等)。
- **存储接口** `IDataStorageService`:由 `:data` 实现。
- **模型**:`Reading`(时间戳 + `Map<String,Double>` 通道值,对应仓库 `MagnetometerReading`)、`Session`、`SensorConfig`。

### 3.2 `:data` — Android 库模块
从仓库 `MagnetometerSystem.Infrastructure` 平移:

- Room 数据库 + `DatabaseInitializer`(嵌入 `Schema.sql`)。
- `readings` 表:`id`, `session_id`(FK, ON DELETE CASCADE), `timestamp`, `data`(TEXT, JSON-blob: `{"values":{name:val}, "original":{...}}`)。
- `sessions` 表:`id`, `name`, `started_at`, `ended_at`, `sensor_type`, `sample_rate`, `channel_count`, `channel_names`(JSON), `device_info`, `connection_type`, `notes`, `total_readings`。
- 索引 `(session_id, timestamp)`。
- `SqliteStorageService`(实现 `:core` 的 `IDataStorageService`):`SaveReading` 仅投递到 `Channel<Reading>`(单消费者);后台 `consumeWriteQueue` 批量 500 条/事务写盘,`SQLITE_BUSY`/`SQLITE_LOCKED` 退避重试(3 次,50/150/450ms);`WaitForPendingWrites` 确认排空。
- `AppConfigService`(`settings` / `user_preferences` 表持久化命令目录等配置)。
- `CsvExporter`(CSV 导出)。

### 3.3 `:app` — Compose 应用
从仓库 `MagnetometerSystem.App` 平移:

- `MainViewModel` + 子 ViewModel:`ConnectionViewModel`、`RealtimeChartViewModel`、`DeviceCommandViewModel`、`SessionListViewModel`、`HistoryPlaybackViewModel`、`SettingsViewModel`。
- `DataBus`:见 §5。
- **`PsdkDeviceConnection`**(`:core` 的 `DeviceConnection` 唯一实现):把 MSDK `DataFromPSDK` 回调桥接成 `incoming` 流(回调线程 → `Flow` 的 `emit`/`tryEmit`,加背压缓冲);`send` 调 `SendDataToPSDK`。
- MSDK 初始化/注册/激活:`Application.onCreate`,机型 `ProductType` 针对 M300/M350 RTK。
- Compose 图表:由 `RealtimeChartViewModel` 持 `CircularBuffer`,渲染 tick(LTTB 降采样)后产出 Compose 图表数据。

### 3.4 关键约束
- `:core` 零 Android/DJI 依赖 → 解析器、命令构建器、图表算法可 JVM 单测。
- MSDK 仅出现在 `:app` 的 `PsdkDeviceConnection` 一个文件 → 换载荷/换测试桩不动下游。

## 4. 组件清单

| 组件 | 模块 | 职责 | 仓库对应物 |
|---|---|---|---|
| `DeviceConnection` | `:core` | 传输接口(进 Flow / 出 suspend send) | `IDeviceConnection` |
| `PsdkDeviceConnection` | `:app` | PSDK 回调↔Flow 桥接 + SendDataToPSDK | `SerialDeviceConnection`/`TcpDeviceConnection` |
| `ProtocolConfig` | `:core` | JSON 可配协议定义 | `ProtocolConfig` |
| `ConfigurableBinaryParser` | `:core` | 段式二进制帧解析 | 同名 |
| `ByteRingBuffer` | `:core` | 黏包环形缓冲 | 同名 |
| `ParserFactory` | `:core` | 按 config 选解析器 | 同名 |
| `DeviceCommand` / `CommandFrameBuilder` | `:core` | 命令模型 + 帧构建 | 同名 |
| `IDataStorageService` / `SqliteStorageService` | `:core`/`:data` | 批量入库 | 同名 |
| `DataBus` | `:app` | 进程内发布订阅 + 采集状态 | `DataBus` |
| `RealtimeChartViewModel` | `:app` | 实时图表引擎 | 同名 |
| `MainViewModel` | `:app` | 编排/导航/跨 VM 接线 | 同名 |
| `ConnectionViewModel` | `:app` | 收→解→处理→发布 主回路 | 同名 |
| `DeviceCommandViewModel` | `:app` | 命令目录 UI + 发送 | 同名 |
| `SessionListViewModel` | `:app` | 会话列表 | 同名 |
| `HistoryPlaybackViewModel` | `:app` | 历史回放 | 同名 |

## 5. 数据流与 DataBus

### 5.1 主回路(收→解→存→画)
`ConnectionViewModel` 是集成点(对应仓库同名 VM 的 `ConnectAsync` + `OnDataReceived`):

1. 用户开始采集 → `DataBus.publishAcquisitionStarting(SensorConfig)`(**await** 顺序):存储先建会话、设 `ActiveSessionId`,保证首字节到达前会话已就绪(不丢首帧)。
2. `PsdkDeviceConnection.connect()`(PSDK 通道就绪)→ `DataBus.publishConnectionChanged` → `DataBus.publishAcquisitionStarted`(图表等非关键消费者初始化)。
3. `incoming: Flow<ByteArray>` 每段字节 → `parser.feed(bytes)` → 循环 `parser.tryParse(out Reading)`。
4. 每个 `Reading` → `DataBus.publishReading(reading)`。

### 5.2 DataBus(Kotlin SharedFlow)
`SharedFlow<ReadingEvent>` 的发布订阅,保留仓库两个关键设计:
- **每订阅者 try/catch 隔离**:图表异常不得饿死存储(对应仓库 `PublishReading` 遍历 `GetInvocationList` 隔离)。
- **采集前 await 时序**:`AcquisitionStarting` 是 `suspend` 事件,存储在连接打开前完成建会话。

事件:
- `ReadingReceived`(热路径,`SharedFlow`,带缓冲,溢出策略 `DROP_OLDEST` 或缓冲足够大避免丢)
- `AcquisitionStarting`(`suspend`,顺序 await,建会话)
- `AcquisitionStarted`(连接后,图表初始化)
- `AcquisitionStopped` / `SessionStarted` / `SessionEnded` / `ConnectionChanged`

状态:`currentConnection`(`DeviceConnection` 供命令 VM 抓取)、`isPlaybackMode`(回放时跳过入库)。

发布者:`ConnectionViewModel`(读数 + 生命周期)、`SqliteStorageService`(会话起止)。
订阅者:`RealtimeChartViewModel`、`SessionListViewModel`、`DeviceCommandViewModel`、`HistoryPlaybackViewModel`。

### 5.3 写入路径
`SqliteStorageService.saveReading` → `Channel<Reading>`(单消费者,容量充足) → `consumeWriteQueue`:批量 500 条/事务,`SQLITE_BUSY` 退避重试,`WaitForPendingWrites` 排空确认。通道数据按通道名存 JSON,跨协议通道重排仍一致(对应仓库 `ExtractOrdered`)。

### 5.4 可视化路径
`RealtimeChartViewModel` 订阅 `ReadingReceived` → 每通道 `CircularBuffer`(10 万点上限,`_dataLock` 互斥)→ 渲染 tick(默认 ~30 FPS)快照 → 可选滤波 → LTTB 降采样 → Compose 图表数据。单图叠加 / 多图模式;计算通道(`FormulaEvaluator`)。区间分析(选时段算均值/标准差/峰峰值,CSV 导出)。

### 5.5 命令路径
`DeviceCommandViewModel` 持命令目录(内置协议组 + 用户组)。选中命令 → 每参数绑定(`EnumMap` 下拉 / 自由文本)→ `CommandFrameBuilder` 实时预览 → `currentConnection.send(bytes)`(即 `SendDataToPSDK`)。自由发送框(hex/ASCII)。内置组只读。协议切换时 `MainViewModel` 把 `ProtocolConfig.Commands` 转发给命令 VM。RX 日志(100ms 批冲刷,避免逐帧 UI 抖动)。

## 6. 错误处理

- **传输层**:`PsdkDeviceConnection` 暴露 `state: StateFlow<ConnectionState>`(Disconnected/Connecting/Connected/Error)。PSDK 回调异常隔离,不污染 `incoming` 流;断链自动状态置位,`ConnectionViewModel` 据此更新 UI 并停止采集。
- **解析层**:校验失败(头/尾/长度/校验/固定值锚点)丢弃坏帧并计数,不抛出;`ByteRingBuffer` 满做丢弃最旧处理。解析错误指标暴露给 UI(坏帧计数)。
- **DataBus 隔离**:每个 `ReadingReceived` 订阅者 try/catch;一个消费者异常被记录(Timber)但不影响其他。
- **存储层**:`SQLITE_BUSY`/`LOCKED` 退避重试 3 次;写队列投递失败(通道满)记录并丢该帧(热路径不得阻塞解析)。`WaitForPendingWrites` 在停止采集时调用,保证落盘。
- **命令层**:`send` 异常上报命令 VM(TX 失败计数);参数校验(`ByteLength`/范围)在 `CommandFrameBuilder` 预览阶段拦截,未通过则禁用发送。
- **生命周期**:采集退出时按序 `publishAcquisitionStopped` → `WaitForPendingWrites` → `disconnect` → 关闭写消费者协程。

## 7. 测试策略

- **`:core` JVM 单测**(无需 Android/MSDK):
  - `ConfigurableBinaryParser`:黏包/分片帧、长度字段、校验(含各 CRC16 变体)、固定值锚点误锁定防护、各 `DataField` 类型解码 + `Scale`/`Offset`。用真实 `ProtocolConfig.CreateZdzC08()`(101B/21 通道)等内置协议喂录制字节流断言。
  - `CommandFrameBuilder`:ASCII 模板占位符替换、二进制帧拼装、各参数类型编码、`EnumMap` 反查、各校验算法。
  - `CircularBuffer` / `LttbDownsampler` / `FormulaEvaluator`:边界与降采样保形。
  - JSON 往返:`ProtocolConfig` 序列化/反序列化与仓库 `Protocols/*.json` 兼容(可直接加载仓库现成 JSON 断言)。
- **`:data` 插桩测试**(Room Android 测):批量写入、`SQLITE_BUSY` 重试、`WaitForPendingWrites` 排空、JSON-blob 通道名一致性。
- **`:app` PSDK 桥接测试**:用伪 `DeviceConnection`(向 `incoming` 注入录制字节)替代 `PsdkDeviceConnection`,驱动 `ConnectionViewModel` 全回路(收→解→DataBus→入库桩→图表状态),验证首帧不丢(会话预建时序)、消费者隔离、停止排空。MSDK 真机联调另列(非自动化)。

## 8. 与仓库的复用对照

| 仓库模式 | 本项目处理 |
|---|---|
| `IDeviceConnection`(事件式) | `DeviceConnection`(Flow 进 + suspend send 出) |
| `SerialDeviceConnection`/`TcpDeviceConnection` | `PsdkDeviceConnection`(单一 PSDK 实现) |
| `ConfigurableBinaryParser`/`ByteRingBuffer`/`ProtocolConfig` | 直接平移为 Kotlin,JSON schema 兼容 |
| `DataBus`(C# 事件) | `SharedFlow` + 每订阅者隔离 + 采集前 await |
| `SqliteStorageService`(Dapper + Channel) | Room + `Channel<Reading>` 批量写,JSON-blob 按通道名 |
| `RealtimeChartViewModel`(ScottPlot) | Compose + Vico,`CircularBuffer`+LTTB 平移 |
| `DeviceCommand`/`CommandFrameBuilder` | 直接平移,`send` 走 `SendDataToPSDK` |
| `OrthogonalityCalibration` | 不纳入 v1 |

## 9. 未决项
- Hilt vs Koin:默认 Hilt,实现计划阶段确认。
- 图表库最终选型:默认 Vico,如不满足实时点量再评估(自绘 Canvas)。
- MSDK v5 具体 SDK 版本号与 `ProductKey`/`AppKey`:实现计划阶段填入。
- PSDK `DataFromPSDK` 回调到 `Flow` 的背压缓冲容量上限:实测后定。
