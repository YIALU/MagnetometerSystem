using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Update;

namespace MagnetometerSystem.Infrastructure.Tests;

/// <summary>
/// 覆盖 GiteeUpdateService 里两个纯解析方法。全部用固定字符串喂进去，不联网。
/// </summary>
public class GiteeUpdateServiceTests
{
    private const string Current = "0.4.0";

    /// <summary>构造一份符合 Gitee v5 接口形状的 release JSON。</summary>
    private static string Release(
        string tag = "v0.5.0",
        bool prerelease = false,
        string assets = """
            [
              {"name":"MagnetometerSystem-v0.5.0-setup.exe","browser_download_url":"https://gitee.com/dl/setup.exe"},
              {"name":"MagnetometerSystem-v0.5.0-portable-win-x64.zip","browser_download_url":"https://gitee.com/dl/portable.zip"},
              {"name":"SHA256SUMS.txt","browser_download_url":"https://gitee.com/dl/SHA256SUMS.txt"}
            ]
            """) =>
        $$"""
        {
          "tag_name": "{{tag}}",
          "prerelease": {{(prerelease ? "true" : "false")}},
          "body": "修了几个问题",
          "html_url": "https://gitee.com/yialu/MagnetometerSystem/releases/tag/{{tag}}",
          "created_at": "2026-08-01T10:00:00+08:00",
          "assets": {{assets}}
        }
        """;

    // ---------------------------------------------------------- 版本比较

    [Fact]
    public void ParseLatestRelease_远端更新_返回可更新()
    {
        var result = GiteeUpdateService.ParseLatestRelease(
            Release(tag: "v0.5.0"), Current, AppPackageKind.Installer);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("0.5.0", result.Info!.Version);
        Assert.Equal("v0.5.0", result.Info.TagName);
        Assert.Equal("修了几个问题", result.Info.ReleaseNotes);
    }

