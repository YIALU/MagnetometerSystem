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
            var line = Encoding.ASCII.GetString(_ringBuffer.ReadBytes(end + 1)).Trim('\r', '\n', ' ');
            if (_remainingHeaderLines > 0) { _remainingHeaderLines--; continue; }
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//")) continue;
            var parts = string.IsNullOrEmpty(_config.AsciiDelimiter)
                ? line.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                : _config.AsciiDelimiter.All(char.IsWhiteSpace)
                    ? line.Split(_config.AsciiDelimiter.ToCharArray(), StringSplitOptions.RemoveEmptyEntries)
                    : line.Split(_config.AsciiDelimiter, StringSplitOptions.None);
            var values = new double[_config.FieldMappings.Count > 0 ? _config.FieldMappings.Count : parts.Length];
            bool valid = values.Length > 0;
            if (_config.FieldMappings.Count > 0)
            {
                foreach (var field in _config.FieldMappings)
                {
                    if (field.ByteOffset >= parts.Length || !TryValue(parts[field.ByteOffset], out var raw)) { valid = false; break; }
                    var value = raw * field.Scale + field.Offset;
                    if (!double.IsFinite(value)) { valid = false; break; }
                    values[field.ChannelIndex] = value;
                }
            }
            else
            {
                for (int i = 0; i < parts.Length; i++)
                    if (!TryValue(parts[i], out values[i])) { valid = false; break; }
            }
            if (!valid) { Reject("ASCII 报文缺失字段或包含非有限数值，整帧已丢弃"); continue; }
            reading = new MagnetometerReading { Timestamp = DateTime.Now, ChannelValues = values };
            return true;
        }
    }

    private static bool TryValue(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    private void Reject(string message) { RejectedFrameCount++; LastError = message; }
    public void Reset()
    {
        _ringBuffer.Clear();
        _remainingHeaderLines = Math.Max(_config.AsciiSkipLines, _config.AsciiHasHeader ? 1 : 0);
    }
}
