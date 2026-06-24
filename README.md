# MagnetometerSystem

磁力计数据采集与分析系统

## 项目简介

MagnetometerSystem 是一个专业的磁力计数据采集与分析 WPF 桌面应用程序，支持多种磁传感器（三轴磁通门、单轴磁通门、双三轴磁通门、质子磁力计）的实时数据采集、显示、正交度校准和历史数据回放功能。

## 主要功能

### 1. 实时数据采集
- 支持串口和 TCP 网络连接
- 多种协议解析（ASCII CSV、二进制帧、可配置协议）
- 协议配置保存与导入
- 实时原始数据显示

### 2. 实时图表显示
- 单图表/多图表模式切换
- 支持 8 通道数据显示
- 计算通道（总场强、梯度计算）
- 数据滤波（移动平均、中值滤波）
- 统计信息显示
- 区间选取与统计

### 3. 正交度校准
- 两种校准模式：连续采集和 48 点手动采集
- 数据验证与质量评估
- 椭球拟合法正交度计算
- 校准参数保存与管理
- 实时数据可视化

### 4. 历史数据回放
- 会话管理与搜索
- 批量正交度校正
- 多种速度回放控制
- 数据导出（CSV 格式）

### 5. 设备命令
- 命令目录管理
- ASCII 和二进制帧构建
- 参数化命令发送
- 通信日志记录

### 6. 设置
- 默认连接参数配置
- 数据存储路径配置
- 图表刷新率设置
- 主题选择

## 技术架构

### 项目结构
```
src/
├── MagnetometerSystem.App/           # WPF 应用程序
│   ├── ViewModels/                   # 视图模型
│   ├── Views/                      # 视图
│   ├── Converters/                 # 值转换器
│   ├── Behaviors/                  # 交互行为
│   └── Helpers/                     # 辅助类
├── MagnetometerSystem.Core/         # 核心库
│   ├── Calibration/                 # 校准算法
│   ├── Communication/              # 通信模块
│   ├── Helpers/                    # 辅助工具
│   ├── Models/                     # 数据模型
│   ├── Processing/                 # 数据处理
│   ├── Protocol/                   # 协议解析
│   ├── Sensors/                    # 传感器适配器
│   └── Services/                   # 核心服务
└── MagnetometerSystem.Infrastructure/# 基础设施
    ├── Configuration/               # 配置服务
    ├── Database/                    # 数据库
    ├── Export/                      # 数据导出
    └── Services/                   # 基础设施服务
```

### 技术栈
- **框架**: .NET 8 + WPF
- **MVVM**: CommunityToolkit.Mvvm
- **图表**: ScottPlot
- **数据库**: SQLite
- **依赖注入**: Microsoft.Extensions.DependencyInjection

## 环境要求

- .NET 8.0 SDK
- Windows 10/11

## 构建与运行

### 构建
```bash
dotnet build MagnetometerSystem.sln
```

### 运行
```bash
dotnet run --project src/MagnetometerSystem.App/MagnetometerSystem.App.csproj
```

## 使用说明

### 连接传感器
1. 选择连接类型（串口/TCP）
2. 配置连接参数（波特率、IP 地址等）
3. 选择传感器类型和采样率
4. 配置协议（可选）
5. 点击连接按钮

### 正交度校准流程
1. 进入"正交度校准"界面
2. 选择传感器类型和校准模式
3. 开始采集数据
4. 采集足够数据点后停止
5. 运行计算
6. 保存校准参数

### 数据记录
1. 进入"会话列表"界面
2. 开始新会话
3. 选择正交度校正文件（可选）
4. 系统自动记录数据到数据库

## 测试

```bash
dotnet test MagnetometerSystem.sln
```

## 项目版本

当前版本信息请查看 `src/MagnetometerSystem.App/AppVersion.cs`

## 目录结构说明

- `docs/` - 项目文档
- `tests/` - 单元测试和集成测试
- `.claude/` - Claude AI 助手配置