# 实施计划文档（三项功能）

**最后更新：** 2026-03-26
**适用项目：** MagnetometerSystem（WPF + .NET 8 / SQLite + Dapper / ScottPlot / MVVM）

## 目录
1. [项目概述](#1-项目概述)
2. [需求详细说明](#2-需求详细说明)
3. [技术架构变更](#3-技术架构变更)
4. [分阶段实施步骤（Phase 1-4）](#4-分阶段实施步骤phase-1-4)
5. [文件修改清单](#5-文件修改清单)
6. [代码示例](#6-代码示例)
7. [测试策略](#7-测试策略)
8. [风险评估与缓解措施](#8-风险评估与缓解措施)
9. [复杂度估算](#9-复杂度估算)
10. [实施顺序建议](#10-实施顺序建议)

---

## 1. 项目概述
本实施计划覆盖三项功能需求：
- **曲线界面多图表增强**：增加计算通道、布局调整、排序与视图切换能力。
- **配置界面滚动条**：提升配置界面可用性。
- **正交度计算结果持久化**：实现校正结果存储、应用与批量修正。

目标是以最小侵入方式扩展现有 MVVM 架构，在不影响实时性能的前提下增强可视化、可配置性与数据一致性。

---

## 2. 需求详细说明
### 2.1 需求 1：曲线界面多图表增强
- 支持显示计算通道（总场、梯度）。
- 可调整图表高度。
- 支持单列/双列视图切换。
- 支持通道拖拽排序。
- 复杂度：高（预计 5-6 小时）。

### 2.2 需求 2：配置界面滚动条
- 在配置界面内容外层添加 ScrollViewer。
- 复杂度：极低（预计 0.5-1 小时）。

### 2.3 需求 3：正交度计算结果持久化
- 计算结果存储到数据库。
- 应用到实时数据计算。
- 支持历史数据批量修正。
- 支持配置导出。
- 复杂度：中（预计 5-6 小时）。

---

## 3. 技术架构变更
### 3.1 新增或扩展模块
- **数据库层**：新增正交度校正记录表与迁移脚本。
- **领域模型**：扩展 OrthogonalityParams，新增 OrthogonalityCalibrationRecord。
- **服务层**：新增 DataCorrectionService、CalculatedChannelService。
- **UI 层**：增强 RealtimeChartView 与 OrthogonalityCalibrationView。

### 3.2 数据流变化
1. 正交度校正完成后持久化校正矩阵。
2. 实时数据计算从数据库加载最新矩阵。
3. 历史数据批量修正通过 DataCorrectionService 执行。
4. 计算通道服务生成衍生曲线并注入图表渲染。

---

## 4. 分阶段实施步骤（Phase 1-4）

### Phase 1：数据库与基础设施（需求 3 基础）
**目标：** 完成数据持久化基础设施。
**步骤：**
1. 创建数据库迁移脚本 `V5_AddOrthogonalityTables.sql`。
2. 扩展 `OrthogonalityParams` 模型，支持序列化矩阵。
3. 新建 `OrthogonalityCalibrationRecord` 实体模型。
4. 扩展 `SqliteCalibrationRepository` 增加保存/查询能力。

**交付物：** 数据库脚本、模型扩展、仓储接口。

### Phase 2：正交度功能完善（需求 3）
**目标：** 校正结果可持久化并应用到实时/历史数据。
**步骤：**
1. 修改 `OrthogonalityCorrector` 在计算后写入仓储。
2. 创建 `DataCorrectionService` 提供批量修正。
3. 更新 `OrthogonalityCalibrationViewModel` 支持加载/保存。
4. 更新 `OrthogonalityCalibrationView` 添加保存、应用按钮与状态展示。
5. 支持配置导出（导出 JSON 或 CSV）。

**交付物：** 服务、ViewModel、UI 更新与导出能力。

### Phase 3：配置界面滚动条（需求 2）
**目标：** 配置界面可滚动，避免内容截断。
**步骤：**
1. 定位协议配置相关 View（配置页面主容器）。
2. 使用 `ScrollViewer` 包裹配置内容区域。
3. 验证滚动条行为与样式一致性。

**交付物：** View XAML 更新。

### Phase 4：多图表界面增强（需求 1）
**目标：** 支持通道扩展、布局调整与排序。
**步骤：**
1. 创建 `ChartChannelConfig` 模型（通道名称、类型、可见性、顺序）。
2. 创建 `CalculatedChannelService` 生成总场与梯度通道。
3. 扩展 `RealtimeChartViewModel` 引入通道配置集合与排序逻辑。
4. 创建 `ColumnCountToWidthConverter` 根据列数动态设置宽度。
5. 重构 `RealtimeChartView` 布局支持单列/双列切换。
6. 实现通道拖拽排序（ItemsControl + DragDrop 行为）。

**交付物：** 新模型/服务、ViewModel 与 View 重构。

---

## 5. 文件修改清单
| 类型 | 文件路径（计划） | 说明 |
|---|---|---|
| SQL | `src/Database/Migrations/V5_AddOrthogonalityTables.sql` | 新增正交度校正表 |
| Model | `src/Domain/Calibration/OrthogonalityParams.cs` | 扩展矩阵序列化字段 |
| Model | `src/Domain/Calibration/OrthogonalityCalibrationRecord.cs` | 新增校正记录模型 |
| Repository | `src/Infrastructure/Sqlite/SqliteCalibrationRepository.cs` | 新增保存/查询接口 |
| Service | `src/Services/DataCorrectionService.cs` | 新增历史修正服务 |
| Service | `src/Services/CalculatedChannelService.cs` | 新增计算通道服务 |
| ViewModel | `src/Presentation/ViewModels/OrthogonalityCalibrationViewModel.cs` | 保存/应用校正 |
| View | `src/Presentation/Views/OrthogonalityCalibrationView.xaml` | UI 交互更新 |
| ViewModel | `src/Presentation/ViewModels/RealtimeChartViewModel.cs` | 通道配置、排序 |
| Converter | `src/Presentation/Converters/ColumnCountToWidthConverter.cs` | 列宽换算 |
| View | `src/Presentation/Views/RealtimeChartView.xaml` | 布局与拖拽 |
| View | `src/Presentation/Views/ProtocolConfigView.xaml` | ScrollViewer |

说明：实际路径以项目结构为准，实施时需核对文件是否已存在或需新增。

---

## 6. 代码示例

### 6.1 SQL 脚本示例（V5_AddOrthogonalityTables.sql）
```sql
CREATE TABLE IF NOT EXISTS OrthogonalityCalibrationRecord (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    DeviceId TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    MatrixJson TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    Operator TEXT NULL,
    Notes TEXT NULL
);

CREATE INDEX IF NOT EXISTS IDX_OrthogonalityCalibrationRecord_DeviceId
ON OrthogonalityCalibrationRecord(DeviceId);
```

### 6.2 C# 模型示例（OrthogonalityCalibrationRecord）
```csharp
public sealed class OrthogonalityCalibrationRecord
{
    public long Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string MatrixJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? Operator { get; set; }
    public string? Notes { get; set; }
}
```

### 6.3 C# 服务示例（DataCorrectionService）
```csharp
public sealed class DataCorrectionService
{
    private readonly ICalibrationRepository _repository;

    public DataCorrectionService(ICalibrationRepository repository)
    {
        _repository = repository;
    }

    public async Task ApplyLatestOrthogonalityAsync(string deviceId, IEnumerable<RawSample> samples)
    {
        var record = await _repository.GetLatestOrthogonalityAsync(deviceId);
        if (record is null)
        {
            return;
        }

        var matrix = OrthogonalityMatrix.FromJson(record.MatrixJson);
        foreach (var sample in samples)
        {
            sample.ApplyOrthogonality(matrix);
        }
    }
}
```

### 6.4 XAML 示例（配置界面 ScrollViewer）
```xml
<ScrollViewer VerticalScrollBarVisibility="Auto"
              HorizontalScrollBarVisibility="Disabled">
    <StackPanel Margin="12">
        <!-- 原有配置内容 -->
    </StackPanel>
</ScrollViewer>
```

### 6.5 XAML 示例（多图表双列布局片段）
```xml
<Grid>
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="{Binding ColumnCount, Converter={StaticResource ColumnCountToWidthConverter}, ConverterParameter=0}" />
        <ColumnDefinition Width="{Binding ColumnCount, Converter={StaticResource ColumnCountToWidthConverter}, ConverterParameter=1}" />
    </Grid.ColumnDefinitions>

    <ItemsControl ItemsSource="{Binding ChartChannels}">
        <ItemsControl.ItemsPanel>
            <ItemsPanelTemplate>
                <UniformGrid Columns="{Binding ColumnCount}" />
            </ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
    </ItemsControl>
</Grid>
```

---

## 7. 测试策略
| 类型 | 范围 | 目标 | 方法 |
|---|---|---|---|
| 单元测试 | OrthogonalityMatrix 序列化 | 确保保存/读取一致 | JSON Round-trip 测试 |
| 单元测试 | DataCorrectionService | 校正应用正确 | 构造样本并断言值 |
| 集成测试 | SqliteCalibrationRepository | CRUD 正常 | 使用测试数据库 |
| UI 测试 | RealtimeChartView | 单列/双列切换 | 手工测试 + 自动化截图 |
| 回归测试 | 实时数据流 | 性能与稳定性 | 压力测试与观察帧率 |

---

## 8. 风险评估与缓解措施
| 风险级别 | 风险 | 影响 | 缓解措施 |
|---|---|---|---|
| 高 | ScottPlot 多图表性能瓶颈 | UI 卡顿、帧率下降 | 限制同时显示通道数，采样降频 |
| 高 | 历史数据批量修正事务处理 | 事务超时、数据不一致 | 分批处理、事务分段提交 |
| 中 | 正交度矩阵序列化 | 保存/读取误差 | 统一精度格式、加入版本字段 |
| 中 | 通道配置持久化 | 配置丢失或不一致 | 在 ViewModel 初始化时回退默认配置 |

---

## 9. 复杂度估算
| 需求 | 复杂度 | 估时 | 说明 |
|---|---|---|---|
| 需求 1：多图表增强 | 高 | 5-6 小时 | 视图重构 + 拖拽排序 |
| 需求 2：配置滚动条 | 极低 | 0.5-1 小时 | 纯 XAML 调整 |
| 需求 3：正交度持久化 | 中 | 5-6 小时 | 数据库 + 服务 + UI |

---

## 10. 实施顺序建议
1. **Phase 1**：数据库与模型基础设施（确保存储结构先行）。
2. **Phase 2**：正交度持久化与应用（依赖 Phase 1）。
3. **Phase 3**：配置界面滚动条（低风险快速交付）。
4. **Phase 4**：多图表增强（高风险、投入最大，应最后执行）。

实施顺序的原则：先落地基础设施，再实现业务逻辑，最后处理高复杂度 UI 重构。
