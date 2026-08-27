# 应用内反馈提交 — 设计规格

- 日期：2026-08-27
- 版本：v0.5.0 目标功能
- 状态：待评审

## 1. 目标

让用户在桌面应用内直接提交 Bug、需求或其它反馈，一键创建一条 Gitee Issue，无需打开浏览器或拥有 Gitee 账号。反馈在断网时本地保存、下次启动自动重试，不丢失用户已录入的内容。

## 2. 范围

### 做什么

- 反馈对话框：类型（Bug / 需求 / 其他）、标题、正文，提交到维护者 Gitee 仓库的 Issue。
- 自动附带最小环境信息：版本号、分发形态（安装版/便携版）、操作系统、.NET Runtime、提交时间（UTC）。
- 离线重试：提交失败时写入本地 SQLite outbox 表，下次启动后台自动重试。
- 节流：每台机器每 24 小时最多 5 次提交。
- 两个入口：MainWindow「帮助 → 提交反馈」菜单项，与 AboutDialog「反馈」按钮。

### 不做什么（YAGNI）

- 不带 Gitee 标签 / 指派人（标签需预先在仓库创建；不存在的标签会让 Gitee 拒绝创建 Issue。v1 不设标签）。
- 不带截图 / 文件附件（Gitee Issue 附件需单独的上传 API，超出 v1 范围）。
- 不附运行日志或配置快照（用户已选最小环境信息）。
- 不实现 Serverless 代理转发（仅作为令牌泄露后的升级路径在文档中记录）。

## 3. 架构

镜像现有 Update 子系统的分层：Core 放接口与纯数据模型，Infrastructure 放 Gitee 实现，App 放协调者、ViewModel 与对话框。这是与 `GiteeUpdateService` / `UpdateCoordinator` 完全一致的结构，便于复用既有模式与测试方法。

### 3.1 Core 层 — `MagnetometerSystem.Core`

**`Services/FeedbackModels.cs`**

```csharp
public enum FeedbackType { Bug, Feature, Other }

public enum FeedbackStatus { Submitted, Failed, Throttled }

public sealed record FeedbackSubmission(FeedbackType Type, string Title, string Body);

public sealed record FeedbackResult(
    FeedbackStatus Status,
    int? IssueNumber = null,
    string? HtmlUrl = null,
    string? ErrorMessage = null,
    bool Retryable = false)
{
    public static FeedbackResult Submitted(int number, string htmlUrl) =>
        new(FeedbackStatus.Submitted, number, htmlUrl);
    public static FeedbackResult Failed(string message, bool retryable = true) =>
        new(FeedbackStatus.Failed, null, null, message, retryable);
    public static FeedbackResult Throttled() =>
        new(FeedbackStatus.Throttled);
}
```

**`Services/FeedbackOptions.cs`** — 与 `UpdateOptions` 对称：

- `Owner` = `"yialu"`，`Repo` = `"MagnetometerSystem"`（与 `UpdateOptions` 默认值一致）。
- `BotToken` — 由 App 在 DI 注册时填入（来源是构建期生成的 `FeedbackCredentials`）。空字符串表示该构建未启用反馈。
- `CurrentVersion`（string，如 `"0.4.0"`）与 `PackageKind`（`AppPackageKind`，Core 已有的枚举，`IUpdateService` 里定义）——由 App 从 `AppVersion.Number` / `AppVersion.PackageKind` 填入，供 Infrastructure 层组装环境信息块。Infrastructure 不能引用 App 层，故必须随 Options 传入，与 `UpdateOptions` 同一处理方式。
- `HomepageUrl` / `IssuesUrl` 派生：`https://gitee.com/{Owner}/{Repo}` 与 `/issues`。
- `DailySubmitLimit` = 5。

**`Services/IFeedbackService.cs`**

```csharp
public interface IFeedbackService
{
    FeedbackOptions Options { get; }
    Task<FeedbackResult> SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default);
    Task<int> RetryPendingAsync(CancellationToken ct = default); // 返回本次成功推送的条数
}
```

### 3.2 Infrastructure 层 — `MagnetometerSystem.Infrastructure.Feedback`

**`FeedbackCredentials.cs`**（构建期生成，`.gitignore` 忽略源文件）

