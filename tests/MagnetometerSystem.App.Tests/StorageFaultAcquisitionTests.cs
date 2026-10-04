using System.IO;
using System.Reflection;
using System.Text;
using Dapper;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

public class StorageFaultAcquisitionTests
{
    [Fact]
    public Task FirstSqliteFailureStopsProducerWhileDispatcherAndDisconnectAreBlocked() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        int faultCount = 0;
        int accepted = 0;
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Bus.AcquisitionFaulted += _ => { Interlocked.Increment(ref faultCount); fault.TrySetResult(); };
        fixture.Bus.ReadingReceived += _ => Interlocked.Increment(ref accepted);
        await fixture.ConnectionVm.ConnectCommand.ExecuteAsync(null);
        await fixture.ExecuteAsync("CREATE TRIGGER fail_live_save BEFORE INSERT ON readings BEGIN SELECT RAISE(FAIL, 'device disk failure'); END;");
        try
        {
            // Deliberately block the STA dispatcher. Production must stop independently of UI continuations.
#pragma warning disable xUnit1031 // Blocking the dedicated STA is the behavior under test, with a bounded timeout.
            Task.Run(async () =>
            {
                using (var blocker = new SqliteConnection(fixture.Database.ConnectionString))
                {
                    await blocker.OpenAsync();
                    using var transaction = blocker.BeginTransaction();
                    fixture.Transport.Feed("1,2,3\n4,5,6\n");
                    Assert.Equal(2, Volatile.Read(ref accepted));
                    transaction.Rollback();
                }
                await fault.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await fixture.Transport.DisconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(fixture.Transport.IsConnected); // Physical disconnect is intentionally still waiting.
                long pending = fixture.Storage.WriteStatus.PendingReadings;
                long bufferedAndAccepted = fixture.Sessions.ActiveSessionReadingCount;
                byte[] packet = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("100,200,300\n", 100)));
                for (int i = 0; i < 1000; i++) fixture.Transport.Feed(packet);
                Assert.Equal(2, Volatile.Read(ref accepted));
                Assert.Equal(bufferedAndAccepted, fixture.Sessions.ActiveSessionReadingCount);
                Assert.Equal(pending, fixture.Storage.WriteStatus.PendingReadings);
                Assert.Equal(1, Volatile.Read(ref faultCount));
            }).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
