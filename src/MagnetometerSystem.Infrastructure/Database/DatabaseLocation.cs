using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.Infrastructure.Database;

/// <summary>
/// 决定数据库放在哪，并把旧版本留在 %LOCALAPPDATA% 的数据库迁移过来。
/// 默认放在程序目录下的 data\（安装版和便携版一致），目录不可写时回退到旧位置。
/// </summary>
public static class DatabaseLocation
{
    public const string FileName = "magnetometer.db";
    public const string DataFolderName = "data";

    /// <summary>迁移完成后写在新目录里的标记：之后即使删掉新数据库也不会再从旧位置复制。</summary>
    public const string MigratedMarkerFileName = "migrated-from-localappdata.txt";

    /// <summary>旧版本统一使用的数据目录：%LOCALAPPDATA%\MagnetometerSystem。</summary>
    public static string LegacyDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MagnetometerSystem");

    /// <summary>程序目录下的 data\。用 AppContext.BaseDirectory —— 单文件发布下 Assembly.Location 为空。</summary>
    public static string ProgramDataDirectory { get; } = Path.Combine(AppContext.BaseDirectory, DataFolderName);

    /// <summary>首选目录可创建且可写就用它，否则用回退目录。</summary>
    public static string ResolveDirectory(string preferred, string fallback)
    {
        try
        {
            Directory.CreateDirectory(preferred);
            var probe = Path.Combine(preferred, $".write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return preferred;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"程序目录不可写，数据库改用 {fallback}: {ex.Message}");
            return fallback;
        }
    }

    /// <summary>
    /// 新位置还没有数据库、也没迁移过，而旧位置有数据库时，用 SQLite 备份接口复制一份过去
    /// （WAL 中未合并的内容一并带上）。旧文件原样保留。
    /// 返回 true 表示本次完成了迁移；无需迁移返回 false；复制失败抛异常，目标位置不留半成品。
    /// </summary>
    public static bool MigrateIfNeeded(string legacyDbPath, string targetDbPath)
    {
        if (PathsEqual(legacyDbPath, targetDbPath)) return false;
        if (File.Exists(targetDbPath)) return false;

        var targetDir = Path.GetDirectoryName(targetDbPath)!;
        var marker = Path.Combine(targetDir, MigratedMarkerFileName);
        if (File.Exists(marker)) return false;
        if (!File.Exists(legacyDbPath)) return false;

        Directory.CreateDirectory(targetDir);
        var temp = targetDbPath + ".migrating";
        DeleteIfExists(temp);
        try
        {
            using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = legacyDbPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString()))
            using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temp,
                Pooling = false
            }.ToString()))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }

            File.Move(temp, targetDbPath);
        }
        catch
        {
            DeleteIfExists(temp);
            throw;
        }

        // 数据库已完整就位，迁移即告成功。标记只用于“删掉新库后不再重复复制”，写失败不能让本次
        // 回退到旧库 —— 否则本次写进旧库的数据下次启动会被已存在的新库挡住而“消失”。
        try
        {
            File.WriteAllText(marker,
                $"数据库已于 {DateTime.Now:yyyy-MM-dd HH:mm:ss} 从 {legacyDbPath} 复制到本目录。{Environment.NewLine}" +
                $"旧文件仍保留在原处，确认新位置数据完整后可自行删除。{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"写入迁移标记失败（数据库已迁移，不影响使用）: {ex.Message}");
        }
        TryWriteLegacyNote(legacyDbPath, targetDbPath);
        return true;
    }

    /// <summary>在旧目录留一份说明，用户去老地方找数据时能知道去向。失败不影响迁移结果。</summary>
    private static void TryWriteLegacyNote(string legacyDbPath, string targetDbPath)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetDirectoryName(legacyDbPath)!, "数据库已迁移.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 起，程序使用的数据库改为：{Environment.NewLine}" +
                $"{targetDbPath}{Environment.NewLine}{Environment.NewLine}" +
                $"本目录中的 {Path.GetFileName(legacyDbPath)} 是迁移前的旧副本，程序不再写入。{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"写入旧目录迁移说明失败: {ex.Message}");
        }
    }

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Trace.TraceWarning($"删除临时文件失败 {path}: {ex.Message}"); }
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
