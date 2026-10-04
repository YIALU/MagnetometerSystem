# Windows 与 Linux 板通信协议文档

> 适用对象：在 Windows 侧开发上位机/客户端程序、需通过网口与嵌入式 Linux 板（X2000 平台，本仓库 eq_precursors 程序）通信的开发者。
> 文档基于源码 `src/TcpServer.*`、`src/TcpSession.*`、`src/WebServer.*`、`src/HttpSession.*`、`src/WebSocketSession.*`、`src/CommandParser.*`、`src/SessionManager.*`、`src/DownloadManager.*`、`main.cpp` 及配置文件 `netParam.json` 等整理。

---

## 1. 通信模型总览

Linux 板上运行的 `eq_precursors` 程序对外提供 **三条以太网通信通道**，全部基于 TCP，监听 `0.0.0.0`：

| 通道 | 传输层 | 默认端口 | 用途 | 实现类 |
|------|--------|----------|------|--------|
| **命令通道** | 裸 TCP（自定义文本帧） | `81`（`serverPorts.command`） | 设备指令、数据查询、参数配置、用户认证、实时数据推送 | `TcpServer` / `TcpSession` |
| **HTTP 通道** | HTTP/1.1（Boost.Beast） | `8080`（`serverPorts.http`） | Web 静态页面、文件下载（令牌制）、WebSocket 升级 | `WebServer` / `HttpSession` |
| **WebSocket 通道** | WS（在 HTTP 通道上升级 `/ws`） | `8080` | 浏览器/上位机的双向命令交互（与命令通道同协议） | `WebSocketSession` |

> 另有 `serverPorts.ftp = 21`、`management.port = 1024` 配置项，FTP 由系统服务提供（非本程序），`management` 用于板间管理，上位机通常不直接使用。

Windows 侧的两种推荐接入方式：

1. **原生 TCP 命令通道（端口 81）** —— 适合 C++/C#/Python 等自研上位机，延迟最低，实时数据推送走此通道。
2. **WebSocket 通道（端口 8080，`/ws`）** —— 适合浏览器内 Web 上位机（本项目自带 `web_root/`），帧格式与命令通道完全一致，额外支持心跳。

文件下载**不**在命令通道内直接传输二进制，而是命令通道返回一个**一次性令牌 URL**，由 Windows 端通过 HTTP GET 拉取（见 §10）。

---

## 2. 网络参数（netParam.json）

```json
{
  "commandOutputMode": "perSecond",          // 输出模式：perSecond / perMinute
  "deviceNetwork": {                         // 板自身网络（注：配置中 IP 以三位补零格式存储）
    "gateway": "127.000.000.002",
    "ipAddress": "127.000.000.001",
    "subnetMask": "255.255.255.0"
  },
  "management": { "ipAddress": "10.13.64.1", "port": 1024 },
  "serverPorts": { "command": 81, "ftp": 21, "http": 8080 },
  "sntpServer": "10.5.67.14"
}
```

- 实际下发/读取时，IP 会去掉前导零（如 `127.000.000.001` → `127.0.0.1`，见 `removeLeadingZeros`）。
- 修改网络参数（命令 `set+n`）会持久化到 `netParam.json` 并通过 `CommandSystemOps::updateIp` 实时应用到 `eth0`，**可能导致连接 IP 变化**，上位机需具备重连能力。
- 端口可通过命令通道动态修改（`set+n` 模式 3），修改后命令/HTTP/FTP 端口都会变。

---

## 3. 命令通道帧格式（TCP，端口 81）

### 3.1 请求帧

每条请求是一个 ASCII 文本行，格式（正则来源 `extractGetRequest`）：

```
GET /<content> /http/1.1
```

正则（大小写不敏感）：`^GET\s+/(.+)\s+/http/1\.1$`

其中 `<content>` 由 `+` 分隔为若干段：

```
<length>+<deviceID>+<command>[+<param1>+<param2>+...]
```

