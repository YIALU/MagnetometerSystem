using System.IO;
using System.Text;
using MagnetometerSystem.App.Services;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Infrastructure.Export;

namespace MagnetometerSystem.App.Tests;

public class CalibrationLibraryViewModelTests
{
    private static OrthogonalityParams Profile(string name) => new()
    {
        Name = name, SensorSerial = "SN-7", Unit = "nT", Offset = [12.5, -3, 0.25],
        CompensationMatrix = [1.01, 0.002, 0, -0.001, 0.99, 0, 0, 0.003, 1], ResidualStd = 0.8, SampleCount = 48,
    };

    private static async Task<(CalibrationLibraryViewModel Vm, MemoryCalibrationRepository Repository, FakeDialogService Dialogs)> CreateAsync(
        params OrthogonalityParams[] profiles)
    {
        var repository = new MemoryCalibrationRepository(profiles);
        var dialogs = new FakeDialogService();
        var vm = new CalibrationLibraryViewModel(repository, dialogs);
        await vm.LoadSavedProfilesCommand.ExecuteAsync(null);
        return (vm, repository, dialogs);
    }

    [Fact]
    public async Task DeleteAsksFirstAndKeepsTheProfileWhenDeclined()
    {
        var (vm, repository, dialogs) = await CreateAsync(Profile("探头 A"), Profile("探头 B"));
        vm.SelectedSavedProfile = vm.SavedProfiles[0];

        await vm.DeleteSavedProfileCommand.ExecuteAsync(null);

        var (message, title) = Assert.Single(dialogs.Confirmations);
        Assert.Equal("确认删除", title);
        Assert.Contains("“探头 A”", message);
        Assert.Equal(2, vm.SavedProfiles.Count);
        Assert.Equal(2, repository.Profiles.Count);
        Assert.Empty(vm.LibraryStatus);
    }

    [Fact]
    public async Task ConfirmedDeleteRemovesTheProfileFromTheDatabaseAndTheList()
    {
        var (vm, repository, dialogs) = await CreateAsync(Profile("探头 A"), Profile("探头 B"));
        vm.SelectedSavedProfile = vm.SavedProfiles[0];
        dialogs.ConfirmAnswer = true;

        await vm.DeleteSavedProfileCommand.ExecuteAsync(null);

        Assert.Equal("探头 B", Assert.Single(vm.SavedProfiles).Name);
        Assert.Equal("探头 B", Assert.Single(repository.Profiles).Name);
        Assert.Equal("已删除“探头 A”", vm.LibraryStatus);
    }

    [Fact]
    public async Task FailedDeleteShowsTheReasonAndKeepsTheRow()
    {
        var (vm, repository, dialogs) = await CreateAsync(Profile("探头 A"));
        vm.SelectedSavedProfile = vm.SavedProfiles[0];
        dialogs.ConfirmAnswer = true;
        repository.DeleteFailure = new IOException("数据库被占用");

        await vm.DeleteSavedProfileCommand.ExecuteAsync(null);

        Assert.Equal("删除失败：数据库被占用", vm.LibraryStatus);
        Assert.Single(vm.SavedProfiles);
        Assert.Single(repository.Profiles);
    }

    [Fact]
    public async Task FailedLoadShowsTheReason()
    {
        var repository = new MemoryCalibrationRepository([]) { LoadFailure = new IOException("文件损坏") };
        var vm = new CalibrationLibraryViewModel(repository, new FakeDialogService());

        await vm.LoadSavedProfilesCommand.ExecuteAsync(null);

        Assert.Equal("加载配置列表失败：文件损坏", vm.LibraryStatus);
        Assert.Empty(vm.SavedProfiles);
    }

