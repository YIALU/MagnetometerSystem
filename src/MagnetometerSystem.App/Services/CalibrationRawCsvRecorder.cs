using System.Globalization;
using System.IO;
using System.Text;

namespace MagnetometerSystem.App.Services;

/// <summary>原始 CSV 的文件头：配置名称、传感器类型、单位、采集模式，以及各列（X、Y、Z[、X2、Y2、Z2]）对应的协议通道。</summary>
public sealed record CalibrationRawCsvHeader(string? ProfileName, string SensorType, string Unit, string Mode, IReadOnlyList<string> SourceChannels);

/// <summary>
/// 校正采集的原始 CSV：开始采集时创建（同一秒内已有同名文件时另起新名，不覆盖），逐点追加；撤销、清空只追加注释行，不改写已写的行。
/// 它只是拟合样本的旁路记录：创建或写入失败时停止写这个文件并保留原因，采集照常进行。
/// 成员可以从任意线程调用；调用方在追加样本的同一临界区内写对应的行，行号与样本一一对应。
/// </summary>
public sealed class CalibrationRawCsvRecorder
{
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string? _filePath;
    private int _rows;      // 已成功写入的数据行数
    private string? _error; // 创建或写入失败的原因

    public CalibrationRawCsvRecorder(string outputDirectory) => OutputDirectory = outputDirectory;

    /// <summary>保存目录；测试改为临时目录，不写入用户数据。</summary>
    public string OutputDirectory { get; set; }

    /// <summary>最近一次开始采集时的文件路径（创建失败时为原定的路径）；还没有开始过时为 null。</summary>
    public string? FilePath
    {
        get { lock (_lock) return _filePath; }
    }

    /// <summary>已写入的数据行数，以及创建或写入失败的原因（没有失败时为 null）。</summary>
    public (int Rows, string? Error) Status
    {
        get { lock (_lock) return (_rows, _error); }
    }

    /// <summary>当前的写入器，未在写入时为 null（诊断与测试用：测试据此模拟写入器失效）。</summary>
    internal StreamWriter? CurrentWriter
    {
        get { lock (_lock) return _writer; }
    }

    /// <summary>按配置名称和当前时间创建新文件并写入文件头；正在写入时不重复创建。</summary>
    public void Open(CalibrationRawCsvHeader header)
    {
        lock (_lock)
        {
            if (_writer != null) return;
            var safeName = string.Join("_", (header.ProfileName ?? "calib").Split(Path.GetInvalidFileNameChars()));
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "calib";
            _rows = 0;
            _error = null;
            var directory = OutputDirectory;
            var baseName = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}_raw";
            _filePath = Path.Combine(directory, baseName + ".csv");
            StreamWriter? writer = null;
            try
            {
                Directory.CreateDirectory(directory);
                // 同一秒内再次开始时文件名相同：另起新名，不覆盖上一次的原始数据。
                FileStream? stream = null;
                for (int n = 2; stream == null; n++)
                {
                    try { stream = new FileStream(_filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
                    catch (IOException) when (File.Exists(_filePath) && n < 100)
                    {
                        _filePath = Path.Combine(directory, $"{baseName}_{n}.csv");
                    }
                }
                writer = new StreamWriter(stream, new UTF8Encoding(true));
                writer.WriteLine($"# Calibration Profile : {header.ProfileName}");
                writer.WriteLine($"# Sensor Type         : {header.SensorType}");
                writer.WriteLine($"# Unit                : {header.Unit}");
                writer.WriteLine($"# Collection Mode     : {header.Mode}");
                writer.WriteLine($"# Recorded At         : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                var channelNames = header.SourceChannels.Count == 6 ? "X1,Y1,Z1,X2,Y2,Z2" : "X,Y,Z";
                // 列名按轴排列；对应的协议通道写在注释里（名称中的换行替换为空格）。
                var axes = channelNames.Split(',');
                writer.WriteLine("# Source Channels     : " + string.Join(", ", header.SourceChannels.Select((label, i) =>
                    $"{(i < axes.Length ? axes[i] : "?")}={label.Replace('\r', ' ').Replace('\n', ' ')}")));
                writer.WriteLine("point_index,timestamp," + channelNames);
                writer.Flush();
                _writer = writer;
            }
            catch (Exception ex)
            {
                // 原始 CSV 只是拟合样本的旁路记录：创建失败时照常采集，在文件状态里说明原因。
                try { writer?.Dispose(); } catch { }
                System.Diagnostics.Trace.TraceError($"创建校正原始数据文件失败: {ex}");
                _error = $"无法创建文件：{Sentence(ex)}。本次采集的点不会写入文件，拟合样本不受影响。";
            }
        }
    }

    /// <summary>写一行原始点：<paramref name="values"/> 已按所选通道排成 X、Y、Z[、X2、Y2、Z2]。</summary>
    public void AppendPoint(DateTime timestamp, IReadOnlyList<double> values)
    {
        lock (_lock)
        {
            if (_writer == null) return;
            var sb = new StringBuilder();
            sb.Append(_rows + 1).Append(',');
            sb.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            foreach (var value in values)
            {
                sb.Append(',');
                sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
            }
            try
            {
                _writer.WriteLine(sb.ToString());
                _writer.Flush();
                _rows++;
            }
            catch (Exception ex) { Fail(ex); }
        }
    }

    /// <summary>追加注释行（# 开头），记录撤销、清空等操作。</summary>
    public void AppendComment(string text)
    {
        lock (_lock)
        {
            if (_writer == null) return;
            try
            {
                _writer.WriteLine($"# {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {text}");
                _writer.Flush();
            }
            catch (Exception ex) { Fail(ex); }
        }
    }

    /// <summary>结束写入；文件保留，路径、行数与失败原因不变。</summary>
    public void Close()
    {
        lock (_lock)
        {
            try { _writer?.Flush(); } catch { }
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }

    /// <summary>写入失败后停止写这个文件（之后的点不再尝试），保留原因供界面显示。调用方持有锁。</summary>
    private void Fail(Exception ex)
    {
        System.Diagnostics.Trace.TraceError($"写入校正原始数据文件失败: {ex}");
        _error = $"写入失败：{Sentence(ex)}。此前已写入 {_rows:N0} 行，之后的点未写入文件；拟合样本不受影响。";
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }

    /// <summary>异常消息去掉结尾句号，便于接在提示句中间。</summary>
    private static string Sentence(Exception ex) => ex.Message.Trim().TrimEnd('.', '。');
}