| 字段 | 说明 |
|------|------|
| `length` | 十进制整数，**等于整个 `<content>` 字符串的字节长度（含 length 自身数字与所有 `+`）**。见 §3.3 自参考长度算法。范围 `0..10000`。 |
| `deviceID` | 设备 ID，**必须 12 个字符，且首字符为 `X`**。结构：`X` + 测项代码(3) + 厂家标志(4) + 序列号(4)。例：`X122PWZK0000`（`X122` 测项 + `PWZK` 厂家码 + `0000` 序列号）。 |
| `command` | 命令字符串，**仅允许字母数字**（`^[a-zA-Z0-9]+$`），3 字符助记符，见 §5 命令表。 |
| `paramN` | 命令参数，可含子结构（内部用空格再分字段）。段数 ≥ 0；`content` 段数必须 ≥ 3（即至少 `length+deviceID+command`）。 |

**完整请求示例（登录）**：

```
GET /31+X122PWZK0000+lin+admin+admin /http/1.1
```

> 注意 `content` = `31+X122PWZK0000+lin+admin+admin`，其字节长度恰为 31（自参考长度，见 §3.3）。

### 3.2 响应帧

响应统一为 ASCII 文本，分三类：

#### (a) 带数据响应 —— `getResponseCmd(payload)` 生成

```
$<L>\n<L><payload>\nack\n
```

- `$`：1 字节起始标记。
- `<L>`：十进制长度，**等于 `<L 数字位数> + <payload 字节数>`**，即 `<L digits>` 之后到 `\nack\n` 之前共有 `L` 字节（恰好是 `<L digits><payload>`）。
- `\n`：分隔符。
- `<L digits><payload>`：恰 `L` 字节。`payload` 通常以一个空格 ` ` 开头（便于和长度字段视觉分隔）。
- `\nack\n`：固定尾部确认。

接收侧解析算法：读到 `$` → 读到第一个 `\n` 得到 `L` → 再精确读取 `L` 字节得到 `<L digits><payload>`（截掉开头的 L 数字即得 payload） → 读到 `\nack\n`。

#### (b) 简单确认

```
$ack\n
```

成功但无数据返回的命令（设置类、控制类）。

#### (c) 错误

```
$err\n
```

解析失败、权限不足、参数非法、执行异常等所有失败场景。

#### 特殊响应

- **登录成功**：`$ack\n` + 仪器 ID（`stationTestConde + manufacturerCode + stationSerialId`，例如 `X122PWZK0000`），**无尾部换行**：

  ```
  $ack\nX122PWZK0000
  ```

- **实时推送启停（内部信号，不外发）**：`$start_push\n` / `$stop_push\n` 是 `CommandParser` 返回给 `TcpSession` 的内部标识，客户端**不会收到**这两个串。客户端收到的是实际数据包或 `$ack\n`（见 §6）。

- **`pmr+clock`（取当前时间）**：响应格式与标准带数据响应略有差异，长度字段不加位数补偿：
  ```
  $<len>\n<time>\nack\n
  ```
  其中 `<time>` = `YYYYMMDDHHMMSS`（本地时区），`<len>` = `<time>` 的字节数（14）。

### 3.3 自参考长度算法（重要）

请求 `length` 与响应 `L` 都采用“长度值等于整串字节数”的自参考编码。客户端构造方法：

**请求侧**：构造 `inner = deviceID + "+" + command + ("+" + param)...`，需求 `L` 使 `"L" + "+" + inner` 的字节长度等于 `L`：

```
L = len(inner) + 1 + digits(L)
```

迭代求解（最多 2~3 次即收敛）：
1. 令 `L = len(inner) + 2`（先按 1 位估）。
2. `L = len(inner) + 1 + digits(L)`，重复至上式成立。

**响应侧**解析见 §3.2(a)，无需构造。

**命令行校验**：服务端 `validateCommandIntegrity` 限制 `length ∈ [0, 10000]`，超长拒绝。

### 3.4 连接生命周期

