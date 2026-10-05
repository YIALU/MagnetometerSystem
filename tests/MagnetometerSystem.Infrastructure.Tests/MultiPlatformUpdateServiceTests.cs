using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Update;

namespace MagnetometerSystem.Infrastructure.Tests;

public class MultiPlatformUpdateServiceTests
{
    [Theory]
    [InlineData("0.5.0", "0.6.0", UpdateSource.GitHub)]
    [InlineData("0.6.0", "0.5.0", UpdateSource.Gitee)]
    [InlineData("0.5.0", "0.5.0", UpdateSource.Gitee)]
    public async Task AutomaticChoosesHighestVersionAndOnlyKeepsMatchingMirrors(string giteeVersion, string githubVersion, UpdateSource expected)
    {
        using var fixture = new Fixture();
        fixture.GiteeVersion = giteeVersion; fixture.GitHubVersion = githubVersion;
        var result = await fixture.Service.CheckForUpdateAsync();
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(expected, result.Info!.Source);
        Assert.All(result.Info.Mirrors, x => Assert.Equal(result.Info.Version, x.Version));
        Assert.Equal(giteeVersion == githubVersion ? 1 : 0, result.Info.Mirrors.Count);
        Assert.Contains(fixture.Requests, x => x.Host == "api.github.com" && x.AbsolutePath == "/repos/YIALU/MagnetometerSystem/releases/latest");
        Assert.Contains(fixture.Requests, x => x.Host == "gitee.com" && x.AbsolutePath == "/api/v5/repos/yialu/MagnetometerSystem/releases/latest");
    }

    [Theory]
    [InlineData(UpdateSource.Gitee)]
    [InlineData(UpdateSource.GitHub)]
    public async Task ExplicitSourceOnlyQueriesChosenPlatform(UpdateSource source)
    {
        using var fixture = new Fixture(); fixture.Options.PreferredSource = source;
        var result = await fixture.Service.CheckForUpdateAsync();
        Assert.Equal(source, result.Info!.Source);
        Assert.Empty(result.Info.Mirrors);
        Assert.Single(fixture.Requests);
    }

    [Theory]
    [InlineData("not-json", "0.5.0", UpdateCheckStatus.UpdateAvailable)]
    [InlineData("not-json", "0.4.0", UpdateCheckStatus.UpToDate)]
    [InlineData("not-json", "not-json", UpdateCheckStatus.Failed)]
    public async Task PartialFailuresRemainDiagnosable(string giteeVersion, string githubVersion, UpdateCheckStatus status)
    {
        using var fixture = new Fixture(); fixture.GiteeVersion = giteeVersion; fixture.GitHubVersion = githubVersion;
        var result = await fixture.Service.CheckForUpdateAsync();
        Assert.Equal(status, result.Status);
        Assert.Contains("Gitee", result.WarningMessage ?? result.ErrorMessage);
    }

    [Fact]
    public async Task NetworkFailureOnOnePlatformStillFindsOtherRelease()
    {
        using var fixture = new Fixture(); fixture.GiteeCheckThrows = true;
        var result = await fixture.Service.CheckForUpdateAsync();
        Assert.Equal(UpdateSource.GitHub, result.Info!.Source);
        Assert.Contains("Gitee", result.WarningMessage);
    }

    [Fact]
    public async Task SameVersionPrefersCompletePackageButNeverDowngradesToOlderCompleteRelease()
    {
        using var fixture = new Fixture(); fixture.GiteeAssets = false;
        Assert.Equal(UpdateSource.GitHub, (await fixture.Service.CheckForUpdateAsync()).Info!.Source);
        fixture.GiteeVersion = "0.6.0";
        var result = await fixture.Service.CheckForUpdateAsync();
        Assert.Equal("0.6.0", result.Info!.Version);
        Assert.False(result.Info.CanDownload);
        Assert.Empty(result.Info.Mirrors);
    }

