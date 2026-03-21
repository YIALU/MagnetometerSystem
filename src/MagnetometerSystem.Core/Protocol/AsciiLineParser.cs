using System.Globalization;
using System.Text;
using MagnetometerSystem.Core.Helpers;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Protocol;

/// <summary>
/// ASCII 行协议解析器
/// 支持格式: 逗号/空格/制表符分隔的数值行，以 \n 或 \r\n 结尾
/// 示例: "12345.67,23456.78,34567.89\r\n"
/// </summary>
public class AsciiLineParser : IDataParser
{
    private readonly ByteRingBuffer _ringBuffer = new(131072);
    private readonly char[] _delimiters;
    private readonly Encoding _encoding;
    private int _expectedChannels;

    /// <summary>
    /// 创建 ASCII 行解析器
    /// </summary>
    /// <param name="expectedChannels">期望的通道数（0 = 自动检测）</param>
    /// <param name="delimiters">字段分隔符，默认为逗号、空格、制表符</param>
    /// <param name="encoding">字符编码，默认 ASCII</param>
    public AsciiLineParser(int expectedChannels = 0, char[]? delimiters = null, Encoding? encoding = null)
    {
        _expectedChannels = expectedChannels;
        _delimiters = delimiters ?? [',', ' ', '\t'];
        _encoding = encoding ?? Encoding.ASCII;
    }

    public void Feed(byte[] data, int offset, int count)
    {
        _ringBuffer.Write(data, offset, count);
    }

    public bool TryParse(out MagnetometerReading? reading)
    {
        reading = null;

        // 查找行尾 \n
        int newlinePos = _ringBuffer.IndexOf((byte)'\n');
        if (newlinePos < 0)
            return false;

        // 读取整行（含 \n）
        var lineBytes = _ringBuffer.ReadBytes(newlinePos + 1);
        var line = _encoding.GetString(lineBytes).Trim('\r', '\n', ' ');

        if (string.IsNullOrWhiteSpace(line))
            return false;

        // 跳过注释行（以 # 或 // 开头）
        if (line.StartsWith('#') || line.StartsWith("//"))
            return false;

        // 分割字段
        var parts = line.Split(_delimiters, StringSplitOptions.RemoveEmptyEntries);

        // 尝试解析数值
        var values = new List<double>();
        foreach (var part in parts)
        {
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            {
                values.Add(val);
            }
        }

        if (values.Count == 0)
            return false;

        // 自动检测通道数
        if (_expectedChannels == 0)
            _expectedChannels = values.Count;

        // 通道数不匹配时截取或补零
        var channelValues = new double[_expectedChannels];
        for (int i = 0; i < _expectedChannels; i++)
        {
            channelValues[i] = i < values.Count ? values[i] : 0;
        }

        reading = new MagnetometerReading
        {
            Timestamp = DateTime.Now,
            ChannelValues = channelValues,
        };

        return true;
    }

    public void Reset()
    {
        _ringBuffer.Clear();
    }
}