- **空闲超时**：`IDLE_TIMEOUT = 300 秒`。每次成功收发后重置；超时则服务端关闭连接。
- **缓冲区**：单次 `async_read_some` 缓冲 `1500` 字节。**单条请求不应超过 1500 字节**（含 `GET /... /http/1.1` 外壳）；超长请求需自行分片或改用其他通道。下载类命令的响应可能远超 1500 字节，服务端用 `async_write` 一次性发送，客户端需循环读到帧尾。
- **请求/响应交替**：一问一答。服务端发完响应后继续 `do_read`，客户端可立即下一条请求（无需等待新建连接）。
- 连接断开：客户端发 FIN（`eof`）即正常关闭；网络错误也关闭并清理会话。

---

## 4. 认证与权限模型

### 4.1 用户等级

```cpp
enum UserLevel { USER = 1, ADMIN = 2, SUPER_ADMIN = 3 };
```

- `USER`：可读状态、读工作参数、读属性、列出用户。
- `ADMIN`：在 `USER` 基础上，可数据传输、设置参数、复位/重启、校准/调零、CAN 控制、下载、用户增删改查（部分需 `SUPER_ADMIN`）。
- `SUPER_ADMIN`：改口令、加用户、改用户权限。

### 4.2 登录与会话

1. 客户端建立 TCP 连接后，**第一条业务命令通常是 `lin`（登录）**。
2. 登录成功后，服务端 `SessionManager` 以**物理连接 ID** 创建会话，会话与该 TCP 连接绑定。
3. 后续命令在该连接上即视为已登录，无需携带 token。
4. **会话不跨连接**：连接断开会话销毁；新连接需重新登录。
5. 默认账户（见 `web_root/config.js`，仅 Web 演示用）：`admin/admin`、`user/123456`。生产环境以 `datas.db` 中 `UserManager` 管理的账户为准。

> ⚠️ 当前协议为**明文口令、无加密**。如处敏感网络，应在外层叠加 TLS/VPN。

### 4.3 操作审计

几乎所有命令执行前都会 `USER_MGR.recordOperate(...)` 写入操作日志（`datas.db`），可通过 `log` 命令或 `ctl+downloadlog` 下载审计 CSV。

---

## 5. 命令参考表

`command` 三字母助记符 → 类型映射（源码 `command_map`）：

| 命令 | 类型 | 权限 | 说明 |
|------|------|------|------|
| `lin` | 用户登录 | — | 登录，参数：`username` `password` |
| `rpw` | 改口令 | SUPER_ADMIN | 参数：`newPassword` |
| `adu` | 加用户 | SUPER_ADMIN | 参数：`username` `password` `userLevel`(1/2/3) |
| `dlu` | 删用户 | ADMIN | 参数：`username` |
| `lsu` | 列用户 | USER | 无参数，响应：`$<count>\n <u1> <lv1> <u2> <lv2>...\n ack\n` |
| `mdu` | 改用户权限 | ADMIN | 参数：`username` `newLevel` |
| `dat` | 数据传输 | ADMIN | 子类型见 §5.1 |
| `evt` | 事件数据 | ADMIN | 参数见 §5.2 |
| `set` | 设置参数 | ADMIN | 子命令见 §5.3 |
| `rst` | 设备复位 | ADMIN | 无参数，恢复出厂并重置网络 |
| `rbt` | 重启 | ADMIN | 无参数 |
| `cal` | 自校准 | ADMIN | 无参数 |
| `adj` | 调零 | ADMIN | 参数：调零参数 |
| `stp` | 停止实时推送 | ADMIN | 无参数 |
| `rnp` | 更新固件 | ADMIN | 预留（TODO） |
| `ctl` | 自定义控制 | ADMIN | 子命令众多，见 §5.4 |
| `ste` | 设备状态 | USER | 无参数，见 §7.1 |
| `log` | 运行日志 | ADMIN | 参数见 §5.5 |
| `pmr` | 工作参数 | USER | 子命令见 §5.6 |
| `ppy` | 设备属性 | ADMIN | 无参数，见 §7.3 |
| `can` | CAN 数采控制 | ADMIN | 子命令见 §5.7 |
| `web` | Web 重启 | ADMIN | 无参数，等同 `rbt` |

### 5.1 `dat` 数据传输