```csharp
internal static class FeedbackCredentials
{
    // 构建期由 build.ps1 从 $env:GITEE_FEEDBACK_TOKEN 生成，XOR+Base64 混淆。
    // 混淆不是加密，仅抬高门槛；令牌作用域仅 issues，泄露后影响面有限。
    private const string Obfuscated = ""; // 由构建脚本写入
    internal static string GetToken() => Deobfuscate(Obfuscated);
}
```

**`GiteeFeedbackService : IFeedbackService, IDisposable`**

- 构造接收 `FeedbackOptions`。持有 `HttpClient`（`Timeout = 15s`，`User-Agent: MagnetometerSystem/<version>`，`Accept: application/json`），与 `GiteeUpdateService` 一致。
- `SubmitAsync`：
  1. 若 `Options.BotToken` 为空 → 返回 `Failed("反馈功能在当前构建未启用")`。
  2. 组装 Issue 正文：用户正文在前，后接环境信息块（见 3.4）。
  3. POST `https://gitee.com/api/v5/repos/{Owner}/{Repo}/issues`，请求体为 JSON，含 `access_token`、`title`、`body`。
  4. 2xx → 解析 `number` 与 `html_url`，返回 `Submitted`。
  5. 4xx → 返回 `Failed(message, retryable: false)` 并带状态码与服务器 message（内容问题，重试无意义）。
  6. 网络异常 / 5xx / 超时 → 返回 `Failed(ex.Message, retryable: true)`（调用方据此决定是否入 outbox 重试）。
- 网络层不抛异常（除 `OperationCanceledException`），与 `CheckForUpdateAsync` 一致。
- 将 JSON 构建与响应解析拆成 `internal static`：`BuildIssueBody(FeedbackSubmission, env)` 与 `ParseResponse(string json, int statusCode)`，供单元测试固定输入覆盖，不打真实网络（同 `ParseLatestRelease`）。

**outbox 持久化**：复用 `AppConfigService` 已有的 SQLite 连接（`DatabaseInitializer.ConnectionString`）。新增薄仓储 `FeedbackOutboxRepository`（或直接在协调者用 Dapper），表结构见 3.3。

### 3.3 数据库 — `feedback_outbox` 表

在 `src/MagnetometerSystem.Infrastructure/Database/Schema.sql` 末尾追加（幂等，随每次启动执行，无需迁移）：

```sql
CREATE TABLE IF NOT EXISTS feedback_outbox (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    type            TEXT    NOT NULL,   -- Bug|Feature|Other
    title           TEXT    NOT NULL,
    body            TEXT    NOT NULL,
    created_at_utc  TEXT    NOT NULL,   -- ISO 8601
    attempts        INTEGER NOT NULL DEFAULT 0
);
```

outbox 行很小（正文 + 环境块），不存二进制。

### 3.4 Issue 正文格式

```
<用户正文>

---
### 环境信息（自动生成）
- 版本：0.4.0
- 分发形态：安装版 | 便携版
- 操作系统：Microsoft Windows 10.0.xxxxx
- .NET Runtime：<RuntimeInformation.FrameworkDescription>
- 反馈类型：Bug
- 提交时间(UTC)：2026-08-27T12:34:56Z
```

版本取 `Options.CurrentVersion`，分发形态取 `Options.PackageKind`（OS 与 .NET Runtime 用 BCL 的 `RuntimeInformation` / `Environment.OSVersion`，Infrastructure 层可直接访问）。`BuildIssueBody` 签名为 `BuildIssueBody(FeedbackSubmission submission, string currentVersion, AppPackageKind packageKind)`，由 `SubmitAsync` 从 `Options` 取值传入。

## 4. App 层 — `MagnetometerSystem.App`

### 4.1 `Services/FeedbackCoordinator.cs`

镜像 `UpdateCoordinator`。构造注入 `IFeedbackService`、`IUserPreferencesService`（Infrastructure 那个，与 `UpdateCoordinator` 同）、`DatabaseInitializer`。

职责：

