using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MagnetometerSystem.Core.Services;

namespace MagnetometerSystem.Infrastructure.Update;

/// <summary>
/// 基于 Gitee Releases API 的更新检查实现。
///
/// 依赖 build.ps1 产出的固定附件命名：
///   MagnetometerSystem-v0.4.0-setup.exe
///   MagnetometerSystem-v0.4.0-portable-win-x64.zip
///   SHA256SUMS.txt
/// 附件命名不符（例如 v0.3.2 及更早的发行版）时不会报错，只是无法应用内下载，
/// UI 会退化为"打开下载页面"。
///
/// 本类不写日志，失败信息通过返回值/异常上抛，由 App 层用 Serilog 记录，
/// 以免 Infrastructure 引入日志框架依赖。
/// </summary>
public sealed class GiteeUpdateService : IUpdateService, IDisposable
{
    /// <summary>形如 0.4.0 的严格三段版本号。tag 允许带 v 前缀。</summary>
    private static readonly Regex VersionPattern = new(@"^v?(\d+\.\d+\.\d+)$", RegexOptions.Compiled);

    private const string ChecksumsFileName = "SHA256SUMS.txt";

    /// <summary>下载目录里超过这个天数的残留文件会被清理。</summary>
    private static readonly TimeSpan DownloadRetention = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private bool _disposed;

    public UpdateOptions Options { get; }