- `dat+5`（参数 `5`，单参数）→ **取当前测量数据**（`GET_CURRENT_DATA`）：返回最近 5 分钟数据。payload：` HHMMSS <stationCode> <instruId> <samplingRate> 4 <ch1code> <ch2code> <ch3code> <ch4code> [<ch1> <ch2> <ch3> <ch4>]...`
- `dat+0`（参数 `0`，单参数）→ **启动实时推送**（`GET_REALTIME_DATA`）：见 §6。
- `dat+<N>+day000+day001+...+day<N-1>` → **取多日整体数据**（`GET_ALL_DATA`）：`N` 为天数，后续 `N` 个参数为 `dayNNN` 偏移（`day000`=当天，`day001`=昨天…）。每日一段，无数据日返回 `0\n`。

> 通道字段说明：`instruId = stationTestConde + manufacturerCode + stationSerialId`（如 `X122PWZK0000`）；`4` 为通道数（固定 4 通道 D/H/Z/T）；`chN` 为各通道测量值。

### 5.2 `evt` 事件数据

预留接口（当前返回 `$ack\n`）。

### 5.3 `set` 设置参数（按 `cmd.original` 子串路由）

| 子命令 | 路由条件 | 参数（单段，内部空格分字段） | 权限 |
|--------|----------|------------------------------|------|
| `set+n` | `params[0]=="n"` | `<len> <ip> <mask> <gateway> <mode=3> <httpPort> <ftpPort> <cmdPort> <mgmtIp> <mgmtPort> <sntpServer>` | ADMIN |
| `set+1n` | `params[0]=="1n"` | `<len> <mode:0/1>`（0=perSecond,1=perMinute） | ADMIN |
| `set+d` | 含 `set+d` | `<len> <stationCode> <stationTestConde> <longitude> <latitude> <elevation>` | ADMIN |
| `set+1d` | 含 `set+1d` | `<len> <stationName> <stationSerialId>` | ADMIN |
| `set+1m` | 含 `set+1m` | 通道参数：`<len> <samplingRateCode> ... 15 个通道系数` | ADMIN |
| `set+clock` | 含 `set+clock` | `<len> <year> <month> <day> <hour> <min> <sec>`（命令长度需 ≥60 字节） | ADMIN |

> `set+n` 的首字段 `<len>` 同样为整段字节长度（与请求外层 length 同算法），用于校验。

### 5.4 `ctl` 自定义控制（按 `cmd.original` 子串路由）

| 子命令 | 功能 | 响应 |
|--------|------|------|
| `ctl+dat0` | 取实时单帧数据（`handleGetRealtimeData1`） | 带数据响应 |
| `ctl+clean` | 清理历史数据，参数含周期 `3m`/`6m`/`all` | `$ack\n` |
| `ctl+getIAGA` / `ctl+setIAGA` | 读/写 IAGA 元数据（13 字段） | 带数据 / `$ack\n` |
| `ctl+getCanInfo` | 取 CAN 采样模式与采样率 | 带数据 |
| `ctl+getTimeZone` / `ctl+setTimeZone` | 读/写时区 | 带数据 / `$ack\n` |
| `ctl+getInstruId` | 取仪器 ID | 纯仪器 ID 字符串 |
| `ctl+shieldset` / `ctl+shieldget` | 读/写屏蔽通道（D/H/Z/T 布尔） | `$ack\n` / 带数据 |
| `ctl+sntp` | 立即 SNTP 对时 | `$ack\n`/`$err\n` |
| `ctl+cali` | 设备标定（预留） | `$ack\n` |
| `ctl+listsec` / `ctl+listminute` / `ctl+list10` | 列可用数据文件（秒/分/10Hz） | 带数据：` <filename> <count> <filename> <count>...` |
| `ctl+listlog` / `ctl+listServerlog` | 列操作日志文件 / 列服务端日志文件 | 带数据 |
| `ctl+downloadsec` / `ctl+downloadminute` / `ctl+download10` | 生成数据 CSV 下载链接 | **返回下载 URL 字符串**（见 §10） |
| `ctl+downloadlog` / `ctl+downServerlog` | 生成日志 CSV / 服务端日志下载链接 | 下载 URL |