    [Theory]
    [InlineData("v0.4.0")]   // 相同
    [InlineData("v0.3.9")]   // 更旧
    [InlineData("v0.1.0")]
    public void ParseLatestRelease_远端不比本地新_返回已是最新(string tag)
    {
        var result = GiteeUpdateService.ParseLatestRelease(
            Release(tag), Current, AppPackageKind.Installer);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Info);
    }

    [Fact]
    public void ParseLatestRelease_不带v前缀的标签也能解析()
    {
        var result = GiteeUpdateService.ParseLatestRelease(
            Release(tag: "1.0.0"), Current, AppPackageKind.Installer);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("1.0.0", result.Info!.Version);
    }

    [Fact]
    public void ParseLatestRelease_预发布版本_不推送给用户()
    {
        var result = GiteeUpdateService.ParseLatestRelease(
            Release(tag: "v9.9.9", prerelease: true), Current, AppPackageKind.Installer);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    // ---------------------------------------------------------- 资源挑选

    [Fact]
    public void ParseLatestRelease_安装版_挑setup_exe()
    {
        var result = GiteeUpdateService.ParseLatestRelease(
            Release(), Current, AppPackageKind.Installer);

        Assert.Equal("https://gitee.com/dl/setup.exe", result.Info!.DownloadUrl);
        Assert.Equal("MagnetometerSystem-v0.5.0-setup.exe", result.Info.FileName);
        Assert.Equal("https://gitee.com/dl/SHA256SUMS.txt", result.Info.ChecksumsUrl);
        Assert.True(result.Info.CanDownload);
    }

    [Fact]
    public void ParseLatestRelease_便携版_挑portable_zip()
    {
        var result = GiteeUpdateService.ParseLatestRelease(
            Release(), Current, AppPackageKind.Portable);

        Assert.Equal("https://gitee.com/dl/portable.zip", result.Info!.DownloadUrl);
        Assert.Equal("MagnetometerSystem-v0.5.0-portable-win-x64.zip", result.Info.FileName);
        Assert.True(result.Info.CanDownload);
    }

    [Fact]
    public void ParseLatestRelease_旧命名的资源_降级为只能打开下载页()
    {
        // v0.3.2 时期的老资源名，两种形态都匹配不上
        var old = Release(assets: """
            [{"name":"MagnetometerSystem-v0.5.0.zip","browser_download_url":"https://gitee.com/dl/old.zip"}]
            """);

        var result = GiteeUpdateService.ParseLatestRelease(old, Current, AppPackageKind.Installer);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Null(result.Info!.DownloadUrl);
        Assert.False(result.Info.CanDownload);
        // 仍然要给出发行页地址，让用户能手动下载
        Assert.False(string.IsNullOrEmpty(result.Info.HtmlUrl));
    }

    [Fact]
    public void ParseLatestRelease_完全没有assets_不抛异常()
    {
        var result = GiteeUpdateService.ParseLatestRelease(
            Release(assets: "[]"), Current, AppPackageKind.Portable);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.False(result.Info!.CanDownload);
    }

    [Fact]
    public void ParseLatestRelease_缺少校验文件_仍可下载()
    {
        var noSums = Release(assets: """
            [{"name":"MagnetometerSystem-v0.5.0-setup.exe","browser_download_url":"https://gitee.com/dl/setup.exe"}]
            """);

        var result = GiteeUpdateService.ParseLatestRelease(noSums, Current, AppPackageKind.Installer);

        Assert.True(result.Info!.CanDownload);
        Assert.Null(result.Info.ChecksumsUrl);
    }

    // ---------------------------------------------------------- 异常输入

    [Fact]
    public void ParseLatestRelease_接口返回数组_取第一个元素()
    {
        var result = GiteeUpdateService.ParseLatestRelease(
            $"[{Release()}]", Current, AppPackageKind.Installer);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("0.5.0", result.Info!.Version);
    }

    [Fact]
    public void ParseLatestRelease_空数组_视为已是最新()
    {
        var result = GiteeUpdateService.ParseLatestRelease("[]", Current, AppPackageKind.Installer);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Theory]
    [InlineData("{ 这不是 json")]
    [InlineData("")]
    [InlineData("\"就是个字符串\"")]
    public void ParseLatestRelease_非法JSON_返回失败而不是抛异常(string json)
    {
        var result = GiteeUpdateService.ParseLatestRelease(json, Current, AppPackageKind.Installer);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public void ParseLatestRelease_缺少tag_name_返回失败()
    {
        var result = GiteeUpdateService.ParseLatestRelease(
            """{"body":"没有标签"}""", Current, AppPackageKind.Installer);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    [Theory]
    [InlineData("release-2026")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3.4")]
    public void ParseLatestRelease_标签不是三段版本号_返回失败(string tag)
    {
        var result = GiteeUpdateService.ParseLatestRelease(
            Release(tag), Current, AppPackageKind.Installer);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    [Fact]
    public void ParseLatestRelease_本地版本号非法_返回失败()
    {
        var result = GiteeUpdateService.ParseLatestRelease(
            Release(), "未知", AppPackageKind.Installer);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    // ---------------------------------------------------------- SHA256SUMS

    private const string Sums = """
        3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b  MagnetometerSystem-v0.5.0-setup.exe
        b1946ac92492d2347c6235b4d2611184d8b1c0f2e7b3a9e0e8f6a5c4b3a29180 *MagnetometerSystem-v0.5.0-portable-win-x64.zip
        """;

    [Fact]
    public void ParseChecksums_能取出对应文件的哈希()
    {
        var hash = GiteeUpdateService.ParseChecksums(Sums, "MagnetometerSystem-v0.5.0-setup.exe");

        Assert.Equal("3a7bd3e2360a3d29eea436fcfb7e44c735d117c42d1c1835420b6b9942dd4f1b", hash);
    }

    [Fact]
    public void ParseChecksums_容忍二进制模式的星号前缀()
    {
        var hash = GiteeUpdateService.ParseChecksums(Sums, "MagnetometerSystem-v0.5.0-portable-win-x64.zip");

        Assert.Equal("b1946ac92492d2347c6235b4d2611184d8b1c0f2e7b3a9e0e8f6a5c4b3a29180", hash);
    }

    [Fact]
    public void ParseChecksums_文件名不在清单里_返回null()
    {
        Assert.Null(GiteeUpdateService.ParseChecksums(Sums, "别的文件.zip"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("只有一列")]
    public void ParseChecksums_内容异常_返回null而不是抛异常(string content)
    {
        Assert.Null(GiteeUpdateService.ParseChecksums(content, "MagnetometerSystem-v0.5.0-setup.exe"));
    }

    // ---------------------------------------------------------- 版本解析

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("  v0.4.0  ", "0.4.0")]
    public void TryParseVersion_接受带或不带v前缀(string raw, string expected)
    {
        Assert.True(GiteeUpdateService.TryParseVersion(raw, out var v));
        Assert.Equal(expected, v.ToString(3));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1.2")]
    [InlineData("1.2.3-beta")]
    [InlineData("latest")]
    public void TryParseVersion_非三段数字_返回false(string? raw)
    {
        Assert.False(GiteeUpdateService.TryParseVersion(raw, out _));
    }
}
