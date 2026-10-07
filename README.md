# MagnetometerSystem

[English](README.en.md) · [下载 V0.5.2](https://github.com/YIALU/MagnetometerSystem/releases/tag/v0.5.2) · [使用说明](docs/使用说明.md) · [更新记录](docs/变更日志.md)

**MagnetometerSystem 是面向日常调试与实验记录的通用磁力仪上位机。** 通过串口或 TCP 连接设备，按用户定义的通信协议接收数据，自动保存原始读数，并实时绘制曲线。

项目以**协议可定制、数据可保存、曲线可观察**为核心。通道数量、名称和单位由协议配置，便于接入不同的数据格式，也可以同时观察磁场、温度及其他辅助通道。数据改正和高级分析作为可选工具，按需要使用。

## 主要功能

| 功能 | 说明 |
| --- | --- |
| **设备连接** | 支持串口与 TCP，配置连接参数；顶部“数据链路”条在任何页面显示 接收 → 解析 → 保存 三段计数与实测频率 |
| **报文诊断** | 「原始报文」逐帧列出解析结果：通过、被拒绝的原因（如校验计算值与帧内值）、重新同步丢弃的字节；「解析测试」不连接设备即可用当前协议试解析一段数据 |
| **通信协议定制** | 配置 ASCII 或二进制协议，包括字段映射、帧结构、字节序、缩放、校验和通道单位；支持协议 JSON 导入、导出 |
| **自动数据记录** | 连接后按会话保存原始读数，查看接收量、已保存量及保存状态 |
| **实时曲线** | 单图叠加或多图显示，多图支持单列、双列布局；单图中的温度等异单位通道使用独立坐标轴 |
| **采集页** | 曲线 + 右侧通道 / 统计 / 区间 / 滤波 / 校正面板 + 底部收发 / 原始报文 / 事件；可折叠或专注曲线，退出后恢复布局 |
| **通道与计算** | 设置通道显示、颜色、顺序和显示偏移；添加总场、梯度及公式计算通道 |
| **统计与分析** | 滚动统计；在曲线上拖动选取区间并计算统计，悬停显示原始值；移动平均、中值等显示滤波；「分析」页对已保存会话按通道和时间段计算噪声、漂移与数据质量 |
| **设备命令** | 管理协议关联的命令组，填写参数发送命令，支持 ASCII、HEX；收发记录逐行写明方向、字节数、应答判定与耗时 |
| **数据页** | 会话列表与详情、回放（按记录时间戳，支持倍速、暂停和定位；连接期间禁用）、按通道导出 CSV |
| **CSV 导出** | 按时间与通道选择数据，设置导出精度，导出原始值或已保存的改正结果 |
| **数据改正** | 「校正」页：正交度四步向导（实时连续 / 手动 48 点、导入文件、已保存会话）与配置库、偏移/增益参数管理；按需用于实时与历史数据改正 |
| **反馈与建议** | 无需登录，填写使用场景和问题/需求即可提交；姓名、联系方式选填，仅供维护者查看 |

## 下载与运行

支持 **Windows 10/11 x64**。发布包自带 .NET 8 运行时，解压或安装后即可使用。

| 下载 | 使用方式 |
| --- | --- |
| [V0.5.2 安装版](https://github.com/YIALU/MagnetometerSystem/releases/download/v0.5.2/MagnetometerSystem-v0.5.2-setup.exe) | 运行安装程序后启动应用 |
| [V0.5.2 便携版](https://github.com/YIALU/MagnetometerSystem/releases/download/v0.5.2/MagnetometerSystem-v0.5.2-portable-win-x64.zip) | 解压后运行 `MagnetometerSystem.App.exe` |
| [SHA256 校验清单](https://github.com/YIALU/MagnetometerSystem/releases/download/v0.5.2/SHA256SUMS.txt) | 校验下载的安装包或便携包 |

版本变化见[更新记录](docs/变更日志.md)。当前版本为 **V0.5.2**，支持 GitHub / Gitee 双平台更新，在「系统设置 → 软件更新」中选择自动或指定平台；自动模式比较两边版本，并支持同版本镜像下载。发现新版本只会提示，点击“下载更新”后才开始下载和安装。

软件提供匿名反馈入口和磁铁波形图标。反馈服务已部署，匿名提交到 GitHub 待审核 Issue 的完整链路已验证；部署与维护见[反馈服务说明](docs/feedback-deployment.md)。

## 快速使用

1. **配置连接**：选择串口或 TCP，填写连接参数。
2. **配置协议**：选择预设或编辑自定义协议，确认字段、通道名称与单位。
3. **连接并采集**：填写标称采样率，连接设备，检查接收数据和保存状态。标称采样率用于记录；调整设备输出频率时，使用设备支持的命令或设置。
4. **观察与分析**：在「采集」页选择显示通道和时间窗口，切换单图、多图，按需展开右侧面板；底部「原始报文」可查看每一帧的解析结论。暂停曲线显示、折叠面板或切换页面时，原始数据仍持续保存。
5. **回放与导出**：停止后等待尾批保存完成（「事件」中出现“会话已结束，尾批已提交”），在「数据」页选择会话，回放曲线或导出 CSV；长时段的噪声与漂移在「分析」页查看。

协议配置、命令响应、校正使用与常见问题见[使用说明](docs/使用说明.md)。

## 源码开发

使用 **.NET 8、WPF、CommunityToolkit.Mvvm、ScottPlot 和 SQLite**。源码构建需要 Windows 与 .NET 8 SDK；使用 Visual Studio 时安装“.NET 桌面开发”工作负载。

在仓库根目录运行：

```powershell
dotnet restore MagnetometerSystem.sln
dotnet build MagnetometerSystem.sln -c Debug
dotnet run --project src/MagnetometerSystem.App/MagnetometerSystem.App.csproj
dotnet test MagnetometerSystem.sln -c Debug --no-build -m:1
```

`src/MagnetometerSystem.App` 为桌面界面，`Core` 为协议、通信与计算，`Infrastructure` 为数据库、配置与导出；测试位于 `tests`。

## 项目文档

- [使用说明](docs/使用说明.md)：配置、数据管理、校正及问题排查。
- [协议校验与通道单位](docs/协议校验与单位.md)：校验设置、预设协议与单位说明。
- [测试与验收](docs/testing-and-acceptance.md)：测试方法、验证记录与设备验收。
- [发布流程](docs/发布流程.md)：版本管理与打包。
- [贡献指南](CONTRIBUTING.md)：提交、PR 与审查流程。
- [AGENTS.md](AGENTS.md)：Agent 工程入口与开发约束。