### 5.5 `log` 运行日志

`log+<N>+day000+...+day<N-1>`：取 `N` 天操作日志，每日一段。每条日志字段：`<event_type> <event_type> <HHMMSS>`。

### 5.6 `pmr` 工作参数（按子串路由）

| 子命令 | 返回 |
|--------|------|
| `pmr+n` | 网络参数：` <ip> <mask> <gateway> 3 <httpPort> <ftpPort> <cmdPort> <mgmtIp> <mgmtPort> <sntpServer>` |
| `pmr+1n` | 同上 + 末尾追加 `commandOutputMode` |
| `pmr+d` | 台站属性：` <stationCode> <stationTestConde> <stationSerialId> <longitude> <latitude> <elevation>` |
| `pmr+1d` | 扩展属性：` <stationName> <stationCode> <stationTestConde> <stationSerialId> <longitude> <latitude> <elevation> <softwareVersion> <systemVersion> <webVersion>` |
| `pmr+clock` | 当前时间 `YYYYMMDDHHMMSS` |
| `pmr+m` | 通道参数：` <samplingRate> 04 8 <ch1_scale> <ch1_corr> ... <ch4_scale> <ch4_corr>` |
| `pmr+1m` | 扩展通道：` <samplingRate> <ch1_code> <ch1_scale> <ch1_corr> ... <ch4_code> <ch4_scale> <ch4_corr>` |

### 5.7 `can` CAN 数采控制

参数 `params[0]` 选择子命令：

| 子命令 | 参数 | 响应 |
|--------|------|------|
| `find` | 无 | 采样率：` <sampleRate>` |
| `continue` / `trigger` | 无 | 连续/触发采样，`$ack\n` |
| `save` | 无 | 保存配置 `$ack\n` |
| `openfliter` / `closefliter` | 无 | `$ack\n` |
| `setSampleRate` | ` <rateCode>`（×10 后下发） | `$ack\n` |
| `setTime` | ` <year> <month> <day> <hour> <min> <sec>` | `$ack\n` |
| `current` | ` <channel> <mode:0读/1写> [<currentMA> <enable>]` | 读：` <currentMA> <enable>`；写 `$ack\n` |
| `setZhengjiao` / `getZhengjiao` | 15 个正交系数 | `$ack\n` / 15 个值 |
| `setChannel` / `getChannel` | ` <channel> <type:mag\|calib\|curr-calib> <v1> <v2>` | `$ack\n` / ` <v1> <v2>` |

---

## 6. 实时数据推送

实时数据通过**命令通道（TCP 81）长连接**推送，非 WebSocket。

1. **启动**：客户端在已登录连接上发送 `dat+0` 命令（`GET_REALTIME_DATA`）。
2. 服务端识别为推送请求，返回内部标识 `$start_push\n`（**不外发**），随即：
   - 启动 `REALTIME_PUSH_INTERVAL = 1 秒` 定时器；
   - **立即下发第一帧实时数据**（不等下一秒）。
3. **后续**：每 1 秒服务端主动推送一帧 `getResponseCmd(...)` 格式数据（同 §3.2(a)）。payload：` HHMMSS <stationCode> <instruId> <samplingRate> 4 <ch1code> <ch2code> <ch3code> <ch4code> <ch1> <ch2> <ch3> <ch4>`。
4. **停止**：客户端发送 `stp` 命令（`STOP_REALTIME`）。服务端停止定时器并回 `$ack\n`。
5. **推送期间仍可收发普通命令**：服务端推送与请求处理共用同一连接，客户端请求的响应会穿插在推送帧之间。客户端解析时应**按帧边界（`$` 起始 + 长度字段）拆分**，而非假设一帧一读。
6. 无数据时推送 `$err\n`。

> 注：`commandOutputMode = perMinute`（`set+1n` 设 1）影响命令输出频率，但实时推送定时间隔代码常量固定 1 秒。

---

## 7. 关键响应字段说明

### 7.1 `ste` 设备状态 payload