    [Theory]
    [InlineData(AppPackageKind.Installer)]
    [InlineData(AppPackageKind.Portable)]
    public async Task DownloadNetworkFailureSwitchesOnlyToSameVersionAndValidatesOtherPlatformsManifest(AppPackageKind kind)
    {
        using var fixture = new Fixture(kind); fixture.GiteePackageFails = true;
        var info = (await fixture.Service.CheckForUpdateAsync()).Info!;
        var reports = new List<DownloadProgress>();
        var path = await fixture.Service.DownloadAsync(info, new CaptureProgress(reports));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(path));
        Assert.Contains(fixture.Requests, x => x.Host == "github.com" && x.AbsolutePath.EndsWith("SHA256SUMS.txt"));
        Assert.Contains(reports, x => x.Source == UpdateSource.GitHub && x.BytesReceived == 0);
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task UnreachablePrimaryManifestAlsoUsesVerifiedMirror()
    {
        using var fixture = new Fixture(); fixture.GiteeManifestFails = true;
        var path = await fixture.Service.DownloadAsync((await fixture.Service.CheckForUpdateAsync()).Info!, null);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task InterruptedBodyIsDiscardedBeforeDownloadingMirrorFromTheBeginning()
    {
        using var fixture = new Fixture(); fixture.GiteeMidStreamFails = true;
        var reports = new List<DownloadProgress>();
        var path = await fixture.Service.DownloadAsync((await fixture.Service.CheckForUpdateAsync()).Info!, new CaptureProgress(reports));
        Assert.Contains(reports, x => x.Source == UpdateSource.Gitee && x.BytesReceived > 0);
        Assert.Contains(reports, x => x.Source == UpdateSource.GitHub && x.BytesReceived == 0);
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task MirrorIsValidatedAgainstItsOwnManifestAndMismatchIsNotPromoted()
    {
        using var fixture = new Fixture(); fixture.GiteePackageFails = true; fixture.GitHubWrongHash = true;
        var info = (await fixture.Service.CheckForUpdateAsync()).Info!;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.DownloadAsync(info, null));
        Assert.Contains(fixture.Requests, x => x.Host == "github.com" && x.AbsolutePath.EndsWith("SHA256SUMS.txt"));
        Assert.Empty(Directory.EnumerateFiles(fixture.Directory));
    }

    [Fact]
    public async Task HashMismatchStopsWithoutFallbackAndLeavesNoCompletedOrPartialFile()
    {
        using var fixture = new Fixture(); fixture.GiteeWrongHash = true;
        var info = (await fixture.Service.CheckForUpdateAsync()).Info!;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.DownloadAsync(info, null));
        Assert.DoesNotContain(fixture.Requests, x => x.Host == "github.com");
        Assert.Empty(Directory.EnumerateFiles(fixture.Directory));
    }

