namespace MagnetometerSystem.Core.Services;

/// <summary>
/// 当前程序的分发形态。决定检查更新时下载哪种安装包、以及如何应用更新。
/// </summary>
public enum AppPackageKind
{
    /// <summary>安装版：下载 setup.exe 静默覆盖安装。</summary>
    Installer,

    /// <summary>便携版：下载 zip，由用户手动解压覆盖。</summary>
    Portable
}

/// <summary>检查更新的配置。由 App 层在注册 DI 时填入当前版本和分发形态。</summary>
public sealed class UpdateOptions
{
    /// <summary>Gitee 仓库所有者。</summary>
    public string Owner { get; init; } = "yialu";

    /// <summary>Gitee 仓库名。</summary>
    public string Repo { get; init; } = "MagnetometerSystem";

    /// <summary>当前运行的版本号，形如 "0.4.0"（不带 v 前缀、不带 git hash）。</summary>
    public required string CurrentVersion { get; init; }

    /// <summary>当前程序是安装版还是便携版。</summary>
    public required AppPackageKind PackageKind { get; init; }

    /// <summary>更新包下载目录。默认 %LOCALAPPDATA%\MagnetometerSystem\updates。</summary>
    public string DownloadDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MagnetometerSystem", "updates");

    /// <summary>仓库主页地址。</summary>
    public string HomepageUrl => $"https://gitee.com/{Owner}/{Repo}";

    /// <summary>发行版列表地址。取不到具体 release 时的兜底跳转目标。</summary>
    public string ReleasesUrl => $"{HomepageUrl}/releases";
}

/// <summary>检查更新的结果状态。</summary>
public enum UpdateCheckStatus
{
    /// <summary>已是最新版本。</summary>
    UpToDate,

    /// <summary>有可用更新。</summary>
    UpdateAvailable,

    /// <summary>检查失败（网络不通、接口异常等）。</summary>
    Failed
}

/// <summary>
/// 检查更新的结果。用状态而非 null 区分"已是最新"和"检查失败"——
/// 这两种情况在 UI 上要给出完全不同的提示。
/// </summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateInfo? Info = null,
    string? ErrorMessage = null)
{
    public static UpdateCheckResult UpToDate() => new(UpdateCheckStatus.UpToDate);
    public static UpdateCheckResult Available(UpdateInfo info) => new(UpdateCheckStatus.UpdateAvailable, info);
    public static UpdateCheckResult Failed(string message) => new(UpdateCheckStatus.Failed, null, message);
}

/// <summary>一个可用的新版本。</summary>
public sealed record UpdateInfo
{
    /// <summary>新版本号，形如 "0.4.0"。</summary>
    public required string Version { get; init; }

    /// <summary>git tag，形如 "v0.4.0"。</summary>
    public required string TagName { get; init; }

    /// <summary>更新说明（release 正文，原始 Markdown 文本）。</summary>
    public string ReleaseNotes { get; init; } = string.Empty;

    /// <summary>发行版页面地址，供"打开下载页面"兜底使用。</summary>
    public required string HtmlUrl { get; init; }

    /// <summary>发布时间。</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>与当前分发形态匹配的安装包下载地址。附件命名不符合规则时为 null。</summary>
    public string? DownloadUrl { get; init; }

    /// <summary>安装包文件名。</summary>
    public string? FileName { get; init; }

    /// <summary>SHA256SUMS.txt 的下载地址。缺失或无法取得有效目标校验值时，应用内下载必须失败。</summary>
    public string? ChecksumsUrl { get; init; }

    /// <summary>是否可以在应用内直接下载（附件齐全）。</summary>
    public bool CanDownload => !string.IsNullOrEmpty(DownloadUrl) && !string.IsNullOrEmpty(FileName);
}

/// <summary>下载进度。</summary>
public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
    /// <summary>0-100。总长度未知时为 null。</summary>
    public double? Percent => TotalBytes is > 0 ? BytesReceived * 100.0 / TotalBytes.Value : null;
}

/// <summary>
/// 检查并下载软件更新。数据源是 Gitee 的 Releases API。
/// </summary>
public interface IUpdateService
{
    /// <summary>当前配置（供 UI 取主页/发行版地址）。</summary>
    UpdateOptions Options { get; }

    /// <summary>
    /// 查询是否有新版本。不抛异常——网络问题一律以 <see cref="UpdateCheckStatus.Failed"/> 返回，
    /// 便于启动时的静默检查直接忽略。
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>
    /// 下载安装包到本地并校验 SHA256，返回本地文件完整路径。
    /// 失败时抛异常（取消则抛 <see cref="OperationCanceledException"/>），由调用方提示用户。
    /// </summary>
    Task<string> DownloadAsync(UpdateInfo info, IProgress<DownloadProgress>? progress, CancellationToken ct = default);

    /// <summary>
    /// 应用更新：安装版拉起 setup.exe 静默安装（调用方随后应退出程序），
    /// 便携版则打开压缩包所在文件夹让用户手动覆盖。
    /// </summary>
    /// <returns>true 表示调用方应当退出程序以便安装继续。</returns>
    bool TryApplyUpdate(UpdateInfo info, string localFilePath);

    /// <summary>用系统浏览器打开发行版页面。所有失败路径的兜底。</summary>
    void OpenReleasePage(UpdateInfo? info = null);

    /// <summary>清理下载目录中的历史残留文件。</summary>
    void CleanupDownloads();
}
