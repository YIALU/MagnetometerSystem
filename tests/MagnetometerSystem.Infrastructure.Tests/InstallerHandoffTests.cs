using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using MagnetometerSystem.Infrastructure.Update;

namespace MagnetometerSystem.Infrastructure.Tests;

[SupportedOSPlatform("windows")]
public class InstallerHandoffTests
{
    [WindowsFact]
    public async Task InstallerStartsOnlyAfterOwnerProcessAndItsMutexAreGone()
    {
        await using var fixture = await ProcessFixture.CreateAsync();
        using var waiter = InstallerHandoff.Start(InstallerHandoff.PowerShellPath,
            fixture.InstallerArguments, fixture.Directory, fixture.Owner);
        await Task.Delay(200);
        Assert.False(fixture.Owner.HasExited);
        Assert.False(File.Exists(fixture.InstallerMarker));
        using (var held = Mutex.OpenExisting(fixture.MutexName)) Assert.NotNull(held);
        fixture.ReleaseOwner.Set();
        await fixture.Owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await waiter.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, waiter.ExitCode);
        await WaitForAsync(() => File.Exists(fixture.InstallerMarker));
        Assert.Equal("owner=exited;mutex=gone", await File.ReadAllTextAsync(fixture.InstallerMarker));
    }

    [WindowsFact]
    public async Task OwnerExitTimeoutDoesNotLaunchInstallerOrReleaseOwnerMutex()
    {
        await using var fixture = await ProcessFixture.CreateAsync();
        using var waiter = InstallerHandoff.Start(InstallerHandoff.PowerShellPath,
            fixture.InstallerArguments, fixture.Directory, fixture.Owner, ownerExitTimeoutMs: 250);
        await waiter.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(0, waiter.ExitCode);
        Assert.False(fixture.Owner.HasExited);
        Assert.False(File.Exists(fixture.InstallerMarker));
        using (var held = Mutex.OpenExisting(fixture.MutexName)) Assert.NotNull(held);
        Assert.Contains("Timed out", await File.ReadAllTextAsync(Path.Combine(fixture.Directory, InstallerHandoff.LogFileName)));
    }

    [WindowsFact]
    public async Task OwnerAlreadyExitedCannotAcknowledgeHandoff()
    {
        await using var fixture = await ProcessFixture.CreateAsync();
        _ = fixture.Owner.StartTime; // Cache identity before exit so the helper performs the failing identity check.
        fixture.ReleaseOwner.Set();
        await fixture.Owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Throws<IOException>(() => InstallerHandoff.Start(InstallerHandoff.PowerShellPath,
            fixture.InstallerArguments, fixture.Directory, fixture.Owner));
        Assert.False(File.Exists(fixture.InstallerMarker));
    }

    private static string Data(string value) =>
        "[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "'))";

    private static string Arguments(string script) => "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " +
        Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition(), "Expected child process marker was not written.");
    }

    private sealed class ProcessFixture : IAsyncDisposable
    {
        public required string Directory { get; init; }
        public required string MutexName { get; init; }
        public required string InstallerMarker { get; init; }
        public required EventWaitHandle ReleaseOwner { get; init; }
        public required Process Owner { get; init; }
        public required string InstallerArguments { get; init; }

        public static async Task<ProcessFixture> CreateAsync()
        {
            string id = Guid.NewGuid().ToString("N");
            string directory = Path.Combine(Path.GetTempPath(), "handoff_中文_'_" + id);
            System.IO.Directory.CreateDirectory(directory);
            string mutexName = @"Local\MagnetometerSystem.HandoffTest." + id;
            string releaseName = @"Local\MagnetometerSystem.HandoffRelease." + id;
            string parentReady = Path.Combine(directory, "owner-ready");
            string marker = Path.Combine(directory, "installer-marker");
            var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
            string ownerScript = $$"""
                $ErrorActionPreference = 'Stop'
                $mutex = New-Object Threading.Mutex($false, {{Data(mutexName)}})
                $release = [Threading.EventWaitHandle]::OpenExisting({{Data(releaseName)}})
                [IO.File]::WriteAllText({{Data(parentReady)}}, 'ready')
                $null = $release.WaitOne()
                # Let process termination dispose the mutex; do not release it before exit.
                """;
            var owner = Process.Start(new ProcessStartInfo(InstallerHandoff.PowerShellPath)
            {
                Arguments = Arguments(ownerScript), UseShellExecute = false,
                CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            try
            {
                await WaitForAsync(() => File.Exists(parentReady));
                string installerScript = $$"""
                    $ErrorActionPreference = 'Stop'
                    $state = 'running'
                    try { $p = [Diagnostics.Process]::GetProcessById({{owner.Id}}); $p.Dispose() }
                    catch { $state = 'exited' }
                    $held = $null
                    $mutexState = 'gone'
                    if ([Threading.Mutex]::TryOpenExisting({{Data(mutexName)}}, [ref]$held)) {
                        $mutexState = 'present'
                        $held.Dispose()
                    }
                    [IO.File]::WriteAllText({{Data(marker + ".tmp")}}, "owner=$state;mutex=$mutexState")
                    [IO.File]::Move({{Data(marker + ".tmp")}}, {{Data(marker)}})
                    """;
                return new ProcessFixture
                {
                    Directory = directory, MutexName = mutexName, InstallerMarker = marker,
                    ReleaseOwner = release, Owner = owner, InstallerArguments = Arguments(installerScript),
                };
            }
            catch
            {
                release.Set();
                if (!owner.WaitForExit(3000)) owner.Kill();
                owner.Dispose();
                release.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            ReleaseOwner.Set();
            if (!Owner.HasExited) await Owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Owner.Dispose();
            ReleaseOwner.Dispose();
            // Windows may briefly retain a terminating process's current-directory handle.
            for (int attempt = 0; ; attempt++)
            {
                try { System.IO.Directory.Delete(Directory, recursive: true); break; }
                catch (IOException) when (attempt < 20) { await Task.Delay(50); }
            }
        }
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Requires Windows PowerShell and Windows named process/mutex semantics.";
    }
}
