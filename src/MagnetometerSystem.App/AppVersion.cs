using System.IO;
using System.Reflection;

namespace MagnetometerSystem.App;

/// <summary>
/// 运行时版本信息：从程序集 InformationalVersion 解析。
/// 由仓库根 Directory.Build.props 在构建时注入 git 短 hash（含 dirty 标记）。
/// 例如：InformationalVersion = "0.1.0+163dfb8" 或 "0.1.0+163dfb8-dirty"
/// </summary>
public static class AppVersion
{
    private static readonly string _full = ReadInformationalVersion();

    /// <summary>完整字符串，例 "0.1.0+163dfb8"</summary>
    public static string Full => _full;

    /// <summary>纯版本号，例 "0.1.0"</summary>
    public static string Number { get; } = _full.Split('+')[0];

    /// <summary>git 短 hash（可能含 -dirty），无则 null</summary>
    public static string? Commit { get; } =
        _full.Contains('+') ? _full.Split('+', 2)[1] : null;

    /// <summary>UI 显示用：v0.1.0 (163dfb8)</summary>
    public static string Display =>
        Commit is null ? $"v{Number}" : $"v{Number} ({Commit})";

    /// <summary>构建时间（程序集文件的最后写入时间作为近似）</summary>
    public static DateTime BuildTime
    {
        get
        {
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

    private static string ReadInformationalVersion()
    {
        var attr = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return attr?.InformationalVersion ?? "0.0.0";
    }
}