- **节流**：键 `feedback.submitCount` 与 `feedback.windowStartUtc`。`SubmitAsync` 先读计数；若窗口已过 24h 则重置计数；若计数 ≥ `DailySubmitLimit` → 返回 `Throttled`，不发网络请求。提交成功（非 Failed）后计数 +1。失败与节流本身不计数。
- **outbox 入队**：`SubmitAsync` 中，当 `IFeedbackService.SubmitAsync` 返回 `Failed` 且 `Retryable == true` 时（网络/5xx/超时；`Retryable == false` 的 4xx 不入队），写入 `feedback_outbox`，并向上层返回该 `Failed`（信息改为"已本地保存，将在下次启动时自动重试"）。
- **启动重试**：`RetryPendingAsyncOnStartupAsync()` 在 `App.InitializeAsync` 中、数据库初始化之后、fire-and-forget 调用（与 `RunStartupUpdateCheckAsync` 同样的独立、失败不影响主流程模式）。遍历 outbox：逐条 `SubmitAsync`，成功则删除该行；失败则 `attempts += 1`，超过 `MaxAttempts = 5` 后停止重试该条并记 Warning 日志保留行。
- **日志**：全部用 Serilog（`Log.Information`/`Log.Warning`），失败一律记日志、不向上层抛（与 `UpdateCoordinator` 一致；但因是用户主动操作，需把结果回传给 UI 由 ViewModel 展示）。
- `ShowFeedbackDialogAsync(Window? owner)` — 构造 `FeedbackViewModel` 与 `FeedbackDialog`，设 Owner，`ShowDialog()`。

### 4.2 `ViewModels/FeedbackViewModel.cs`

- 属性：`SelectedType`（默认 Bug）、`Title`、`Body`、`IsBusy`、`StatusMessage`、`ResultHtmlUrl`、`CanSubmit`（标题与正文非空且非 IsBusy 时为 true）。
- 命令：`SubmitCommand`（async）、`CancelCommand`、`OpenIssueUrlCommand`（`Process.Start` 打开返回的 `html_url`）。
- 提交：调 `FeedbackCoordinator`，按 `FeedbackResult.Status` 更新 UI：
  - `Submitted` → 显示"已提交，Issue #N"+可点链接，清空表单。
  - `Throttled` → 显示"今日提交已达上限（5 次），请明天再试"。
  - `Failed` → 显示协调者返回的信息（含"已本地保存将重试"）。
- 全程在 UI 线程更新（`Application.Current.Dispatcher.InvokeAsync`）。

### 4.3 `Views/Dialogs/FeedbackDialog.xaml(.cs)`

模态对话框，样式对齐 `UpdateDialog`/`AboutDialog`。布局：类型 ComboBox、标题 TextBox、正文多行 TextBox（带 placeholder 提示如"请描述重现步骤或需求…"、最小高度）、提交/取消按钮、状态行（含可点链接）。代码后置只做 `InitializeComponent` 与 owner 处理，逻辑在 ViewModel。

## 5. 入口与 DI 接线

### 5.1 `App.OnStartup` DI 注册

在 `UpdateCoordinator` 注册之后追加：

```csharp
services.AddSingleton(new FeedbackOptions
{
    Owner = "yialu",
    Repo = "MagnetometerSystem",
    BotToken = MagnetometerSystem.Infrastructure.Feedback.FeedbackCredentials.GetToken(),
    CurrentVersion = AppVersion.Number,
    PackageKind = AppVersion.PackageKind,
});
services.AddSingleton<IFeedbackService, GiteeFeedbackService>();
services.AddSingleton<FeedbackCoordinator>();
```

### 5.2 启动重试挂载

在 `App.InitializeAsync` 末尾、`RunStartupUpdateCheckAsync` 旁：

```csharp
_ = RunStartupFeedbackRetryAsync();
```

`RunStartupFeedbackRetryAsync` 取 `FeedbackCoordinator`，调 `RetryPendingAsync`，结果只记日志。

### 5.3 MainWindow 菜单

在 MainWindow 的「帮助」菜单下新增 `MenuItem`「提交反馈」，Click → `Services.GetRequiredService<FeedbackCoordinator>().ShowFeedbackDialogAsync(this)`。

### 5.4 AboutDialog 按钮

在 `AboutDialog` 现有「检查更新」按钮旁加「反馈」按钮，Click → 同样调用 `FeedbackCoordinator.ShowFeedbackDialogAsync`。

## 6. 令牌与安全