```
 <YYYYMMDDHHMMSS> <clockStatus> <DeviceZero> <dcPower> <acPower> <selfCaliStatus> <zeroAdjStatus> <eventNums> <abnAlertStatus> <cardStatus> <diskUsage%>
```

- `clockStatus`：时钟状态（`DeviceStatus.json`，`1`=正常）。
- `cardStatus`：`00`=找到 CAN 卡，`01`=未找到。
- `diskUsage%`：根分区已用百分比，2 位小数。
- 其余状态字段语义见 `DeviceStatus.json`（`dcPower`/`acPower`/`selfCaliStatus`/`zeroAdjStatus`/`eventNums`/`abnAlertStatus`）。

### 7.2 数据通道编码

`channel.chN_measureCode`（如 `3125`/`3124`/`3123`/`3129`）对应 D/H/Z/T 四个测量分量；`samplingRate` 为采样率代码（见 `samplingRateToHzMap`，如 `07`→`20Hz`，`04`→`10Hz`）。

### 7.3 `ppy` 设备属性 payload

```
 <deviceName> <deviceModel> <manufacturer> <manuMailingAddress> <manuDate> <contactTel> <softwareVersion>
```

取自 `propertyParam.json`（如设备名 `EP-IV型IP采集控制器`、型号 `EP-IV`）。

---

## 8. HTTP 通道（端口 8080）

基于 Boost.Beast 的 HTTP/1.1，`HttpSession` 路由顺序：

1. **WebSocket 升级**：`Upgrade` 请求且 `target == /ws` → 移交给 `WebSocketSession`（见 §9）。
2. **下载**：`target` 以 `/download/` 开头 → 取令牌，`DownloadManager::resolveToken` → 返回文件/内存数据/受保护日志。令牌无效或过期返回 404。
3. **静态文件**：其余按 `web_root/<target>` 提供静态资源，`/` → `index.html`。含路径穿越防护（`..` 拦截 → 403），MIME 按扩展名识别。

HTTP 响应头：`Server: MyEmbeddedServer`，支持 keep-alive，30 秒读超时。下载响应带 `Content-Disposition: attachment; filename="..."`。

Windows 上位机若不使用 Web 界面，通常**仅使用 `/download/<token>`**（见 §10）。

---

## 9. WebSocket 通道（端口 8080，`/ws`）

- 握手：标准 HTTP Upgrade，路径 `/ws`。装饰器添加 `Server: MyEmbeddedServer`。
- 握手后 30 秒层超时转为 `idle=none, 3 分钟`空闲超时，禁用内置 ping。
- **文本帧**，命令格式与命令通道完全相同（§3.1 请求帧 → §3.2 响应帧），复用 `CommandParser`。
- 专用控制消息（`WebSocketSession::on_read`）：
  - `PING` → 回 `PONG`（应用层心跳，区别于 WS 协议 ping）。
  - `HELLO` → 回 `WELCOME`。
- 异常：`ERROR: <msg>`。
- Web 客户端心跳间隔 120s，断线重连 5s/次，最多 10 次（`web_root/config.js`）。

---

## 10. 文件下载流程

下载采用**令牌制两步下载**，避免在命令通道传输大二进制：

1. 客户端在命令通道发送下载命令，例如 `ctl+downloadsec+<len> <filename>`。
2. 服务端生成 CSV（内存数据）或定位日志文件，注册到 `DownloadManager` 得到**一次性令牌**，返回响应为**纯 URL 字符串**（非 `$..` 帧格式）：
   ```
   http://<deviceIp>:<httpPort>/download/<token>
   ```
   - `deviceIp` 已去前导零；`httpPort` = `serverPorts.http`。
3. 客户端用 HTTP GET 访问该 URL，得到 `text/csv` 文件（带 `Content-Disposition`）。
4. 令牌绑定到登录会话，**一次性、可过期**；服务端对临时文件下载后自动删除。

下载文件命名规则：`<stationCode><stationTestConde><YYYYMMDD><sec|min|010|log>.csv`。

---

## 11. Windows 客户端集成建议

### 11.1 推荐技术栈与最小调用流

