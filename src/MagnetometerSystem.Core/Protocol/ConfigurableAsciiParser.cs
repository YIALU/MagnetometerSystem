using System.Globalization;
using System.Text;
using MagnetometerSystem.Core.Helpers;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Protocol;

public class ConfigurableAsciiParser : IDataParser, IParserDiagnostics
{
    private readonly ByteRingBuffer _ringBuffer = new(131072);
    private readonly ProtocolConfig _config;
    private int _remainingHeaderLines;
    public long RejectedFrameCount { get; private set; }
    public string? LastError { get; private set; }
    public ParseRecordLog Records { get; } = new();

    public ConfigurableAsciiParser(ProtocolConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = ProtocolConfig.FromJson(config.ToJson())!;
        _config.Validate();
        Reset();
    }

    public void Feed(byte[] data, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > data.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
        if (count > _ringBuffer.FreeSpace)
        {
            Records.Skipped(_ringBuffer.Count, "接收缓冲区溢出，丢弃未成行的数据");
            _ringBuffer.Clear();
            Reject("接收缓冲区溢出，已丢弃不完整报文");
            if (count > _ringBuffer.Capacity) { offset += count - _ringBuffer.Capacity; count = _ringBuffer.Capacity; }
        }
        _ringBuffer.Write(data, offset, count);
    }

    public bool TryParse(out MagnetometerReading? reading)
    {
        reading = null;
        while (true)
        {
            // CRLF 配置兼容只发送 LF 的旧设备；CR 单独分帧也可显式配置。
            int end = _ringBuffer.IndexOf(_config.AsciiLineEnding == "\r" ? (byte)'\r' : (byte)'\n');
            if (end < 0) return false;
            var bytes = _ringBuffer.ReadBytes(end + 1);
            var line = Encoding.ASCII.GetString(bytes).Trim('\r', '\n', ' ');
            if (_remainingHeaderLines > 0) { _remainingHeaderLines--; Records.Skipped(bytes.Length, "跳过表头行"); continue; }
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//")) { Records.Skipped(bytes.Length, "跳过空行 / 注释行"); continue; }
            var parts = string.IsNullOrEmpty(_config.AsciiDelimiter)
                ? line.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                : _config.AsciiDelimiter.All(char.IsWhiteSpace)
                    ? line.Split(_config.AsciiDelimiter.ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
                    : line.Split(_config.AsciiDelimiter, StringSplitOptions.None);
            var values = new double[_config.FieldMappings.Count > 0 ? _config.FieldMappings.Count : parts.Length];
            string? problem = values.Length > 0 ? null : "没有可解析的字段";
            if (_config.FieldMappings.Count > 0)
            {
                foreach (var field in _config.FieldMappings)
                {
                    if (field.ByteOffset >= parts.Length) { problem = $"缺少列号 {field.ByteOffset}（本行只有 {parts.Length} 列，列号从 0 开始）"; break; }
                    if (!TryValue(parts[field.ByteOffset], out var raw)) { problem = $"列号 {field.ByteOffset} 的“{Clip(parts[field.ByteOffset])}”不是有限数值"; break; }
                    var value = raw * field.Scale + field.Offset;
                    if (!double.IsFinite(value)) { problem = $"通道 {field.ChannelIndex} 缩放后不是有限数值"; break; }
                    values[field.ChannelIndex] = value;
                }
            }
            else
            {
                for (int i = 0; i < parts.Length && problem == null; i++)
                    if (!TryValue(parts[i], out values[i])) problem = $"列号 {i} 的“{Clip(parts[i])}”不是有限数值";
            }
            if (problem != null)
            {
                Reject($"ASCII 报文{problem}，整帧已丢弃");
                Records.Rejected(bytes.Length, problem, bytes, hex: false);
                continue;
            }
            Records.Accepted(bytes.Length, values.Length, bytes, hex: false);
            reading = new MagnetometerReading { Timestamp = DateTime.Now, ChannelValues = values };
            return true;
        }
    }

    private static bool TryValue(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private void Reject(string message) { RejectedFrameCount++; LastError = message; }
    private static string Clip(string text) => text.Length <= 16 ? text : text[..16] + "…";
    public void Reset()
    {
        _ringBuffer.Clear();
        _remainingHeaderLines = Math.Max(_config.AsciiSkipLines, _config.AsciiHasHeader ? 1 : 0);
    }
}