- 专用 **bot 账号** 令牌，作用域仅 `issues`（创建/读取 Issue），非维护者主账号。
- 构建期混淆：`build.ps1` 读取 `$env:GITEE_FEEDBACK_TOKEN`，XOR+Base64 混淆后写入 `src/MagnetometerSystem.Infrastructure/Feedback/FeedbackCredentials.cs` 的 `Obfuscated` 常量。该文件加入 `.gitignore`。`build.ps1` 在 `Invoke-Publish` 之前生成它、打包后删除临时生成（或保留在 `.gitignore` 中不提交）。
- 开发构建（未设环境变量）→ `Obfuscated` 为空 → `GetToken()` 返回空 → 反馈功能降级为"当前构建未启用"，不报错。
- **已知风险**：混淆 ≠ 加密，逆向 exe 可提取令牌。缓解：issues-only 作用域（泄露后影响面=被刷 Issue）、每机 5 次/24h 节流、bot 账号可随时吊销。
- **升级路径（不在 v1 实现）**：若将来令牌被滥用，替换为 Serverless 代理——客户端 POST 到代理 URL，代理持有令牌转发。无需改客户端接口，仅需换 `IFeedbackService` 实现与 `FeedbackOptions` 的目标 URL。
- 仓库 owner/repo 来自 `FeedbackOptions`，不取自令牌；轮换令牌不需改代码，只需重新打包。

## 7. 错误处理与日志

- `GiteeFeedbackService`：网络层失败返回 `Failed`，不抛（除取消）。4xx 不入 outbox（内容问题重试无意义）；网络/5xx/超时入 outbox。
- `FeedbackCoordinator`：所有偏好读写与 outbox 操作 try/catch，失败记 Warning、降级处理（如读节流计数失败则按未节流放行，同 `UpdateCoordinator.IsAutoCheckEnabledAsync` 的容错思路）。
- 启动重试失败不影响主流程（fire-and-forget），与 `RunStartupUpdateCheckAsync` 一致。

## 8. 测试

`MagnetometerSystem.Infrastructure.Tests`：

- `GiteeFeedbackServiceTests`：
  - `BuildIssueBody` 含版本号、OS、时间、反馈类型、用户正文。
  - `ParseResponse` 覆盖：2xx 正常 JSON、2xx 缺 `number`、4xx、malformed JSON、空响应。
  - 不打真实网络（仅测 `internal static` 方法）。
- `FeedbackOutboxRepository`（若独立类）的基本增删改查用内存 SQLite。

`MagnetometerSystem.Core.Tests`：模型默认值与工厂方法（`FeedbackResult.Submitted/Failed/Throttled`）。

手动集成测试（实现期，不自动化）：对一个测试仓库发一次真实提交，确认 Gitee API 请求体/响应字段与设计一致。

## 9. 交付物清单

新增文件：
- `src/MagnetometerSystem.Core/Services/FeedbackModels.cs`
- `src/MagnetometerSystem.Core/Services/FeedbackOptions.cs`
- `src/MagnetometerSystem.Core/Services/IFeedbackService.cs`
- `src/MagnetometerSystem.Infrastructure/Feedback/GiteeFeedbackService.cs`
- `src/MagnetometerSystem.Infrastructure/Feedback/FeedbackOutboxRepository.cs`
- `src/MagnetometerSystem.Infrastructure/Feedback/FeedbackCredentials.cs`（构建期生成，gitignored）
- `src/MagnetometerSystem.App/Services/FeedbackCoordinator.cs`
- `src/MagnetometerSystem.App/ViewModels/FeedbackViewModel.cs`
- `src/MagnetometerSystem.App/Views/Dialogs/FeedbackDialog.xaml(.cs)`
- `tests/MagnetometerSystem.Infrastructure.Tests/Feedback/GiteeFeedbackServiceTests.cs`

修改文件：
- `src/MagnetometerSystem.Infrastructure/Database/Schema.sql` — 追加 `feedback_outbox` 表。
- `src/MagnetometerSystem.App/App.xaml.cs` — DI 注册 + 启动重试挂载。
- `src/MagnetometerSystem.App/MainWindow.xaml(.cs)` — 帮助菜单项。
- `src/MagnetometerSystem.App/Views/Dialogs/AboutDialog.xaml(.cs)` — 反馈按钮。
- `.gitignore` — 忽略生成的 `FeedbackCredentials.cs`。
- `build.ps1` — 令牌注入步骤。
