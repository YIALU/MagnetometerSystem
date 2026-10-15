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

    /// <summary>
    /// 使用旧位置数据库的进程在旧目录持有的占用文件（共享打开，进程退出自动释放）。
    /// 迁移前若能独占打开它，说明没有新版实例还在往旧库写。
    /// </summary>
    public const string LegacyInUseFileName = "magnetometer.db.inuse";

    /// <summary>串行化各实例的“迁移还是沿用旧库”判断，避免同时首启时一个迁移、一个沿用旧库。</summary>
    private const string MigrationMutexName = "MagnetometerSystem.DatabaseMigration";

    private static readonly object HeldLocksGate = new();
    private static readonly Dictionary<string, FileStream> HeldLocks = new(StringComparer.OrdinalIgnoreCase);

    public enum MigrationOutcome
    {
        /// <summary>无需迁移：新位置已有数据库、已迁移过，或旧位置没有数据库。</summary>
        NotNeeded,
        /// <summary>本次完成了迁移。</summary>
        Migrated,
        /// <summary>另有实例正在使用旧数据库，本次不迁移、沿用旧库，避免复制后旧库还有人写入。</summary>
        DeferredLegacyInUse
    }

    /// <summary>
    /// 在全局迁移锁内决定本次用哪个库：新位置已有库就用新库；旧库还有其他实例在用
    /// （<paramref name="anotherInstanceRunning"/> 覆盖不认识占用文件的旧版本程序，占用文件覆盖新版本中回退到旧库的实例）
    /// 就沿用旧库并登记占用；否则迁移。复制失败抛异常，调用方回退旧库时应调用 <see cref="HoldLegacyInUse"/>。
    /// </summary>
    public static MigrationOutcome MigrateOrDefer(string legacyDbPath, string targetDbPath, bool anotherInstanceRunning)
    {
        if (PathsEqual(legacyDbPath, targetDbPath))
        {
            HoldLegacyInUse(legacyDbPath);
            return MigrationOutcome.NotNeeded;
        }

        using var gate = AcquireMigrationGate();

        var targetDir = Path.GetDirectoryName(targetDbPath)!;
        if (File.Exists(targetDbPath) || File.Exists(Path.Combine(targetDir, MigratedMarkerFileName)) || !File.Exists(legacyDbPath))
            return MigrationOutcome.NotNeeded;

        if (anotherInstanceRunning || IsLegacyInUse(legacyDbPath))
        {
            HoldLegacyInUse(legacyDbPath);
            return MigrationOutcome.DeferredLegacyInUse;
        }

        return MigrateIfNeeded(legacyDbPath, targetDbPath) ? MigrationOutcome.Migrated : MigrationOutcome.NotNeeded;
    }

    /// <summary>登记本进程正在使用旧数据库，直到进程退出。</summary>
    public static void HoldLegacyInUse(string legacyDbPath)
    {
        var path = LegacyInUsePath(legacyDbPath);
        lock (HeldLocksGate)
        {
            if (HeldLocks.ContainsKey(path)) return;
            try
            {
                HeldLocks[path] = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"登记旧数据库占用失败 {path}: {ex.Message}");
            }
        }
    }

    private static bool IsLegacyInUse(string legacyDbPath)
    {
        try
        {
            using var probe = new FileStream(LegacyInUsePath(legacyDbPath), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (Exception ex)
        {
            // 无法判断时按“在用”处理：本次沿用旧库不会丢数据，下次启动再迁移
            Trace.TraceWarning($"检测旧数据库占用失败: {ex.Message}");
            return true;
        }
    }

    private static string LegacyInUsePath(string legacyDbPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(legacyDbPath))!, LegacyInUseFileName);

    private static IDisposable AcquireMigrationGate()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: false, MigrationMutexName);
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromMinutes(5)))
                    Trace.TraceWarning("等待其他实例完成数据库迁移超时，继续启动");
                else
                    return new MutexRelease(mutex);
            }
            catch (AbandonedMutexException)
            {
                return new MutexRelease(mutex);
            }
            mutex.Dispose();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"创建数据库迁移锁失败: {ex.Message}");
        }
        return new MutexRelease(null);
    }

    private sealed class MutexRelease(Mutex? mutex) : IDisposable
    {
        public void Dispose()
        {
            if (mutex is null) return;
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    /// <summary>
    /// 选定本次使用的数据库文件。首选目录里已有数据库或迁移标记时，它就是唯一的权威数据库，
    /// 即使目录暂时不可写也不回退 —— 回退会打开迁移前留下的旧副本，造成数据分叉
    /// （写不进去时由初始化或保存报错，可诊断）。否则首选目录可写用首选，不可写用回退目录。
    /// </summary>
    public static string ResolveDatabasePath(string preferredDir, string fallbackDir)
    {
        var preferred = Path.Combine(preferredDir, FileName);
        if (File.Exists(preferred) || File.Exists(Path.Combine(preferredDir, MigratedMarkerFileName)))
            return preferred;
        return Path.Combine(ResolveDirectory(preferredDir, fallbackDir), FileName);
    }

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
    /// 返回 true 表示本次完成了迁移；无需迁移、或同时启动的另一个实例已先完成迁移时返回 false；
    /// 复制失败抛异常，目标位置不留半成品。
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
        // 允许多开：每次迁移用独立的临时文件，谁先改名谁完成迁移
        var temp = $"{targetDbPath}.migrating-{Guid.NewGuid():N}";
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
        catch (IOException) when (File.Exists(targetDbPath))
        {
            // 另一个实例抢先把完整副本改名到位（它也是先写临时文件再改名），直接用它
            DeleteIfExists(temp);
            return false;
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
