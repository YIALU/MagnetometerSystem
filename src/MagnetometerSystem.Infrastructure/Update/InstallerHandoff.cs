using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace MagnetometerSystem.Infrastructure.Update;

/// <summary>
/// Starts a hidden waiter, then hands off only after the application process has terminated.
/// Inno Setup checks AppMutex on startup, even with /SILENT; releasing the mutex early is unsafe.
/// https://jrsoftware.org/ishelp/topic_setup_appmutex.htm
/// </summary>
[SupportedOSPlatform("windows")]
internal static class InstallerHandoff
{
    internal const string LogFileName = "update-handoff.log";
    internal static string PowerShellPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    internal static Process Start(string executable, string arguments, string workingDirectory, Process owner,
        int ownerExitTimeoutMs = 120_000, int readyTimeoutMs = 10_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ownerExitTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(readyTimeoutMs);
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("更新包不存在", executable);
        workingDirectory = Path.GetFullPath(workingDirectory);
        string logPath = Path.Combine(workingDirectory, LogFileName);
        string readyName = @"Local\MagnetometerSystem.UpdateReady." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, readyName);
        string script = BuildScript(executable, arguments, workingDirectory, logPath, owner.Id,
            owner.StartTime.ToUniversalTime().Ticks, readyName, ownerExitTimeoutMs);
        var start = new ProcessStartInfo(PowerShellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        var waiter = Process.Start(start) ?? throw new IOException("无法启动更新交接进程。");
        try
        {
            var elapsed = Stopwatch.StartNew();
            while (!ready.WaitOne(50))
            {
                if (waiter.HasExited)
                    throw new IOException($"更新交接进程启动失败（退出码 {waiter.ExitCode}）。请查看 {logPath}");
                if (elapsed.ElapsedMilliseconds >= readyTimeoutMs)
                    throw new TimeoutException("更新交接进程未就绪，应用将保持运行；请稍后重试。");
            }
            return waiter;
        }
        catch
        {
            if (!waiter.HasExited) waiter.Kill();
            waiter.WaitForExit(5000);
            waiter.Dispose();
            throw;
        }
    }

    private static string Data(string value) =>
        "[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "'))";

    private static string BuildScript(string executable, string arguments, string workingDirectory, string logPath,
        int ownerId, long ownerStartedTicks, string readyName, int ownerExitTimeoutMs) => $$"""
        $ErrorActionPreference = 'Stop'
        $ownerProcess = $null
        $readyEvent = $null
        $exitCode = 0
        try {
            $ownerProcess = [Diagnostics.Process]::GetProcessById({{ownerId.ToString(CultureInfo.InvariantCulture)}})
            if ($ownerProcess.StartTime.ToUniversalTime().Ticks -ne [Int64]::Parse('{{ownerStartedTicks.ToString(CultureInfo.InvariantCulture)}}')) {
                throw 'The application process identity changed; the installer was not launched.'
            }
            # Hold the real process handle before signalling readiness. No PID polling or early mutex release.
            $null = $ownerProcess.Handle
            $readyEvent = [Threading.EventWaitHandle]::OpenExisting({{Data(readyName)}})
            $null = $readyEvent.Set()
            if (-not $ownerProcess.WaitForExit({{ownerExitTimeoutMs.ToString(CultureInfo.InvariantCulture)}})) {
                throw 'Timed out waiting for the application to exit; the installer was not launched.'
            }
            $installer = New-Object Diagnostics.ProcessStartInfo
            $installer.FileName = {{Data(executable)}}
            $installer.Arguments = {{Data(arguments)}}
            $installer.WorkingDirectory = {{Data(workingDirectory)}}
            $installer.UseShellExecute = $true
            $launched = [Diagnostics.Process]::Start($installer)
            if ($null -eq $launched) { throw 'The installer process could not be started.' }
            $launched.Dispose()
        }
        catch {
            $exitCode = 1
            try { [IO.File]::AppendAllText({{Data(logPath)}}, [DateTime]::UtcNow.ToString('o') + ' ' + $_.Exception.ToString() + [Environment]::NewLine) } catch { }
        }
        finally {
            if ($null -ne $readyEvent) { $readyEvent.Dispose() }
            if ($null -ne $ownerProcess) { $ownerProcess.Dispose() }
        }
        exit $exitCode
        """;
}
