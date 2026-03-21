using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 数据字段的数据类型
/// </summary>
public enum FieldDataType
{
    Float,      // 4 字节
    Double,     // 8 字节
    Int16,      // 2 字节有符号
    UInt16,     // 2 字节无符号
    Int32,      // 4 字节有符号
    UInt32,     // 4 字节无符号
}

/// <summary>
/// 校验方式
/// </summary>
public enum ChecksumType
{
    None,       // 无校验
    Xor,        // 异或校验
    Sum8,       // 累加和取低 8 位
    CRC16,      // CRC-16
}

/// <summary>
/// 协议中的一个字段映射：描述帧中某段字节对应哪个通道的什么数据
/// </summary>
public class FieldMapping
{
    /// <summary>字段名称（如 "X轴", "Y轴", "Total"）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>在数据区中的字节偏移（从数据区起始算，不含帧头/长度字节）</summary>
    public int ByteOffset { get; set; }

    /// <summary>数据类型</summary>
    public FieldDataType DataType { get; set; } = FieldDataType.Double;

    /// <summary>对应的通道索引（0=第一通道, 1=第二通道...）</summary>
    public int ChannelIndex { get; set; }

    /// <summary>缩放系数（原始值 * Scale = 实际 nT 值）</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>偏移量（原始值 * Scale + Offset = 实际 nT 值）</summary>
    public double Offset { get; set; } = 0.0;

    /// <summary>是否为大端序 (true=Big Endian, false=Little Endian)</summary>
    public bool BigEndian { get; set; } = false;

    /// <summary>该数据类型占用的字节数</summary>
    [JsonIgnore]
    public int ByteSize => DataType switch
    {
        FieldDataType.Float => 4,
        FieldDataType.Double => 8,
        FieldDataType.Int16 => 2,
        FieldDataType.UInt16 => 2,
        FieldDataType.Int32 => 4,
        FieldDataType.UInt32 => 4,
        _ => 4
    };
}

/// <summary>
/// 协议类别
/// </summary>
public enum ProtocolCategory
{
    /// <summary>ASCII 行协议（以换行符分隔，字段用分隔符分隔）</summary>
    Ascii,

    /// <summary>二进制帧协议（帧头 + 数据 + 可选校验 + 可选帧尾）</summary>
    Binary,
}

/// <summary>
/// 通信协议配置：用户可自由定义帧格式、字段映射等，可保存/加载
/// </summary>
public class ProtocolConfig
{
    /// <summary>配置 ID</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>配置名称（如 "CZM-5 三轴磁通门协议"）</summary>
    public string Name { get; set; } = "默认协议";

    /// <summary>协议类别</summary>
    public ProtocolCategory Category { get; set; } = ProtocolCategory.Ascii;

    // ==== ASCII 协议参数 ====

    /// <summary>字段分隔符（逗号、空格、制表符等）</summary>
    public string AsciiDelimiter { get; set; } = ",";

    /// <summary>行结束符</summary>
    public string AsciiLineEnding { get; set; } = "\r\n";

    /// <summary>是否有表头行（首行跳过）</summary>
    public bool AsciiHasHeader { get; set; } = false;

    // ==== 二进制协议参数 ====

    /// <summary>帧头字节（十六进制，如 "AA55"）</summary>
    public string FrameHeader { get; set; } = "AA55";

    /// <summary>帧尾字节（十六进制，如 "0D"，为空表示无帧尾）</summary>
    public string FrameTail { get; set; } = "";

    /// <summary>帧头后是否有长度字节</summary>
    public bool HasLengthByte { get; set; } = true;

    /// <summary>长度字节的字节数（1 或 2）</summary>
    public int LengthByteCount { get; set; } = 1;

    /// <summary>长度值是否为大端序</summary>
    public bool LengthBigEndian { get; set; } = false;

    /// <summary>
    /// 固定数据区长度（仅在 HasLengthByte=false 时使用）。
    /// 如果没有长度字节，需要指定固定的数据区长度。
    /// </summary>
    public int FixedDataLength { get; set; } = 0;

    /// <summary>校验方式</summary>
    public ChecksumType Checksum { get; set; } = ChecksumType.None;

    /// <summary>校验计算的范围起始（0=从帧头开始，通常为 0）</summary>
    public int ChecksumStartOffset { get; set; } = 0;

    // ==== 字段映射 ====

    /// <summary>字段映射列表（描述数据区中各字段的位置和含义）</summary>
    public List<FieldMapping> FieldMappings { get; set; } = [];

    /// <summary>备注</summary>
    public string? Notes { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ==== 辅助方法 ====

    /// <summary>将十六进制字符串转为字节数组</summary>
    public static byte[] HexToBytes(string hex)
    {
        hex = hex.Replace(" ", "").Replace("0x", "").Replace("0X", "");
        if (hex.Length % 2 != 0)
            hex = "0" + hex;
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    /// <summary>获取帧头字节数组</summary>
    [JsonIgnore]
    public byte[] FrameHeaderBytes => string.IsNullOrEmpty(FrameHeader) ? [] : HexToBytes(FrameHeader);

    /// <summary>获取帧尾字节数组</summary>
    [JsonIgnore]
    public byte[] FrameTailBytes => string.IsNullOrEmpty(FrameTail) ? [] : HexToBytes(FrameTail);

    /// <summary>序列化为 JSON 字符串（用于保存）</summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    }

    private static readonly JsonSerializerOptions _safeJsonOptions = new()
    {
        MaxDepth = 8,
    };

    /// <summary>从 JSON 字符串反序列化（用于加载）</summary>
    public static ProtocolConfig? FromJson(string json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 1_000_000)
            return null;
        return JsonSerializer.Deserialize<ProtocolConfig>(json, _safeJsonOptions);
    }

    /// <summary>
    /// 创建一个三轴磁通门 ASCII 协议的默认配置
    /// </summary>
    public static ProtocolConfig CreateDefaultAsciiTriaxial()
    {
        return new ProtocolConfig
        {
            Name = "三轴 ASCII (逗号分隔)",
            Category = ProtocolCategory.Ascii,
            AsciiDelimiter = ",",
            FieldMappings =
            [
                new() { Name = "X", ChannelIndex = 0, ByteOffset = 0 },
                new() { Name = "Y", ChannelIndex = 1, ByteOffset = 1 },
                new() { Name = "Z", ChannelIndex = 2, ByteOffset = 2 },
            ]
        };
    }

    /// <summary>
    /// 创建一个三轴磁通门二进制协议的默认配置
    /// </summary>
    public static ProtocolConfig CreateDefaultBinaryTriaxial()
    {
        return new ProtocolConfig
        {
            Name = "三轴 Binary (AA55 帧头, Double)",
            Category = ProtocolCategory.Binary,
            FrameHeader = "AA55",
            FrameTail = "0D",
            HasLengthByte = true,
            Checksum = ChecksumType.Xor,
            FieldMappings =
            [
                new() { Name = "X", ChannelIndex = 0, ByteOffset = 0, DataType = FieldDataType.Double },
                new() { Name = "Y", ChannelIndex = 1, ByteOffset = 8, DataType = FieldDataType.Double },
                new() { Name = "Z", ChannelIndex = 2, ByteOffset = 16, DataType = FieldDataType.Double },
            ]
        };
    }
}
