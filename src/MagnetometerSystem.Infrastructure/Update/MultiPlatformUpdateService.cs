using System.Net.Http;
using MagnetometerSystem.Core.Services;

namespace MagnetometerSystem.Infrastructure.Update;

/// <summary>自动查询两边发布；只在同版本的镜像之间切换，校验逻辑始终属于各自发布。</summary>
public sealed class MultiPlatformUpdateService : IUpdateService, IDisposable
{
    private readonly ReleaseUpdateService _gitee;
    private readonly ReleaseUpdateService _github;
    public UpdateOptions Options { get; }

    public MultiPlatformUpdateService(UpdateOptions options) : this(options, null, null) { }
    internal MultiPlatformUpdateService(UpdateOptions options, HttpMessageHandler? gitee, HttpMessageHandler? github)
    {
        Options = options;
        _gitee = new(options, UpdateSource.Gitee, gitee);
        _github = new(options, UpdateSource.GitHub, github);
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var preference = Options.PreferredSource;
        if (preference != UpdateSource.Automatic) return await Service(preference).CheckForUpdateAsync(ct).ConfigureAwait(false);
        var results = await Task.WhenAll(_gitee.CheckForUpdateAsync(ct), _github.CheckForUpdateAsync(ct)).ConfigureAwait(false);
        var warnings = results.Select((result, index) => result.Status == UpdateCheckStatus.Failed
            ? $"{(index == 0 ? "Gitee" : "GitHub")}：{result.ErrorMessage}" : null).Where(x => x != null).ToArray();
        var warning = warnings.Length > 0 ? string.Join("；", warnings) : null;
        var candidates = results.Where(x => x.Status == UpdateCheckStatus.UpdateAvailable && x.Info != null)
            .Select(x => x.Info!).OrderByDescending(x => Version.Parse(x.Version))
            .ThenByDescending(x => x.CanDownload && !string.IsNullOrWhiteSpace(x.ChecksumsUrl)).ToArray();
        if (candidates.Length > 0)
        {
            var chosen = candidates[0];
            var mirrors = candidates.Skip(1).Where(x => x.Version == chosen.Version).ToArray();
            return UpdateCheckResult.Available(chosen with { Mirrors = mirrors }) with { WarningMessage = warning };
        }
        return results.Any(x => x.Status == UpdateCheckStatus.UpToDate)
            ? UpdateCheckResult.UpToDate() with { WarningMessage = warning }
            : UpdateCheckResult.Failed(warning ?? "没有可用的更新平台");
    }

    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        try { return await Service(info.Source).DownloadAsync(info, new SourceProgress(progress, info.Source), ct).ConfigureAwait(false); }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsNetworkFailure(ex))
        {
            var mirror = info.Mirrors.FirstOrDefault(x => x.Source != info.Source && x.Version == info.Version
                && x.FileName == info.FileName && x.CanDownload && !string.IsNullOrWhiteSpace(x.ChecksumsUrl));
            if (mirror == null) throw;
            // 上一平台的 .part 已清理。另一平台重新读取自己的清单并从头下载，不能拼接字节流。
            progress?.Report(new DownloadProgress(0, null) { Source = mirror.Source });
            return await Service(mirror.Source).DownloadAsync(mirror, new SourceProgress(progress, mirror.Source), ct).ConfigureAwait(false);
        }
    }

    private static bool IsNetworkFailure(Exception ex) => ex is HttpRequestException or HttpIOException or TaskCanceledException
        || (ex is InvalidDataException && ex.InnerException != null && IsNetworkFailure(ex.InnerException));
    private sealed class SourceProgress(IProgress<DownloadProgress>? target, UpdateSource source) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => target?.Report(value with { Source = source });
    }
    private ReleaseUpdateService Service(UpdateSource source) => source switch
    {
        UpdateSource.Gitee => _gitee,
        UpdateSource.GitHub => _github,
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };
    public bool TryApplyUpdate(UpdateInfo info, string localFilePath) => Service(info.Source).TryApplyUpdate(info, localFilePath);
    public void OpenReleasePage(UpdateInfo? info = null) => Service(info?.Source ?? (Options.PreferredSource == UpdateSource.GitHub ? UpdateSource.GitHub : UpdateSource.Gitee)).OpenReleasePage(info);
    public void CleanupDownloads() => _gitee.CleanupDownloads();
    public void Dispose() { _gitee.Dispose(); _github.Dispose(); }
}