    public GiteeUpdateService(UpdateOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));

        _http = new HttpClient
        {
            // 只覆盖到响应头；下载正文时用 ResponseHeadersRead 绕开该超时
            Timeout = TimeSpan.FromSeconds(15)
        };
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("MagnetometerSystem", options.CurrentVersion));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ------------------------------------------------------------------ 检查

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"https://gitee.com/api/v5/repos/{Options.Owner}/{Options.Repo}/releases/latest";

            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed($"服务器返回 {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseLatestRelease(json, Options.CurrentVersion, Options.PackageKind);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 含 TaskCanceledException（超时）、HttpRequestException（断网/DNS 失败）等
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// 解析 Gitee release JSON 并与当前版本比较。
    /// 抽成 internal static 便于单元测试用固定 JSON 覆盖各种边界，不打真实网络。
    /// </summary>
    internal static UpdateCheckResult ParseLatestRelease(string json, string currentVersion, AppPackageKind kind)
    {
        JsonElement root;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return UpdateCheckResult.Failed($"返回内容不是合法 JSON: {ex.Message}");
        }

        using (doc)
        {
            root = doc.RootElement;

            // 少数情况下接口会返回数组，取第一个元素
            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0) return UpdateCheckResult.UpToDate();
                root = root[0];
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return UpdateCheckResult.Failed("返回内容格式不符合预期");
            }

            // 预发布版本不推送给普通用户
            if (root.TryGetProperty("prerelease", out var pre) &&
                pre.ValueKind == JsonValueKind.True)
            {
                return UpdateCheckResult.UpToDate();
            }

            var tagName = GetString(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return UpdateCheckResult.Failed("发行版信息里没有 tag_name");
            }

            if (!TryParseVersion(tagName, out var remote))
            {
                return UpdateCheckResult.Failed($"无法解析发行版版本号: {tagName}");
            }

            if (!TryParseVersion(currentVersion, out var local))
            {
                return UpdateCheckResult.Failed($"无法解析当前版本号: {currentVersion}");
            }

            if (remote <= local) return UpdateCheckResult.UpToDate();

            var (downloadUrl, fileName, checksumsUrl) = SelectAssets(root, kind);

            var info = new UpdateInfo
            {
                Version = remote.ToString(3),
                TagName = tagName.Trim(),
                ReleaseNotes = GetString(root, "body") ?? string.Empty,
                HtmlUrl = ResolveHtmlUrl(root, tagName.Trim()),
                PublishedAt = ParseDate(GetString(root, "created_at")),
                DownloadUrl = downloadUrl,
                FileName = fileName,
                ChecksumsUrl = checksumsUrl
            };

            return UpdateCheckResult.Available(info);
        }
    }

    /// <summary>按分发形态从 assets 里挑出对应的安装包和校验文件。</summary>
    private static (string? DownloadUrl, string? FileName, string? ChecksumsUrl) SelectAssets(
        JsonElement release, AppPackageKind kind)
    {
        if (!release.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return (null, null, null);
        }

        string? downloadUrl = null, fileName = null, checksumsUrl = null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = GetString(asset, "name");
            var url = GetString(asset, "browser_download_url");
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url)) continue;

            if (name.Equals(ChecksumsFileName, StringComparison.OrdinalIgnoreCase))
            {
                checksumsUrl = url;
                continue;
            }

            if (downloadUrl is not null) continue;

            var matches = kind switch
            {
                AppPackageKind.Installer =>
                    name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase),
                AppPackageKind.Portable =>
                    name.Contains("-portable-", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
                _ => false
            };

            if (matches)
            {
                downloadUrl = url;
                fileName = name;
            }
        }

        return (downloadUrl, fileName, checksumsUrl);
    }

    /// <summary>
    /// Gitee 的 release 对象不一定带 html_url 字段，用 tag 拼一个稳定的发行版地址兜底。
    /// </summary>
    private static string ResolveHtmlUrl(JsonElement release, string tagName)
    {
        var html = GetString(release, "html_url");
        if (!string.IsNullOrWhiteSpace(html)) return html;

        // 从任一附件地址反推仓库路径：
        // https://gitee.com/{owner}/{repo}/releases/download/{tag}/{file}
        if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var url = GetString(asset, "browser_download_url");
                if (string.IsNullOrEmpty(url)) continue;

                var idx = url.IndexOf("/releases/", StringComparison.OrdinalIgnoreCase);
                if (idx > 0) return $"{url[..idx]}/releases/tag/{tagName}";
            }
        }

        return string.Empty;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static DateTimeOffset? ParseDate(string? raw) =>
        DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt)
            ? dt
            : null;

    internal static bool TryParseVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var match = VersionPattern.Match(raw.Trim());
        return match.Success && Version.TryParse(match.Groups[1].Value, out version!);
    }

    // ------------------------------------------------------------------ 下载

    public async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        if (!info.CanDownload)
        {
            throw new InvalidOperationException("该发行版没有提供可直接下载的安装包");
        }

        Directory.CreateDirectory(Options.DownloadDirectory);

        var targetPath = Path.Combine(Options.DownloadDirectory, info.FileName!);
        var partPath = targetPath + ".part";

        var expectedHash = await TryGetExpectedHashAsync(info, ct).ConfigureAwait(false);

        // 上次下载完成过就别再下一遍
        if (File.Exists(targetPath))
        {
            if (expectedHash is null ||
                string.Equals(await ComputeSha256Async(targetPath, ct).ConfigureAwait(false),
                              expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return targetPath;
            }
            File.Delete(targetPath);
        }

        File.Delete(partPath);

        try
        {
            using (var response = await _http.GetAsync(
                       info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength;
                progress?.Report(new DownloadProgress(0, total));

                using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var target = new FileStream(
                    partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, useAsync: true);

                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new DownloadProgress(received, total));
                }
            }

            if (expectedHash is not null)
            {
                var actual = await ComputeSha256Async(partPath, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "安装包校验失败，文件可能在传输中损坏。请重试，或到发行版页面手动下载。");
                }
            }

            File.Move(partPath, targetPath, overwrite: true);
            return targetPath;
        }
        catch
        {
            // 失败不留半截文件，否则下次会被当成"已下载完成"
            TryDelete(partPath);
            throw;
        }
    }

    /// <summary>
    /// 从 SHA256SUMS.txt 取该文件的预期哈希。附件不存在或取不到时返回 null（跳过校验）。
    /// 校验文件本身取不到不应阻断更新——它只是加分项。
    /// </summary>
    private async Task<string?> TryGetExpectedHashAsync(UpdateInfo info, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(info.ChecksumsUrl) || string.IsNullOrEmpty(info.FileName))
        {
            return null;
        }

        try
        {
            var text = await _http.GetStringAsync(info.ChecksumsUrl, ct).ConfigureAwait(false);
            return ParseChecksums(text, info.FileName!);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析 sha256sum 格式：「&lt;hash&gt;&lt;空白&gt;&lt;文件名&gt;」，每行一条。</summary>
    internal static string? ParseChecksums(string content, string fileName)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            var parts = trimmed.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) continue;

            // 兼容 sha256sum 二进制模式的 '*' 前缀
            var name = parts[1].Trim().TrimStart('*');
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].Trim();
            }
        }

        return null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        using var sha = SHA256.Create();

        var hash = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // -------------------------------------------------------------- 应用更新

    public bool TryApplyUpdate(UpdateInfo info, string localFilePath)
    {
        if (!File.Exists(localFilePath))
        {
            throw new FileNotFoundException("更新包不存在", localFilePath);
        }

        if (Options.PackageKind == AppPackageKind.Installer)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("自动安装更新仅支持 Windows。");
            // Setup checks AppMutex immediately, even in silent mode. Keep the application's mutex
            // alive and let a ready helper wait for actual process termination before launching Setup.
            using var owner = Process.GetCurrentProcess();
            using var waiter = InstallerHandoff.Start(localFilePath, "/SILENT /CLOSEAPPLICATIONS /NORESTART",
                Path.GetDirectoryName(Path.GetFullPath(localFilePath))!, owner);
            return true; // Only a successfully acknowledged handoff permits the caller to exit.
        }

        // 便携版：运行中的单文件 exe 无法自我覆盖，只能引导用户手动解压
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{localFilePath}\"",
            UseShellExecute = true
        });
        return false;
    }

    public void OpenReleasePage(UpdateInfo? info = null)
    {
        var url = !string.IsNullOrWhiteSpace(info?.HtmlUrl) ? info!.HtmlUrl : Options.ReleasesUrl;

        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true
        });
    }

    public void CleanupDownloads()
    {
        try
        {
            if (!Directory.Exists(Options.DownloadDirectory)) return;

            var cutoff = DateTime.UtcNow - DownloadRetention;
            foreach (var file in Directory.EnumerateFiles(Options.DownloadDirectory))
            {
                // .part 是中断的下载，任何时候都没有保留价值
                if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                    File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    TryDelete(file);
                }
            }
        }
        catch
        {
            // 清理失败无关紧要
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 文件被占用等情况忽略
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
