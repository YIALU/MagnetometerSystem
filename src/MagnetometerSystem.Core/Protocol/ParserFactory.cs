namespace MagnetometerSystem.Core.Protocol;

/// <summary>
/// 协议解析器工厂
/// </summary>
public class ParserFactory
{
    /// <summary>
    /// 根据协议类型标识创建对应的解析器
    /// </summary>
    /// <param name="protocolType">协议类型标识</param>
    /// <param name="expectedChannels">期望通道数</param>
    public static IDataParser Create(string protocolType, int expectedChannels = 0)
    {
        return protocolType.ToUpperInvariant() switch
        {
            "ASCII_CSV" => new AsciiLineParser(expectedChannels, [',']),
            "ASCII_SPACE" => new AsciiLineParser(expectedChannels, [' ', '\t']),
            "ASCII_AUTO" => new AsciiLineParser(expectedChannels),
            "BINARY_FLOAT" => new BinaryFrameParser(useDouble: false),
            "BINARY_DOUBLE" => new BinaryFrameParser(useDouble: true),
            _ => new AsciiLineParser(expectedChannels) // 默认 ASCII 自动检测
        };
    }
}
