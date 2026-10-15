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

    /// <summary>
    /// 串行化各实例的“迁移还是沿用旧库”判断，避免同时首启时一个迁移、一个沿用旧库。
    /// 用 Global\ 前缀：不带前缀的命名 mutex 只在当前 Windows 会话内可见，同一用户在远程桌面
    /// 和本地控制台同时启动时会各自迁移。
    /// </summary>
    private const string MigrationMutexName = @"Global\MagnetometerSystem.DatabaseMigration";

    private static readonly object HeldLocksGate = new();
    private static readonly Dictionary<string, FileStream> HeldLocks = new(StringComparer.OrdinalIgnoreCase);

    public enum MigrationOutcome
    {
        /// <summary>无需迁移：新位置已有数据库、已迁移过，或旧位置没有数据库。</summary>
        NotNeeded,
        /// <summary>本次完成了迁移。</summary>
        Migrated,
        /// <summary>另有实例正在使用旧数据库，本次不迁移、沿用旧库，避免复制后旧库还有人写入。</summary>
        DeferredLegacyInUse,
        /// <summary>新位置还没有数据库且不可写，本次使用旧位置的数据库。</summary>
        FallbackNotWritable
    }

    /// <summary>本次启动的位置决定：结果类别和实际使用的数据库文件。</summary>
    public readonly record struct MigrationResult(MigrationOutcome Outcome, string DatabasePath);

    /// <summary>
    /// 在全局迁移锁内决定本次用哪个库：新位置已有库就用新库；旧库还有其他实例在用
    /// （<paramref name="anotherInstanceRunning"/> 覆盖不认识占用文件的旧版本程序，占用文件覆盖新版本中回退到旧库的实例）
    /// 就沿用旧库并登记占用；否则迁移。<paramref name="fallbackWhenNotWritable"/> 为 true 时，
    /// “新位置不可写而回退旧库”也在锁内判断并登记占用，不会绕过正在进行的迁移。
    /// 复制失败时在锁内登记旧库占用后抛异常，调用方沿用旧库。
    /// </summary>
    public static MigrationResult MigrateOrDefer(string legacyDbPath, string targetDbPath, bool anotherInstanceRunning,
        bool fallbackWhenNotWritable = false)
    {
        using var gate = AcquireMigrationGate();

        var targetDir = Path.GetDirectoryName(targetDbPath)!;
        if (fallbackWhenNotWritable)
        {
            // 锁内选位置：别的实例刚迁移完时这里能看到新库并直接使用，不会回退到迁移前的旧副本
            var chosen = ResolveDatabasePath(targetDir, Path.GetDirectoryName(legacyDbPath)!);
            if (!PathsEqual(chosen, targetDbPath))
            {
                HoldLegacyInUse(chosen);
                return new(MigrationOutcome.FallbackNotWritable, chosen);
            }
        }

        if (PathsEqual(legacyDbPath, targetDbPath))
        {
            HoldLegacyInUse(legacyDbPath);
            return new(MigrationOutcome.NotNeeded, targetDbPath);
        }

        var marker = Path.Combine(targetDir, MigratedMarkerFileName);
        var targetState = ProbeFile(targetDbPath);
        if (targetState != FileState.Absent)
        {
            // 新库已是权威数据库：补写此前可能写失败的标记，之后删掉新库也不会再复制旧副本。
            // 新位置无法检查（权限、I/O 错误）时同样不迁移、不回退，由初始化打开时报错，避免写进旧副本。
            if (targetState == FileState.Exists && ProbeFile(legacyDbPath) != FileState.Absent && ProbeFile(marker) == FileState.Absent)
                TryWriteMarker(marker, $"本目录已有数据库，不再从 {legacyDbPath} 复制旧副本。");
            return new(MigrationOutcome.NotNeeded, targetDbPath);
        }
        if (ProbeFile(marker) != FileState.Absent)
            return new(MigrationOutcome.NotNeeded, targetDbPath);
        switch (ProbeFile(legacyDbPath))
        {
            case FileState.Absent:
                return new(MigrationOutcome.NotNeeded, targetDbPath);
            case FileState.Unknown:
                // 旧库可能存在但暂时无法检查：不能当作全新安装在新位置建空库（之后就再也不会迁移）
                HoldLegacyInUse(legacyDbPath);
                throw new IOException($"无法检查旧数据库 {legacyDbPath}，本次不迁移");
        }

        // 拿不到迁移锁就不能保证没有别的实例正在迁移：本次不迁移，沿用旧库
        if (gate is null || anotherInstanceRunning || IsLegacyInUse(legacyDbPath))
        {
            HoldLegacyInUse(legacyDbPath);
            return new(MigrationOutcome.DeferredLegacyInUse, legacyDbPath);
        }

        try
        {
            return MigrateIfNeeded(legacyDbPath, targetDbPath)
                ? new(MigrationOutcome.Migrated, targetDbPath)
                : new(MigrationOutcome.NotNeeded, targetDbPath);
        }
        catch
        {
            // 释放迁移锁之前登记：之后启动的实例能看到旧库仍在使用而推迟迁移
            HoldLegacyInUse(legacyDbPath);
            throw;
        }
    }

    /// <summary>登记本进程正在使用旧数据库，直到进程退出。登记失败抛异常，调用方不得继续使用旧库。</summary>
    public static void HoldLegacyInUse(string legacyDbPath)
    {
        var path = LegacyInUsePath(legacyDbPath);
        lock (HeldLocksGate)
        {
            if (HeldLocks.ContainsKey(path)) return;
            try
            {
                // 全新安装回退到旧位置时目录可能还不存在，先建好，否则登记会失败、后来的实例看不到占用
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                HeldLocks[path] = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception ex)
            {
                // 登记不上就不能用旧库：否则别的实例会以为旧库没人用而迁移，之后写入的数据在新库里看不到
                throw new IOException($"登记旧数据库占用失败 {path}，本次不使用旧数据库: {ex.Message}", ex);
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

    /// <summary>
    /// 获取跨进程迁移锁。持锁实例可能正在复制很大的数据库，因此无限等待；
    /// 创建失败返回 null，调用方据此放弃迁移。
    /// </summary>
    private static MutexRelease? AcquireMigrationGate()
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MigrationMutexName);
            try
            {
                mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // 上一个持锁进程异常退出；锁已归本进程，迁移本身是“临时文件 + 改名”，不留半成品
            }
            return new MutexRelease(mutex);
        }
        catch (Exception ex)
        {
            mutex?.Dispose();
            Trace.TraceWarning($"获取数据库迁移锁失败，本次不迁移: {ex.Message}");
            return null;
        }
    }

    private sealed class MutexRelease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
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
        // 只有确认首选目录里没有数据库和标记时才考虑回退；无法检查（权限、I/O 错误）时不回退，
        // File.Exists 在这种情况下也返回 false，会误选迁移前的旧副本
        if (ProbeFile(preferred) != FileState.Absent || ProbeFile(Path.Combine(preferredDir, MigratedMarkerFileName)) != FileState.Absent)
            return preferred;
        return Path.Combine(ResolveDirectory(preferredDir, fallbackDir), FileName);
    }

    private enum FileState { Absent, Exists, Unknown }

    /// <summary>区分“确认不存在”和“无法检查”。同名目录不算文件。</summary>
    private static FileState ProbeFile(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return FileState.Exists;
        }
        catch (FileNotFoundException) { return FileState.Absent; }
        catch (DirectoryNotFoundException) { return FileState.Absent; }
        catch (UnauthorizedAccessException) when (Directory.Exists(path)) { return FileState.Absent; }
        catch (Exception ex)
        {
            Trace.TraceWarning($"无法检查 {path}: {ex.Message}");
            return FileState.Unknown;
        }
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
    /// 返回 true 表示本次完成了迁移；无需迁移返回 false；复制失败（包括目标位置已被别的文件占用）
    /// 抛异常，目标位置不留半成品。多实例之间由 <see cref="MigrateOrDefer"/> 的迁移锁串行。
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
        // 每次迁移用独立的临时文件，异常退出残留的旧临时文件不会干扰下一次
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
        catch
        {
            DeleteIfExists(temp);
            throw;
        }

        // 数据库已完整就位，迁移即告成功。标记只用于“删掉新库后不再重复复制”，写失败不能让本次
        // 回退到旧库 —— 否则本次写进旧库的数据下次启动会被已存在的新库挡住而“消失”。
        TryWriteMarker(marker,
            $"数据库已于 {DateTime.Now:yyyy-MM-dd HH:mm:ss} 从 {legacyDbPath} 复制到本目录。{Environment.NewLine}" +
            "旧文件仍保留在原处，确认新位置数据完整后可自行删除。");
        TryWriteLegacyNote(legacyDbPath, targetDbPath);
        return true;
    }

    /// <summary>写迁移标记；失败只记日志（数据库已就位），下次启动会补写。</summary>
    private static void TryWriteMarker(string marker, string text)
    {
        try
        {
            File.WriteAllText(marker, text + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"写入迁移标记失败（数据库已迁移，不影响使用，下次启动补写）: {ex.Message}");
        }
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
