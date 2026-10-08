using System.IO;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Core.Storage;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Export;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

/// <summary>会话默认名带协议名和开始时间；导出文件名沿用会话名。</summary>
public class SessionNamingTests
{
    [Fact]
    public Task NewSessionIsNamedAfterProtocolAndStartTime() => WpfTestHost.RunAsync(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"naming_{Guid.NewGuid():N}.db");
        var db = new DatabaseInitializer(path);
        await db.InitializeAsync();
        var bus = new DataBus();
        var storage = new SqliteStorageService(db, bus);
        try
        {
            var repo = new SqliteCalibrationRepository(db);
            var sessions = new SessionListViewModel(storage, new CsvExporter(storage), bus, new OrthogonalityCorrector(), repo);
            var before = DateTime.Now.AddSeconds(-1);
            // 与连接页相同：ProtocolType 取自当前协议名。
            await bus.PublishAcquisitionStartingAsync(new SensorConfig
            {
                Type = SensorType.Generic, SampleRate = 10, ChannelCountOverride = 3,
                ChannelNamesOverride = ["X", "Y", "Z"], ChannelUnitsOverride = ["nT", "nT", "nT"],
                ProtocolType = "磁梯度数采卡-pt (仅磁场6通道)",
            }, new ConnectionConfig());
            await bus.PublishAcquisitionStoppingAsync();
            bus.PublishAcquisitionStopped();

            var session = Assert.Single(await storage.GetSessionsAsync());
            Assert.StartsWith("磁梯度数采卡-pt (仅磁场6通道) ", session.Name);
            var time = DateTime.ParseExact(session.Name[^19..], "yyyy-MM-dd HH:mm:ss", null);
            Assert.InRange(time, before, DateTime.Now.AddSeconds(1));
        }
        finally
        {
            storage.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    });

    [Theory]
    [InlineData(null, "采集 2026-10-08 09:30:15")]
    [InlineData("  ", "采集 2026-10-08 09:30:15")]
    [InlineData(" 三轴 ASCII (逗号分隔) ", "三轴 ASCII (逗号分隔) 2026-10-08 09:30:15")]
    public void DefaultSessionName_UsesProtocolOrFallback(string? protocol, string expected) =>
        Assert.Equal(expected, SessionListViewModel.DefaultSessionName(protocol, new DateTime(2026, 10, 8, 9, 30, 15)));

    [Theory]
    [InlineData("三轴 ASCII (逗号分隔) 2026-10-08 09:30:15", null, "三轴_ASCII_(逗号分隔)_2026-10-08_09-30-15.csv")]
    [InlineData("三轴 ASCII (逗号分隔) 2026-10-08 09:30:15", "室外 第2次", "三轴_ASCII_(逗号分隔)_2026-10-08_09-30-15_室外_第2次.csv")]
    [InlineData("测试/甲", null, "测试甲_2026-10-08_09-30-15.csv")]   // 改过名：补上开始时间
    [InlineData("", null, "2026-10-08_09-30-15.csv")]
    public void DefaultExportFileName_FollowsSessionNameAndKeepsTime(string name, string? notes, string expected) =>
        Assert.Equal(expected, SessionListViewModel.DefaultExportFileName(new SessionInfo
        {
            Name = name, Notes = notes, StartedAt = new DateTime(2026, 10, 8, 9, 30, 15),
        }));
}