- **TCP 命令通道**：`TcpClient`（C#）/ `boost::asio`（C++）/ `socket`（Python）。
  1. 连接 `<板IP>:81`。
  2. 发 `lin` 登录，校验响应以 `$ack\n` 开头并取出仪器 ID。
  3. 按需发命令，按帧解析响应。
  4. 实时数据：发 `dat+0` → 循环读帧 → 发 `stp` 停止。
  5. 保持心跳：300 秒内至少一次通信，否则被服务端断开。

### 11.2 帧解析要点

- 响应首字节 `$`：若紧跟 `ack\n` 或 `err\n` 即简单响应。
- 否则 `$<L>\n`：解析 `L`，再精确读 `L` 字节（前 `digits(L)` 位是重复的长度，剩余为 payload），然后期待 `\nack\n`。
- **实时推送期间**多帧可能合并到达，务必按 `$` + 长字段定帧，循环解析缓冲区。
- 下载命令的响应是**裸 URL 字符串**（不含 `$`），需特殊处理：若响应不以 `$` 开头，视为下载 URL。

### 11.3 示例请求构造（C# 伪码）

```csharp
string BuildRequest(string deviceId, string cmd, params string[] parms) {
    string inner = deviceId + "+" + cmd;
    foreach (var p in parms) inner += "+" + p;
    // 求 L: L = inner.Length + 1 + digits(L)
    int L = inner.Length + 2;
    while (L != inner.Length + 1 + L.ToString().Length)
        L = inner.Length + 1 + L.ToString().Length;
    string content = L + "+" + inner;
    return $"GET /{content} /http/1.1";
}
// 登录：BuildRequest("X122PWZK0000","lin","admin","admin")
// => "GET /31+X122PWZK0000+lin+admin+admin /http/1.1"
```

### 11.4 注意事项

- **编码**：全部 ASCII/UTF-8。CSV 含 BOM（`\xEF\xBB\xBF`）。
- **时区**：数据时间戳基于 PPS 高精度时钟，实时数据用 UTC `HHMMSS`；状态/日志的本地时间用板时区（`timeZone`，默认 `UTC+8`）。
- **网络变更**：`set+n`/`rst` 会改板 IP，连接会断，需重新发现/连接。
- **缓冲**：单条请求 ≤ 1500 字节；下载走 HTTP。
- **安全**：口令明文，建议部署在专用内网或叠加 VPN/TLS。

---

## 12. 速查：完整请求示例

下表长度已按 §3.3 算法精确计算（`deviceId = X122PWZK0000`）：

| 场景 | content | 请求 |
|------|---------|------|
| 登录 | `31+X122PWZK0000+lin+admin+admin` | `GET /31+X122PWZK0000+lin+admin+admin /http/1.1` |
| 取状态 | `19+X122PWZK0000+ste` | `GET /19+X122PWZK0000+ste /http/1.1` |
| 取当前数据 | `21+X122PWZK0000+dat+5` | `GET /21+X122PWZK0000+dat+5 /http/1.1` |
| 启动实时推送 | `21+X122PWZK0000+dat+0` | `GET /21+X122PWZK0000+dat+0 /http/1.1` |
| 停止实时推送 | `19+X122PWZK0000+stp` | `GET /19+X122PWZK0000+stp /http/1.1` |
| 取属性 | `19+X122PWZK0000+ppy` | `GET /19+X122PWZK0000+ppy /http/1.1` |
| 取网络参数 | `21+X122PWZK0000+pmr+n` | `GET /21+X122PWZK0000+pmr+n /http/1.1` |
| 重启 | `19+X122PWZK0000+rbt` | `GET /19+X122PWZK0000+rbt /http/1.1` |

**验证**：`ste` 的 inner = `X122PWZK0000+ste`（12+1+3=16 字节），`L = 16+1+digits(L) = 17+digits`，解得 `L=19`（2 位，17+2=19），故 content = `19+X122PWZK0000+ste` 共 2+1+16=19 字节 ✓。

---

*文档生成依据：eq_precursors 源码（见文首引用）。如协议随代码演进，以源码 `CommandParser.cpp` / `TcpSession.cpp` 为准。*
