using System.Globalization;
using System.IO;
using System.Reflection;
using MagnetometerSystem.Core.Services;

namespace MagnetometerSystem.App;

/// <summary>
/// 运行时版本信息：从程序集 InformationalVersion 解析。
/// 由仓库根 Directory.Build.props 在构建时注入 git 短 hash（含 dirty 标记）。
/// 例如：InformationalVersion = "0.1.0+163dfb8" 或 "0.1.0+163dfb8-dirty"
/// </summary>
public static class AppVersion
{
    /// <summary>便携版标记文件名。由 build.ps1 写入便携包，安装版没有这个文件。</summary>
    private const string PortableMarkerFileName = "portable.marker";

    private static readonly string _full = ReadInformationalVersion();

    /// <summary>完整字符串，例 "0.1.0+163dfb8"</summary>
    public static string Full => _full;

    /// <summary>纯版本号，例 "0.1.0"</summary>
    public static string Number { get; } = _full.Split('+')[0];

    /// <summary>git 短 hash（可能含 -dirty），无则 null</summary>
    public static string? Commit { get; } =
        _full.Contains('+') ? _full.Split('+', 2)[1] : null;

    /// <summary>UI 显示用：v0.1.0。提交号只在“关于”窗口单独列出。</summary>
    public static string Display => $"v{Number}";

    /// <summary>诊断用（匿名反馈等）：v0.1.0 (163dfb8)，带提交号便于定位构建。</summary>
    public static string DiagnosticVersion =>
        Commit is null ? $"v{Number}" : $"v{Number} ({Commit})";

    /// <summary>
    /// 当前程序是安装版还是便携版。决定检查更新时下载哪种包。
    /// 用 AppContext.BaseDirectory 而非 Assembly.Location —— 后者在单文件发布下为空。
    /// </summary>
    public static AppPackageKind PackageKind { get; } =
        File.Exists(Path.Combine(AppContext.BaseDirectory, PortableMarkerFileName))
            ? AppPackageKind.Portable
            : AppPackageKind.Installer;

    /// <summary>分发形态的中文名，供"关于"窗口显示。</summary>
    public static string PackageKindDisplay =>
        PackageKind == AppPackageKind.Portable ? "便携版" : "安装版";

    /// <summary>
    /// 构建时间。优先读 Release 构建注入的 BuildTimeUtc 元数据
    /// （单文件发布下 Assembly.Location 为空，只能靠它），
    /// 取不到时回退到程序集文件的最后写入时间。
    /// </summary>
    public static DateTime BuildTime { get; } = ReadBuildTime();

    private static string ReadInformationalVersion()
    {
        var attr = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return attr?.InformationalVersion ?? "0.0.0";
    }

    private static DateTime ReadBuildTime()
    {
        try
        {
            var metadata = typeof(AppVersion).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildTimeUtc");

            if (metadata?.Value is { Length: > 0 } raw &&
                DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var utc))
            {
                return utc.ToLocalTime();
            }
        }
        catch { }

        try
        {
            var path = typeof(AppVersion).Assembly.Location;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return File.GetLastWriteTime(path);
        }
        catch { }

        return DateTime.MinValue;
    }
}
