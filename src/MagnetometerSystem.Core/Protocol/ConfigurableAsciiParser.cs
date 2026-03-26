using System.Globalization;
using System.Text;
using MagnetometerSystem.Core.Helpers;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Protocol;

/// <summary>
/// 可配置的 ASCII 行协议解析器，由 ProtocolConfig 驱动
/// 支持用户自定义分隔符、字段映射（缩放/偏移）
/// </summary>
public class ConfigurableAsciiParser : IDataParser
{
    private readonly ByteRingBuffer _ringBuffer = new(131072);
    private readonly ProtocolConfig _config;
    private readonly char[] _delimiters;
    private bool _headerSkipped;

    public ConfigurableAsciiParser(ProtocolConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _delimiters = string.IsNullOrEmpty(config.AsciiDelimiter)
            ? [',', ' ', '\t']
            : config.AsciiDelimiter.ToCharArray();
        _headerSkipped = !config.AsciiHasHeader;
    }

    public void Feed(byte[] data, int offset, int count)
    {
        if (count > _ringBuffer.FreeSpace)
            _ringBuffer.Skip(count - _ringBuffer.FreeSpace);
        _ringBuffer.Write(data, offset, count);
    }

    public bool TryParse(out MagnetometerReading? reading)
    {
        reading = null;

        int newlinePos = _ringBuffer.IndexOf((byte)'\n');
        if (newlinePos < 0)
            return false;

        var lineBytes = _ringBuffer.ReadBytes(newlinePos + 1);
        var line = Encoding.ASCII.GetString(lineBytes).Trim('\r', '\n', ' ');

        // 跳过表头行
        if (!_headerSkipped)
        {
            _headerSkipped = true;
            return false;
        }

        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#') || line.StartsWith("//"))
            return false;

        var parts = line.Split(_delimiters, StringSplitOptions.RemoveEmptyEntries);

        // 根据字段映射解析
        if (_config.FieldMappings.Count > 0)
        {
            int maxChannel = _config.FieldMappings.Max(f => f.ChannelIndex) + 1;
            var values = new double[maxChannel];

            foreach (var field in _config.FieldMappings)
            {
                // 对 ASCII 协议，ByteOffset 表示字段的列索引（第几个分隔字段）
                int colIndex = field.ByteOffset;
                if (colIndex < parts.Length &&
                    double.TryParse(parts[colIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                {
                    values[field.ChannelIndex] = val * field.Scale + field.Offset;
                }
            }

            reading = new MagnetometerReading
            {
                Timestamp = DateTime.Now,
                ChannelValues = values,
            };
            return true;
        }
        else
        {
            // 无字段映射，按顺序解析所有数值字段
            var values = new List<double>();
            foreach (var part in parts)
            {
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
                    values.Add(val);
            }
            if (values.Count == 0) return false;

            reading = new MagnetometerReading
            {
                Timestamp = DateTime.Now,
                ChannelValues = values.ToArray(),
            };
            return true;
        }
    }

    public void Reset()
    {
        _ringBuffer.Clear();
        _headerSkipped = !_config.AsciiHasHeader;
    }
}