    [Fact]
    public async Task PrimaryDownloadFailureNeverUsesDifferentVersion()
    {
        using var fixture = new Fixture(); fixture.GiteeVersion = "0.6.0"; fixture.GiteePackageFails = true;
        var info = (await fixture.Service.CheckForUpdateAsync()).Info!;
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Service.DownloadAsync(info, null));
        Assert.DoesNotContain(fixture.Requests, x => x.Host == "github.com");
    }

    [Fact]
    public async Task CancellationDoesNotDownloadFromOtherPlatform()
    {
        using var fixture = new Fixture();
        var info = (await fixture.Service.CheckForUpdateAsync()).Info!;
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.DownloadAsync(info, null, cts.Token));
        Assert.DoesNotContain(fixture.Requests, x => x.Host == "github.com");
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    public void DraftAndPreviewGitHubReleasesAreIgnored(string flag)
    {
        var result = ReleaseUpdateService.ParseLatestRelease($$"""{"tag_name":"v9.0.0","{{flag}}":true} """, "0.4.0", AppPackageKind.Installer);
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task GitHubPublishedDateIsUsedInsteadOfCommitDate()
    {
        using var fixture = new Fixture(); fixture.Options.PreferredSource = UpdateSource.GitHub;
        var info = (await fixture.Service.CheckForUpdateAsync()).Info!;
        Assert.Equal(DateTimeOffset.Parse("2026-10-04T14:36:15Z"), info.PublishedAt);
    }

    private sealed class CaptureProgress(List<DownloadProgress> reports) : IProgress<DownloadProgress>
    { public void Report(DownloadProgress value) => reports.Add(value); }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "magnetometer-update-" + Guid.NewGuid());
        public UpdateOptions Options { get; }
        public MultiPlatformUpdateService Service { get; }
        public List<Uri> Requests { get; } = [];
        public byte[] Bytes { get; } = "validated same release bytes"u8.ToArray();
        public string GiteeVersion = "0.5.0", GitHubVersion = "0.5.0";
        public bool GiteeAssets = true, GiteeCheckThrows, GiteePackageFails, GiteeManifestFails, GiteeWrongHash, GitHubWrongHash, GiteeMidStreamFails;

        public Fixture(AppPackageKind kind = AppPackageKind.Installer)
        {
            Options = new() { CurrentVersion = "0.4.0", PackageKind = kind, DownloadDirectory = Directory };
            Service = new(Options, new Handler(this, false), new Handler(this, true));
        }
        private HttpResponseMessage Respond(HttpRequestMessage request, bool github, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (Requests) Requests.Add(request.RequestUri!);
            Assert.Null(request.Headers.Authorization);
            if (request.RequestUri!.AbsolutePath.EndsWith("/latest"))
            {
                if (!github && GiteeCheckThrows) throw new HttpRequestException("offline");
                var version = github ? GitHubVersion : GiteeVersion;
                if (version == "not-json") return new(HttpStatusCode.OK) { Content = new StringContent("not-json") };
                var host = github ? "github.com" : "gitee.com";
                var file = $"MagnetometerSystem-v{version}-" + (Options.PackageKind == AppPackageKind.Installer ? "setup.exe" : "portable-win-x64.zip");
                object[] assets = !github && !GiteeAssets ? [] : [new { name = file, browser_download_url = $"https://{host}/dl/{file}" }, new { name = "SHA256SUMS.txt", browser_download_url = $"https://{host}/dl/SHA256SUMS.txt" }];
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { tag_name = "v" + version, published_at = "2026-10-04T14:36:15Z", created_at = "2026-01-01T00:00:00Z", assets })) };
            }
            if (request.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt"))
            {
                if (!github && GiteeManifestFails) return new(HttpStatusCode.ServiceUnavailable);
                var version = github ? GitHubVersion : GiteeVersion;
                var name = $"MagnetometerSystem-v{version}-" + (Options.PackageKind == AppPackageKind.Installer ? "setup.exe" : "portable-win-x64.zip");
                var hash = (!github && GiteeWrongHash) || (github && GitHubWrongHash) ? new string('a', 64) : Convert.ToHexString(SHA256.HashData(Bytes));
                return new(HttpStatusCode.OK) { Content = new StringContent($"{hash}  {name}\n") };
            }
            if (!github && GiteePackageFails) return new(HttpStatusCode.ServiceUnavailable);
            if (!github && GiteeMidStreamFails) return new(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(Bytes)) };
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
        }
        public void Dispose() { Service.Dispose(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
        private sealed class Handler(Fixture fixture, bool github) : HttpMessageHandler
        { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(fixture.Respond(request, github, ct)); }
        private sealed class InterruptedStream(byte[] bytes) : MemoryStream(bytes)
        {
            private bool _read;
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                if (_read) throw new HttpIOException(HttpRequestError.ResponseEnded, "network stream interrupted");
                _read = true;
                return base.ReadAsync(buffer[..Math.Min(buffer.Length, 10)], ct);
            }
        }
    }
}
