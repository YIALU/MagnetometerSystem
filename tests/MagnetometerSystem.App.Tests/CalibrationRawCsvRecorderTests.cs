using System.IO;
using MagnetometerSystem.App.Services;

namespace MagnetometerSystem.App.Tests;

/// <summary>校正原始 CSV：文件头、逐点追加与注释行；创建或写入失败时保留原因并停止写这个文件。</summary>
public class CalibrationRawCsvRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"raw_recorder_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static CalibrationRawCsvHeader Header(string? name, params string[] channels) =>
        new(name, "DualTriaxialFluxgate", "uT", "Manual48", channels);

    [Fact]
    public void WritesTheHeaderAxisColumnsRowsAndComments()
    {
        var recorder = new CalibrationRawCsvRecorder(_dir);
        Assert.Null(recorder.FilePath);
        recorder.Open(Header("探头 A", "X1", "Y1", "Z1", "X\r\n2", "Y2", "Z2"));
        var path = Assert.IsType<string>(recorder.FilePath);
        Assert.StartsWith(Path.Combine(_dir, "探头 A_"), path);
        Assert.EndsWith("_raw.csv", path);

        recorder.AppendPoint(new DateTime(2026, 1, 2, 3, 4, 5, 678), [0.1, -2, 1e-9, 4, 5, 6]);
        recorder.AppendComment("已撤销第 1 点");
        recorder.AppendPoint(new DateTime(2026, 1, 2, 3, 4, 6), [1, 2, 3, 4, 5, 6]);
        recorder.Close();
        recorder.AppendPoint(DateTime.Now, [9, 9, 9, 9, 9, 9]);   // 关闭后不再写入

        Assert.Equal((2, (string?)null), recorder.Status);
        Assert.Equal(path, recorder.FilePath);   // 关闭后仍指向这个文件，界面据此打开
        var lines = File.ReadAllLines(path);
        Assert.Equal("# Calibration Profile : 探头 A", lines[0]);
        Assert.Equal("# Sensor Type         : DualTriaxialFluxgate", lines[1]);
        Assert.Equal("# Unit                : uT", lines[2]);
        Assert.Equal("# Collection Mode     : Manual48", lines[3]);
        Assert.StartsWith("# Recorded At         : ", lines[4]);
        Assert.Equal("# Source Channels     : X1=X1, Y1=Y1, Z1=Z1, X2=X  2, Y2=Y2, Z2=Z2", lines[5]);   // 名称中的换行替换为空格
        Assert.Equal("point_index,timestamp,X1,Y1,Z1,X2,Y2,Z2", lines[6]);
        Assert.Equal("1,2026-01-02 03:04:05.678,0.1,-2,1E-09,4,5,6", lines[7]);
        Assert.Matches(@"^# \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} 已撤销第 1 点$", lines[8]);
        Assert.Equal("2,2026-01-02 03:04:06.000,1,2,3,4,5,6", lines[9]);
        Assert.Equal(10, lines.Length);
    }

    [Theory]
    [InlineData("a/b", "a_b_")]   // '/' 在 Windows 与 Linux 上都不能出现在文件名中
    [InlineData("   ", "calib_")]
    [InlineData(null, "calib_")]
    public void FileNamesUseASafeProfileName(string? name, string prefix)
    {
        var recorder = new CalibrationRawCsvRecorder(_dir);
        recorder.Open(Header(name, "X", "Y", "Z"));
        recorder.Close();
        var fileName = Path.GetFileName(Assert.IsType<string>(recorder.FilePath));
        Assert.StartsWith(prefix, fileName);
        Assert.Equal("point_index,timestamp,X,Y,Z", File.ReadAllLines(recorder.FilePath!)[6]);
    }

    [Fact]
    public void OpeningAgainWhileWritingKeepsTheCurrentFile()
    {
        var recorder = new CalibrationRawCsvRecorder(_dir);
        recorder.Open(Header("A", "X", "Y", "Z"));
        recorder.AppendPoint(DateTime.Now, [1, 2, 3]);
        var path = recorder.FilePath;

        recorder.Open(Header("B", "X", "Y", "Z"));
        Assert.Equal(path, recorder.FilePath);
        Assert.Equal(1, recorder.Status.Rows);
        recorder.Close();
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void AWriteFailureStopsWritingAndKeepsTheRowCount()
    {
        var recorder = new CalibrationRawCsvRecorder(_dir);
        recorder.Open(Header("A", "X", "Y", "Z"));
        recorder.AppendPoint(DateTime.Now, [1, 2, 3]);
        Assert.IsType<StreamWriter>(recorder.CurrentWriter).Dispose();   // 模拟写入器失效

        recorder.AppendPoint(DateTime.Now, [4, 5, 6]);
        var (rows, error) = recorder.Status;
        Assert.Equal(1, rows);
        Assert.StartsWith("写入失败：", error);
        Assert.Contains("此前已写入 1 行", error);
        Assert.DoesNotContain("。。", error);
        Assert.Null(recorder.CurrentWriter);

        recorder.AppendPoint(DateTime.Now, [7, 8, 9]);   // 之后的点不再尝试
        recorder.AppendComment("已清空之前的 1 点，重新记录");
        Assert.Equal(1, recorder.Status.Rows);
        recorder.Close();
        Assert.Equal(1, File.ReadAllLines(recorder.FilePath!).Count(l => l.Length > 0 && char.IsDigit(l[0])));
    }

    [Fact]
    public void ACreateFailureIsReportedAndNothingIsWritten()
    {
        Directory.CreateDirectory(_dir);
        var blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "");   // 目录位置被同名文件占用
        var recorder = new CalibrationRawCsvRecorder(blocked);

        recorder.Open(Header("A", "X", "Y", "Z"));
        recorder.AppendPoint(DateTime.Now, [1, 2, 3]);

        var (rows, error) = recorder.Status;
        Assert.Equal(0, rows);
        Assert.StartsWith("无法创建文件：", error);
        Assert.EndsWith("本次采集的点不会写入文件，拟合样本不受影响。", error);
        Assert.Equal(blocked, Path.GetDirectoryName(recorder.FilePath));   // 界面仍显示原定的位置
        Assert.Equal("", File.ReadAllText(blocked));

        // 换到可写的目录后重新开始：清掉上一次的失败原因。
        recorder.OutputDirectory = _dir;
        recorder.Open(Header("A", "X", "Y", "Z"));
        Assert.Equal((0, (string?)null), recorder.Status);
        recorder.Close();
    }
}