#pragma warning restore xUnit1031

            fixture.Transport.ReleaseDisconnect.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() => fixture.ConnectionVm.StopAcquisitionAsync());
            Assert.False(fixture.Transport.IsConnected);
            Assert.Null(fixture.Bus.CurrentConnection);
            Assert.Equal(2, fixture.Storage.WriteStatus.PendingReadings);
            Assert.NotNull(fixture.Sessions.ActiveSessionId);
            Assert.Null(Assert.Single(await fixture.Storage.GetSessionsAsync()).EndedAt);
            Assert.Equal(1, faultCount); // Fault handling must not automatically retry the failed database write.
            await fixture.ExecuteAsync("DROP TRIGGER fail_live_save;");
            await fixture.Sessions.RetryStorageCommand.ExecuteAsync(null);
            await fixture.ConnectionVm.StopAcquisitionAsync();
            var session = Assert.Single(await fixture.Storage.GetSessionsAsync());
            Assert.NotNull(session.EndedAt);
            Assert.Equal(2, session.TotalReadings);
            Assert.Equal(new[] { 1d, 4d }, (await fixture.Storage.GetReadingsAsync(session.Id)).Select(r => r.ChannelValues[0]));
            Assert.Equal(0, fixture.Storage.WriteStatus.PendingReadings);
            Assert.Null(fixture.Sessions.ActiveSessionId);
        }
        finally
        {
            fixture.Transport.ReleaseDisconnect.TrySetResult();
            await fixture.ExecuteAsync("DROP TRIGGER IF EXISTS fail_live_save;");
            await fixture.Sessions.RetryStorageCommand.ExecuteAsync(null);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StorageRetryCompletesStopOnceAndNextClickStartsNewSession(bool retryDuringDisconnect) => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        int stopped = 0;
        fixture.Bus.AcquisitionStopped += () => stopped++;
        await fixture.ConnectionVm.ConnectCommand.ExecuteAsync(null);
        string originalSession = fixture.Sessions.ActiveSessionId!;
        await fixture.ExecuteAsync("CREATE TRIGGER fail_recovery_save BEFORE INSERT ON readings BEGIN SELECT RAISE(FAIL, 'retry still fails'); END;");
        try
        {
            fixture.Transport.Feed("1,2,3\n");
            await fixture.Transport.DisconnectEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (!retryDuringDisconnect)
            {
                fixture.Transport.ReleaseDisconnect.TrySetResult();
                await Assert.ThrowsAsync<IOException>(() => fixture.ConnectionVm.StopAcquisitionAsync());
            }

            await fixture.Sessions.RetryStorageCommand.ExecuteAsync(null);
            await WpfTestHost.PumpAsync();
            Assert.Equal(0, stopped);
            Assert.True(fixture.ConnectionVm.IsAcquiring);
            Assert.Equal(originalSession, fixture.Sessions.ActiveSessionId);
            Assert.NotNull(fixture.Sessions.StorageError);
            Assert.Equal(1, fixture.Storage.WriteStatus.PendingReadings);
            Assert.Null(Assert.Single(await fixture.Storage.GetSessionsAsync()).EndedAt);
            Assert.DoesNotContain("会话已保存", fixture.ConnectionVm.StatusMessage);

            await fixture.ExecuteAsync("DROP TRIGGER fail_recovery_save;");
            var retry = fixture.Sessions.RetryStorageCommand.ExecuteAsync(null);
            if (retryDuringDisconnect)
            {
                // Fault stop owns the connection gate while physical disconnect waits.
                // Recovery must release the session gate before waiting for that owner.
                await Assert.ThrowsAsync<TimeoutException>(() => retry.WaitAsync(TimeSpan.FromMilliseconds(100)));
                fixture.Transport.ReleaseDisconnect.TrySetResult();
            }
            await retry.WaitAsync(TimeSpan.FromSeconds(3));
            await WpfTestHost.PumpAsync();
            Assert.Equal(1, stopped);
            Assert.False(fixture.ConnectionVm.IsAcquiring);
            Assert.False(fixture.ConnectionVm.IsConnected);
            Assert.Null(fixture.Bus.CurrentConnection);
            Assert.Null(fixture.Sessions.ActiveSessionId);
            Assert.Null(fixture.Sessions.StorageError);
            Assert.Empty(fixture.ConnectionVm.LastError);
            Assert.Contains("会话已保存", fixture.ConnectionVm.StatusMessage);
            Assert.Equal(1, Assert.Single(await fixture.Storage.GetSessionsAsync()).TotalReadings);
            Assert.NotNull(Assert.Single(await fixture.Storage.GetSessionsAsync()).EndedAt);

            await fixture.Bus.PublishAcquisitionRecoveryCompletedAsync(originalSession);
            Assert.Equal(1, stopped); // Repeated completion is idempotent.
            await fixture.ConnectionVm.ConnectCommand.ExecuteAsync(null); // Exactly one click.
            string nextSession = fixture.Sessions.ActiveSessionId!;
            Assert.NotNull(nextSession);
            Assert.NotEqual(originalSession, nextSession);
            Assert.True(fixture.ConnectionVm.IsConnected);
            Assert.True(fixture.ConnectionVm.IsAcquiring);
            await fixture.Bus.PublishAcquisitionRecoveryCompletedAsync(originalSession);
            Assert.Equal(1, stopped); // A stale session cannot stop the new acquisition.
            Assert.True(fixture.ConnectionVm.IsAcquiring);
            fixture.Transport.Feed("7,8,9\n");
            await fixture.ConnectionVm.StopAcquisitionAsync();
            Assert.Equal(2, stopped);
            Assert.Equal(7, Assert.Single(await fixture.Storage.GetReadingsAsync(nextSession)).ChannelValues[0]);
            Assert.Equal(1, Assert.Single(await fixture.Storage.GetReadingsAsync(originalSession)).ChannelValues[0]);
        }
        finally
        {
            fixture.Transport.ReleaseDisconnect.TrySetResult();
            await fixture.ExecuteAsync("DROP TRIGGER IF EXISTS fail_recovery_save;");
            await fixture.Sessions.RetryStorageCommand.ExecuteAsync(null);
        }
    });

    [Fact]
    public Task FaultRaisedDuringOnePacketStopsItsRemainingFramesAndPreservesAcceptedTail() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        int accepted = 0;
        fixture.Bus.ReadingReceived += _ =>
        {
            if (++accepted == 3) fixture.Bus.PublishAcquisitionFault(new IOException("consumer cannot save"));
        };
        await fixture.ConnectionVm.ConnectCommand.ExecuteAsync(null);
        fixture.Transport.Feed(string.Concat(Enumerable.Repeat("1,2,3\n", 100)));
        Assert.Equal(3, accepted);
        Assert.Equal(3, fixture.Sessions.ActiveSessionReadingCount);
        fixture.Transport.ReleaseDisconnect.TrySetResult();
        await fixture.ConnectionVm.StopAcquisitionAsync();
        var session = Assert.Single(await fixture.Storage.GetSessionsAsync());
        Assert.Equal(3, session.TotalReadings);
        Assert.Equal(3, (await fixture.Storage.GetReadingsAsync(session.Id)).Count);
        Assert.Equal(0, fixture.Storage.WriteStatus.PendingReadings);
    });

    [Fact]
    public Task DelayedFaultWorkFromPreviousSessionCannotStopNewAcquisition() => WpfTestHost.RunAsync(async () =>
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ConnectionVm.ConnectCommand.ExecuteAsync(null);
        long connectionGeneration = (long)typeof(ConnectionViewModel)
            .GetField("_acquisitionGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.ConnectionVm)!;
        long sessionGeneration = (long)typeof(SessionListViewModel)
            .GetField("_sessionGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Sessions)!;
        var delayedWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = (Task)typeof(SessionListViewModel)
            .GetMethod("ObserveWriteAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.Sessions, new object[] { (Func<Task>)(() => delayedWrite.Task), sessionGeneration })!;
        fixture.Transport.ReleaseDisconnect.TrySetResult();
        await fixture.ConnectionVm.StopAcquisitionAsync();
        await fixture.ConnectionVm.ConnectCommand.ExecuteAsync(null);
        string? nextSession = fixture.Sessions.ActiveSessionId;
        Assert.NotNull(nextSession);
        Assert.True(fixture.Transport.IsConnected);

        // Replay captured fault work after recovery/reconnect. Reusing the transport
        // means connection identity alone is insufficient to protect the new session.
        var oldCleanup = (Task)typeof(ConnectionViewModel)
            .GetMethod("StopFaultedAcquisitionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(fixture.ConnectionVm, new object[] { fixture.Transport, connectionGeneration })!;
        await oldCleanup;
        delayedWrite.TrySetException(new IOException("late error from previous session"));
        await observer;
        await WpfTestHost.PumpAsync();
        Assert.True(fixture.Transport.IsConnected);
        Assert.True(fixture.Sessions.IsRecording);
        Assert.Equal(nextSession, fixture.Sessions.ActiveSessionId);
        Assert.Null(fixture.Sessions.StorageError);
        fixture.Transport.Feed("7,8,9\n");
        await fixture.ConnectionVm.StopAcquisitionAsync();
        Assert.Equal(7, Assert.Single(await fixture.Storage.GetReadingsAsync(nextSession!)).ChannelValues[0]);
    });

    [Fact]
    public Task FaultStopAwaitsTimerCallbackThatHasTakenTailButNotEnqueuedIt() => WpfTestHost.RunAsync(async () =>
    {
        BlockingEnqueueStorage? wrapper = null;
        await using var fixture = await Fixture.CreateAsync(storage => wrapper = new BlockingEnqueueStorage(storage));
        try
        {
            await fixture.ConnectionVm.ConnectCommand.ExecuteAsync(null);
            fixture.Transport.Feed("1,2,3\n4,5,6\n");
            await wrapper!.TailEnqueueEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // The timer has removed the second reading from the local buffer, but
            // the storage service has not accepted it yet. Empty queues aren't enough.
            fixture.Transport.ReleaseDisconnect.TrySetResult();
            fixture.Bus.PublishAcquisitionFault(new IOException("critical consumer stopped"));
            var stop = fixture.ConnectionVm.StopAcquisitionAsync();
            await Assert.ThrowsAsync<TimeoutException>(() => stop.WaitAsync(TimeSpan.FromMilliseconds(100)));
            Assert.NotNull(fixture.Sessions.ActiveSessionId);
            Assert.Null(Assert.Single(await fixture.Storage.GetSessionsAsync()).EndedAt);
            wrapper.ReleaseEnqueue.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(3));
            var session = Assert.Single(await fixture.Storage.GetSessionsAsync());
            Assert.NotNull(session.EndedAt);
            Assert.Equal(2, session.TotalReadings);
            Assert.Equal(new[] { 1d, 4d }, (await fixture.Storage.GetReadingsAsync(session.Id)).Select(r => r.ChannelValues[0]));
        }
        finally { wrapper?.ReleaseEnqueue.Set(); }
    });

    private sealed class BlockingEnqueueStorage(IDataStorageService inner) : IDataStorageService
    {
        private int _saveCalls;
        public TaskCompletionSource TailEnqueueEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseEnqueue { get; } = new(false);
        public StorageWriteStatus WriteStatus => inner.WriteStatus;
        public event Action<StorageWriteStatus>? WriteStatusChanged { add => inner.WriteStatusChanged += value; remove => inner.WriteStatusChanged -= value; }
        public Task SaveReadingsAsync(IEnumerable<MagnetometerReading> readings)
        {
            if (Interlocked.Increment(ref _saveCalls) == 2)
            {
                TailEnqueueEntered.TrySetResult();
                if (!ReleaseEnqueue.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test enqueue barrier");
            }
            return inner.SaveReadingsAsync(readings);
        }
        public Task<string> StartSessionAsync(string name, SensorConfig config, ConnectionConfig connection) => inner.StartSessionAsync(name, config, connection);
        public Task EndSessionAsync(string id) => inner.EndSessionAsync(id);
        public Task WaitForPendingWritesAsync(int timeoutMs = 5000) => inner.WaitForPendingWritesAsync(timeoutMs);
        public Task RetryPendingWritesAsync() => inner.RetryPendingWritesAsync();
        public Task<IReadOnlyList<SessionInfo>> GetSessionsAsync() => inner.GetSessionsAsync();
        public Task<IReadOnlyList<MagnetometerReading>> GetReadingsAsync(string id, DateTime? startTime = null, DateTime? endTime = null) => inner.GetReadingsAsync(id, startTime, endTime);
        public Task DeleteSessionAsync(string id) => inner.DeleteSessionAsync(id);
        public Task UpdateSessionAsync(string id, string name, string? notes) => inner.UpdateSessionAsync(id, name, notes);
        public Task SaveCorrectedReadingsAsync(IEnumerable<CorrectedReading> readings) => inner.SaveCorrectedReadingsAsync(readings);
        public Task<IReadOnlyList<CorrectedReading>> GetCorrectedReadingsAsync(string id, string? profile = null) => inner.GetCorrectedReadingsAsync(id, profile);
        public Task DeleteCorrectedReadingsAsync(string id, string? profile = null) => inner.DeleteCorrectedReadingsAsync(id, profile);
        public Task<bool> HasCorrectedReadingsAsync(string id) => inner.HasCorrectedReadingsAsync(id);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required DatabaseInitializer Database { get; init; }
        public required SqliteStorageService Storage { get; init; }
        public required DataBus Bus { get; init; }
        public required SessionListViewModel Sessions { get; init; }
        public required ConnectionViewModel ConnectionVm { get; init; }
        public required DelayedDisconnectConnection Transport { get; init; }
        private string DatabasePath { get; init; } = "";

        public static async Task<Fixture> CreateAsync(Func<SqliteStorageService, IDataStorageService>? decorateStorage = null)
        {
            string path = Path.Combine(Path.GetTempPath(), $"acquisition-fault-{Guid.NewGuid():N}.db");
            var database = new DatabaseInitializer(path);
            await database.InitializeAsync();
            var bus = new DataBus();
            var storage = new SqliteStorageService(database, bus);
            var profiles = new SqliteCalibrationRepository(database);
            var corrector = new OrthogonalityCorrector();
            var sessions = new SessionListViewModel(decorateStorage?.Invoke(storage) ?? storage, new CsvExporter(storage), bus, corrector, profiles);
            var transport = new DelayedDisconnectConnection();
            var connectionVm = new ConnectionViewModel(new Factory(transport), bus, corrector, profiles);
            return new Fixture { Database = database, DatabasePath = path, Bus = bus, Storage = storage,
                Sessions = sessions, ConnectionVm = connectionVm, Transport = transport };
        }

        public async Task ExecuteAsync(string sql)
        {
            using var connection = new SqliteConnection(Database.ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(sql);
        }

        public async ValueTask DisposeAsync()
        {
            Transport.ReleaseDisconnect.TrySetResult();
            await Sessions.RetryStorageCommand.ExecuteAsync(null);
            await ConnectionVm.StopAcquisitionAsync();
            Storage.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(DatabasePath + suffix);
        }
    }

    private sealed class Factory(DelayedDisconnectConnection transport) : IConnectionFactory
    {
        public IDeviceConnection Create(ConnectionConfig config) => transport;
    }

    private sealed class DelayedDisconnectConnection : IDeviceConnection
    {
        public event EventHandler<byte[]>? DataReceived;
        public event EventHandler<string>? ErrorOccurred { add { } remove { } }
        public event EventHandler<bool>? ConnectionStateChanged;
        public bool IsConnected { get; private set; }
        public ConnectionConfig Config { get; } = new();
        public TaskCompletionSource DisconnectEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDisconnect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ConnectAsync(CancellationToken ct = default) { IsConnected = true; ConnectionStateChanged?.Invoke(this, true); return Task.CompletedTask; }
        public async Task DisconnectAsync()
        {
            DisconnectEntered.TrySetResult();
            await ReleaseDisconnect.Task.ConfigureAwait(false);
            IsConnected = false;
            ConnectionStateChanged?.Invoke(this, false);
        }
        public Task SendAsync(byte[] data, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Feed(string text) => Feed(Encoding.ASCII.GetBytes(text));
        public void Feed(byte[] bytes) => DataReceived?.Invoke(this, bytes);
    }
}