    [Fact]
    public async Task ExportJsonWritesTheChosenFileAndCancelWritesNothing()
    {
        var (vm, _, dialogs) = await CreateAsync(Profile("产线/探头 7"));
        var profile = vm.SavedProfiles[0];
        vm.SelectedSavedProfile = profile;
        var dir = Directory.CreateTempSubdirectory("library-export-");
        try
        {
            // 取消保存对话框：不写文件、不提示。建议的文件名去掉了不能用于文件名的字符。
            await vm.ExportSelectedProfileJsonCommand.ExecuteAsync(null);
            var request = Assert.Single(dialogs.SaveFileRequests);
            Assert.Equal("产线探头 7.json", request.FileName);
            Assert.Equal(".json", request.DefaultExt);
            Assert.Empty(dialogs.Notifications);
            Assert.Empty(dir.GetFiles());

            var path = Path.Combine(dir.FullName, "profile.json");
            dialogs.SaveFilePath = path;
            await vm.ExportSelectedProfileJsonCommand.ExecuteAsync(null);

            Assert.Equal(OrthogonalityProfileExporter.BuildJson(profile), File.ReadAllText(path));
            Assert.Equal(($"已导出: {path}", "成功"), Assert.Single(dialogs.Notifications));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ExportCsvWritesUtf8WithBomAndReportsWriteFailures()
    {
        var (vm, _, dialogs) = await CreateAsync(Profile("探头 A"));
        var profile = vm.SavedProfiles[0];
        vm.SelectedSavedProfile = profile;
        var dir = Directory.CreateTempSubdirectory("library-export-");
        try
        {
            var path = Path.Combine(dir.FullName, "profile.csv");
            dialogs.SaveFilePath = path;
            await vm.ExportSelectedProfileCsvCommand.ExecuteAsync(null);

            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.Equal(OrthogonalityProfileExporter.BuildCsv(profile), Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
            Assert.Equal(($"已导出: {path}", "成功"), Assert.Single(dialogs.Notifications));

            // 写不进去（目录不存在）：提示失败原因，不报成功。
            dialogs.Notifications.Clear();
            dialogs.SaveFilePath = Path.Combine(dir.FullName, "missing", "profile.csv");
            await vm.ExportSelectedProfileCsvCommand.ExecuteAsync(null);

            var (message, title) = Assert.Single(dialogs.Notifications);
            Assert.Equal("错误", title);
            Assert.StartsWith("导出失败: ", message);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task UsageHelpShowsTheProfileHelp()
    {
        var (vm, _, dialogs) = await CreateAsync();

        vm.ShowProfileUsageHelpCommand.Execute(null);

        Assert.Equal(HelpTopic.ProfileUsage, Assert.Single(dialogs.HelpTopics));
    }

    /// <summary>内存中的配置库，可以让读取或删除失败。</summary>
    private sealed class MemoryCalibrationRepository(IEnumerable<OrthogonalityParams> profiles) : ICalibrationRepository
    {
        public List<OrthogonalityParams> Profiles { get; } = profiles.ToList();
        public Exception? LoadFailure { get; set; }
        public Exception? DeleteFailure { get; set; }

        public Task<IReadOnlyList<OrthogonalityParams>> GetOrthogonalityProfilesAsync(string? sensorSerial = null) =>
            LoadFailure is { } failure
                ? Task.FromException<IReadOnlyList<OrthogonalityParams>>(failure)
                : Task.FromResult<IReadOnlyList<OrthogonalityParams>>(Profiles.ToList());

        public Task DeleteOrthogonalityProfileAsync(string id)
        {
            if (DeleteFailure is { } failure) return Task.FromException(failure);
            Profiles.RemoveAll(p => p.Id == id);
            return Task.CompletedTask;
        }

        public Task SaveOrthogonalityProfileAsync(OrthogonalityParams profile) => throw new NotSupportedException();
        public Task<OrthogonalityParams?> GetOrthogonalityProfileAsync(string id) => throw new NotSupportedException();
        public Task SaveCalibrationProfileAsync(CalibrationParams profile) => throw new NotSupportedException();
        public Task<IReadOnlyList<CalibrationParams>> GetCalibrationProfilesAsync(SensorType? sensorType = null) => throw new NotSupportedException();
        public Task DeleteCalibrationProfileAsync(string id) => throw new NotSupportedException();
        public Task<int> SaveOrthogonalityCalibrationAsync(OrthogonalityCalibrationRecord record) => throw new NotSupportedException();
        public Task<List<OrthogonalityCalibrationRecord>> GetOrthogonalityHistoryAsync(string deviceId) => throw new NotSupportedException();
    }
}
