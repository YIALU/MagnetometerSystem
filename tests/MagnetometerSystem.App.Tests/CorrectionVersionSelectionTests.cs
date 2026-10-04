using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;
using MagnetometerSystem.Infrastructure.Export;

namespace MagnetometerSystem.App.Tests;

public class CorrectionVersionSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SwitchingSessions_IgnoresLateVersionResultsAndErrors(bool fails) => WpfTestHost.RunAsync(async () =>
    {
        var storage = new DelayedVersionStorage();
        var vm = CreateViewModel(storage);
        vm.SelectedSession = new SessionInfo { Id = "session-a" };
        var previous = Assert.Single(storage.Requests);
        vm.SelectedSession = new SessionInfo { Id = "session-b" };
        Assert.Empty(vm.AvailableCorrectionVersions);
        Assert.Null(vm.SelectedCorrectionVersion);

        var current = storage.Requests[1];
        Assert.Equal("session-b", current.SessionId);
        current.Complete("saved-version-b");
        await WpfTestHost.PumpAsync();
        var selected = Assert.Single(vm.AvailableCorrectionVersions);
        Assert.Equal("saved-version-b", selected.Id);
        Assert.Same(selected, vm.SelectedCorrectionVersion);

        if (fails) previous.Completion.SetException(new InvalidOperationException("old session failed"));
        else previous.Complete("saved-version-a");
        await WpfTestHost.PumpAsync();

        Assert.Equal("session-b", vm.SelectedSession.Id);
        Assert.Same(selected, Assert.Single(vm.AvailableCorrectionVersions));
        Assert.Same(selected, vm.SelectedCorrectionVersion);
        Assert.Equal("", vm.CorrectionVersionsStatus);
    });

    [Fact]
    public Task ClearingSession_ClearsSelectedVersionAndInvalidatesPendingRefresh() => WpfTestHost.RunAsync(async () =>
    {
        var storage = new DelayedVersionStorage();
        var vm = CreateViewModel(storage);
        vm.SelectedSession = new SessionInfo { Id = "session-a" };
        storage.Requests[0].Complete("saved-version-a");
        await WpfTestHost.PumpAsync();
        Assert.NotNull(vm.SelectedCorrectionVersion);
        var refresh = vm.RefreshCorrectionVersionsAsync();
        var pending = storage.Requests[1];

        vm.SelectedSession = null;
        Assert.Empty(vm.AvailableCorrectionVersions);
        Assert.Null(vm.SelectedCorrectionVersion);
        Assert.Equal("", vm.CorrectionVersionsStatus);
        pending.Complete("late-version-a");
        await refresh;

        Assert.Null(vm.SelectedSession);
        Assert.Empty(vm.AvailableCorrectionVersions);
        Assert.Null(vm.SelectedCorrectionVersion);
        Assert.Equal("", vm.CorrectionVersionsStatus);
    });

    [Fact]
    public Task RefreshingSameSession_PreservesExplicitSelectionAndOnlyLatestRequestWins() => WpfTestHost.RunAsync(async () =>
    {
        var storage = new DelayedVersionStorage();
        var vm = CreateViewModel(storage);
        vm.SelectedSession = new SessionInfo { Id = "session-a" };
        storage.Requests[0].Complete("version-a", "version-z");
        await WpfTestHost.PumpAsync();
        Assert.Equal(new[] { "version-a", "version-z" }, vm.AvailableCorrectionVersions.Select(v => v.Id));
        Assert.Null(vm.SelectedCorrectionVersion); // Multiple saved versions require an explicit choice.
        vm.SelectedCorrectionVersion = vm.AvailableCorrectionVersions.Single(v => v.Id == "version-z");

        var olderRefresh = vm.RefreshCorrectionVersionsAsync();
        var newerRefresh = vm.RefreshCorrectionVersionsAsync();
        storage.Requests[2].Complete("version-new", "version-z");
        await newerRefresh;
        Assert.Equal("version-z", vm.SelectedCorrectionVersion?.Id);
        storage.Requests[1].Complete("stale-version");
        await olderRefresh;

        Assert.Equal(new[] { "version-new", "version-z" }, vm.AvailableCorrectionVersions.Select(v => v.Id));
        Assert.Equal("version-z", vm.SelectedCorrectionVersion?.Id);
        var finalRefresh = vm.RefreshCorrectionVersionsAsync();
        storage.Requests[3].Complete("only-remaining-version");
        await finalRefresh;
        Assert.Same(Assert.Single(vm.AvailableCorrectionVersions), vm.SelectedCorrectionVersion);
        Assert.Equal("only-remaining-version", vm.SelectedCorrectionVersion?.Id);
    });

    [Fact]
    public Task SavedLegacyIdsRemainSelectableWithoutRequiringExistingCalibrationProfiles() => WpfTestHost.RunAsync(async () =>
    {
        const string legacyId = "deleted-profile-id";
        var version = new OrthogonalityCorrectionSnapshot(
            new OrthogonalityParams { Id = "first/profile" }, new OrthogonalityParams { Id = "second|profile" },
            new[] { 1, 2, 3 }, new[] { 4, 5, 6 }).VersionId;
        var storage = new DelayedVersionStorage();
        var vm = CreateViewModel(storage);
        vm.SelectedSession = new SessionInfo { Id = "session-a" };
        storage.Requests[0].Complete(legacyId, version);
        await WpfTestHost.PumpAsync();

        var legacy = vm.AvailableCorrectionVersions.Single(v => v.Id == legacyId);
        Assert.Equal($"旧配置：{legacyId}", legacy.DisplayName);
        vm.SelectedCorrectionVersion = legacy;
        Assert.Equal(legacyId, vm.SelectedCorrectionVersion.Id);
        var saved = vm.AvailableCorrectionVersions.Single(v => v.Id == version);
        Assert.Contains("first/profile[1,2,3]", saved.DisplayName);
        Assert.Contains("第二组 second|profile[4,5,6]", saved.DisplayName);
        Assert.Equal(version, saved.Id); // Human-readable display never replaces the persisted version key.
        Assert.Empty(vm.AvailableProfiles);
    });

    private static SessionListViewModel CreateViewModel(DelayedVersionStorage storage) => new(
        storage, new CsvExporter(storage), new DataBus(), new OrthogonalityCorrector(), new UnusedCalibrationRepository());

    private sealed class VersionRequest(string sessionId)
    {
        public string SessionId { get; } = sessionId;
        public TaskCompletionSource<IReadOnlyList<string>> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(params string[] versions) => Completion.SetResult(versions);
    }

    private sealed class DelayedVersionStorage : IDataStorageService
    {
        public List<VersionRequest> Requests { get; } = [];
        public StorageWriteStatus WriteStatus { get; } = new(0, 0, null);
        public event Action<StorageWriteStatus>? WriteStatusChanged { add { } remove { } }
        public Task<IReadOnlyList<string>> GetCorrectionVersionIdsAsync(string sessionId)
        {
            var request = new VersionRequest(sessionId);
            Requests.Add(request);
            return request.Completion.Task;
        }

        public Task<string> StartSessionAsync(string name, SensorConfig config, ConnectionConfig connection) => throw new NotSupportedException();
        public Task EndSessionAsync(string id) => throw new NotSupportedException();
        public Task SaveReadingsAsync(IEnumerable<MagnetometerReading> readings) => throw new NotSupportedException();
        public Task WaitForPendingWritesAsync(int timeoutMs = 5000) => throw new NotSupportedException();
        public Task RetryPendingWritesAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInfo>> GetSessionsAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<MagnetometerReading>> GetReadingsAsync(string id, DateTime? startTime = null, DateTime? endTime = null) => throw new NotSupportedException();
        public Task DeleteSessionAsync(string id) => throw new NotSupportedException();
        public Task UpdateSessionAsync(string id, string name, string? notes) => throw new NotSupportedException();
        public Task SaveCorrectedReadingsAsync(IEnumerable<CorrectedReading> readings) => throw new NotSupportedException();
        public Task<IReadOnlyList<CorrectedReading>> GetCorrectedReadingsAsync(string id, string? profile = null) => throw new NotSupportedException("Version selection must query only saved version IDs.");
        public Task DeleteCorrectedReadingsAsync(string id, string? profile = null) => throw new NotSupportedException();
        public Task<bool> HasCorrectedReadingsAsync(string id) => throw new NotSupportedException();
    }

    private sealed class UnusedCalibrationRepository : ICalibrationRepository
    {
        public Task SaveOrthogonalityProfileAsync(OrthogonalityParams profile) => throw new NotSupportedException();
        public Task<IReadOnlyList<OrthogonalityParams>> GetOrthogonalityProfilesAsync(string? sensorSerial = null) => throw new NotSupportedException();
        public Task<OrthogonalityParams?> GetOrthogonalityProfileAsync(string id) => throw new NotSupportedException();
        public Task DeleteOrthogonalityProfileAsync(string id) => throw new NotSupportedException();
        public Task SaveCalibrationProfileAsync(CalibrationParams profile) => throw new NotSupportedException();
        public Task<IReadOnlyList<CalibrationParams>> GetCalibrationProfilesAsync(SensorType? sensorType = null) => throw new NotSupportedException();
        public Task DeleteCalibrationProfileAsync(string id) => throw new NotSupportedException();
        public Task<int> SaveOrthogonalityCalibrationAsync(OrthogonalityCalibrationRecord record) => throw new NotSupportedException();
        public Task<List<OrthogonalityCalibrationRecord>> GetOrthogonalityHistoryAsync(string deviceId) => throw new NotSupportedException();
    }
}
