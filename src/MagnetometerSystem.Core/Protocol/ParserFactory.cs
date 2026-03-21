using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Protocol;

/// <summary>
/// 协议解析器工厂：根据 ProtocolConfig 创建对应的解析器
/// </summary>
public static class ParserFactory
{
    /// <summary>
    /// 根据 ProtocolConfig 创建解析器（推荐方式）
    /// </summary>
    public static IDataParser Create(ProtocolConfig config)
    {
        return config.Category switch
        {
            ProtocolCategory.Ascii => new ConfigurableAsciiParser(config),
            ProtocolCategory.Binary => new ConfigurableBinaryParser(config),
            _ => new ConfigurableAsciiParser(config)
        };
    }

    /// <summary>
    /// 简单工厂：根据协议类型名称创建解析器（向后兼容）
    /// </summary>
    public static IDataParser Create(string protocolType, int expectedChannels = 0)
    {
        return protocolType.ToUpperInvariant() switch
        {
            "ASCII_CSV" => new AsciiLineParser(expectedChannels, [',']),
            "ASCII_SPACE" => new AsciiLineParser(expectedChannels, [' ', '\t']),
            "ASCII_AUTO" => new AsciiLineParser(expectedChannels),
            "BINARY_FLOAT" => new BinaryFrameParser(useDouble: false),
            "BINARY_DOUBLE" => new BinaryFrameParser(useDouble: true),
            _ => new AsciiLineParser(expectedChannels)
        };
    }
}
