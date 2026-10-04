# MagnetometerSystem

[English](README.en.md) · [下载 V0.5.0](https://github.com/YIALU/MagnetometerSystem/releases/tag/v0.5.0) · [使用说明](docs/使用说明.md) · [更新记录](docs/变更日志.md)

**MagnetometerSystem 是面向日常调试与实验记录的通用磁力仪上位机。** 通过串口或 TCP 连接设备，按用户定义的通信协议接收数据，自动保存原始读数，并实时绘制曲线。

项目以**协议可定制、数据可保存、曲线可观察**为核心。通道数量、名称和单位由协议配置，便于接入不同的数据格式，也可以同时观察磁场、温度及其他辅助通道。数据改正和高级分析作为可选工具，按需要使用。

## 主要功能

| 功能 | 说明 |
| --- | --- |
| **设备连接** | 支持串口与 TCP，配置连接参数，查看连接状态和原始收发报文 |
| **通信协议定制** | 配置 ASCII 或二进制协议，包括字段映射、帧结构、字节序、缩放、校验和通道单位；支持协议 JSON 导入、导出 |
| **自动数据记录** | 连接后按会话保存原始读数，查看接收量、已保存量及保存状态 |
| **实时曲线** | 单图叠加或多图显示，多图支持单列、双列布局；单图中的温度等异单位通道使用独立坐标轴 |
| **曲线工作台** | 按需折叠连接、通道、收发及分析面板，支持专注曲线与布局恢复 |
| **通道与计算** | 设置通道显示、颜色、顺序和显示偏移；添加总场、梯度及公式计算通道 |
| **统计与分析** | 滚动统计、区间选取与分析，以及移动平均、中值等显示滤波 |
| **设备命令** | 管理协议关联的命令组，填写参数发送命令，支持 ASCII、HEX 和通信日志 |
| **历史回放** | 查询已保存会话，按记录时间戳播放，支持倍速、暂停和定位 |
| **CSV 导出** | 按时间与通道选择数据，设置导出精度，导出原始值或已保存的改正结果 |
| **数据改正** | 按需使用偏移/增益校准、正交度采集与参数管理，以及实时、历史数据改正 |

## 下载与运行

支持 **Windows 10/11 x64**。发布包自带 .NET 8 运行时，解压或安装后即可使用。

| 下载 | 使用方式 |
| --- | --- |
| [V0.5.0 安装版](https://github.com/YIALU/MagnetometerSystem/releases/download/v0.5.0/MagnetometerSystem-v0.5.0-setup.exe) | 运行安装程序后启动应用 |
| [V0.5.0 便携版](https://github.com/YIALU/MagnetometerSystem/releases/download/v0.5.0/MagnetometerSystem-v0.5.0-portable-win-x64.zip) | 解压后运行 `MagnetometerSystem.App.exe` |
| [SHA256 校验清单](https://github.com/YIALU/MagnetometerSystem/releases/download/v0.5.0/SHA256SUMS.txt) | 校验下载的安装包或便携包 |

版本变化见[更新记录](docs/变更日志.md)。应用内更新检查使用 Gitee 发布源；GitHub 发布包可通过上面的链接手动下载。

## 快速使用

1. **配置连接**：选择串口或 TCP，填写连接参数。
2. **配置协议**：选择预设或编辑自定义协议，确认字段、通道名称与单位。
3. **连接并采集**：填写标称采样率，连接设备，检查接收数据和保存状态。标称采样率用于记录；调整设备输出频率时，使用设备支持的命令或设置。
4. **观察与分析**：选择显示通道和时间窗口，切换单图、多图，按需展开统计或分析面板。暂停曲线显示时，原始数据仍持续保存。
5. **回放与导出**：断开后等待保存完成，在数据管理中选择会话，回放曲线或导出 CSV。

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
