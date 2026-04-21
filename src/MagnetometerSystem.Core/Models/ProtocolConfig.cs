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

    // ==== 字段映射（旧模式，保留兼容） ====

    /// <summary>字段映射列表（描述数据区中各字段的位置和含义）</summary>
    public List<FieldMapping> FieldMappings { get; set; } = [];

    // ==== 帧段配置（新模式） ====

    /// <summary>帧段列表（顺序拼接，系统自动计算偏移）</summary>
    public List<FrameSegment> Segments { get; set; } = [];

    /// <summary>是否使用帧段模式</summary>
    [JsonIgnore]
    public bool UsesSegments => Segments.Count > 0;

    /// <summary>备注</summary>
    public string? Notes { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ==== 派生属性 ====

    /// <summary>从协议配置派生的通道数量</summary>
    [JsonIgnore]
    public int DerivedChannelCount => UsesSegments
        ? Segments.Count(s => s.Type == SegmentType.DataField)
        : FieldMappings.Count;

    /// <summary>从协议配置派生的通道名称列表</summary>
    [JsonIgnore]
    public List<string> DerivedChannelNames => UsesSegments
        ? Segments.Where(s => s.Type == SegmentType.DataField).Select(s => s.Name).ToList()
        : FieldMappings.Select(f => f.Name).ToList();

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

    private static readonly JsonSerializerOptions _jsonWriteOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>序列化为 JSON 字符串（用于保存，枚举以字符串输出便于人工编辑）</summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, _jsonWriteOptions);
    }

    private static readonly JsonSerializerOptions _safeJsonOptions = new()
    {
        MaxDepth = 8,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>从 JSON 字符串反序列化（用于加载），自动迁移旧格式</summary>
    public static ProtocolConfig? FromJson(string json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > 1_000_000)
            return null;
        var config = JsonSerializer.Deserialize<ProtocolConfig>(json, _safeJsonOptions);
        if (config != null)
        {
            // 旧格式自动迁移：如果没有 Segments 但有 FieldMappings 且是二进制协议
            if (config.Category == ProtocolCategory.Binary
                && config.Segments.Count == 0
                && config.FieldMappings.Count > 0)
            {
                config.MigrateFromLegacy();
            }
            config.ComputeSegmentOffsets();
        }
        return config;
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
        var config = new ProtocolConfig
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
            ],
            Segments =
            [
                new() { Type = SegmentType.Header, Name = "帧头", ByteCount = 2, FixedHexValue = "AA55" },
                new() { Type = SegmentType.LengthField, Name = "长度", ByteCount = 1, LengthBigEndian = false },
                new() { Type = SegmentType.DataField, Name = "X通道", ByteCount = 8, DataType = FieldDataType.Double, ChannelIndex = 0 },
                new() { Type = SegmentType.DataField, Name = "Y通道", ByteCount = 8, DataType = FieldDataType.Double, ChannelIndex = 1 },
                new() { Type = SegmentType.DataField, Name = "Z通道", ByteCount = 8, DataType = FieldDataType.Double, ChannelIndex = 2 },
                new() { Type = SegmentType.Checksum, Name = "校验", ByteCount = 1, ChecksumAlgorithm = ChecksumAlgorithm.Xor, ChecksumStartIndex = 0 },
                new() { Type = SegmentType.Tail, Name = "帧尾", ByteCount = 1, FixedHexValue = "0D" },
            ],
        };
        config.ComputeSegmentOffsets();
        return config;
    }

    /// <summary>
    /// 创建一个纯段式三轴磁通门二进制协议（Float 类型，帧更短）
    /// </summary>
    public static ProtocolConfig CreateDefaultBinaryTriaxialSegments()
    {
        var config = new ProtocolConfig
        {
            Name = "三轴 Binary 段式 (AA55, Float)",
            Category = ProtocolCategory.Binary,
            Segments =
            [
                new() { Type = SegmentType.Header, Name = "帧头", ByteCount = 2, FixedHexValue = "AA55" },
                new() { Type = SegmentType.LengthField, Name = "长度", ByteCount = 1, LengthBigEndian = false },
                new() { Type = SegmentType.DataField, Name = "X通道", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 0 },
                new() { Type = SegmentType.DataField, Name = "Y通道", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 1 },
                new() { Type = SegmentType.DataField, Name = "Z通道", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 2 },
                new() { Type = SegmentType.Checksum, Name = "校验", ByteCount = 1, ChecksumAlgorithm = ChecksumAlgorithm.Xor, ChecksumStartIndex = 0 },
                new() { Type = SegmentType.Tail, Name = "帧尾", ByteCount = 1, FixedHexValue = "0D" },
            ],
        };
        config.ComputeSegmentOffsets();
        return config;
    }

    /// <summary>
    /// 创建双三轴 ASCII 协议的默认配置（6通道）
    /// </summary>
    public static ProtocolConfig CreateDefaultAsciiDualTriaxial()
    {
        return new ProtocolConfig
        {
            Name = "双三轴 ASCII (逗号分隔)",
            Category = ProtocolCategory.Ascii,
            AsciiDelimiter = ",",
            FieldMappings =
            [
                new() { Name = "X1", ChannelIndex = 0, ByteOffset = 0 },
                new() { Name = "Y1", ChannelIndex = 1, ByteOffset = 1 },
                new() { Name = "Z1", ChannelIndex = 2, ByteOffset = 2 },
                new() { Name = "X2", ChannelIndex = 3, ByteOffset = 3 },
                new() { Name = "Y2", ChannelIndex = 4, ByteOffset = 4 },
                new() { Name = "Z2", ChannelIndex = 5, ByteOffset = 5 },
            ]
        };
    }

    /// <summary>
    /// 遍历 Segments 累加 ByteCount，设置每段的 ComputedOffset
    /// </summary>
    public void ComputeSegmentOffsets()
    {
        int offset = 0;
        foreach (var seg in Segments)
        {
            seg.ComputedOffset = offset;
            offset += seg.ByteCount;
        }
    }

    /// <summary>
    /// 计算段式配置的总帧长度
    /// </summary>
    [JsonIgnore]
    public int TotalFrameLength => Segments.Sum(s => s.ByteCount);

    /// <summary>
    /// 将旧的 FieldMapping 格式迁移为 Segments 格式
    /// </summary>
    public void MigrateFromLegacy()
    {
        Segments.Clear();

        // 帧头
        if (!string.IsNullOrEmpty(FrameHeader))
        {
            var headerBytes = HexToBytes(FrameHeader);
            Segments.Add(new FrameSegment
            {
                Type = SegmentType.Header,
                Name = "帧头",
                ByteCount = headerBytes.Length,
                FixedHexValue = FrameHeader,
            });
        }

        // 长度字段
        if (HasLengthByte)
        {
            Segments.Add(new FrameSegment
            {
                Type = SegmentType.LengthField,
                Name = "长度",
                ByteCount = LengthByteCount,
                LengthBigEndian = LengthBigEndian,
            });
        }

        // 数据字段（按 ByteOffset 排序）
        foreach (var field in FieldMappings.OrderBy(f => f.ByteOffset))
        {
            Segments.Add(new FrameSegment
            {
                Type = SegmentType.DataField,
                Name = field.Name,
                ByteCount = field.ByteSize,
                DataType = field.DataType,
                BigEndian = field.BigEndian,
                ChannelIndex = field.ChannelIndex,
                Scale = field.Scale,
                Offset = field.Offset,
            });
        }

        // 校验
        if (Checksum != ChecksumType.None)
        {
            Segments.Add(new FrameSegment
            {
                Type = SegmentType.Checksum,
                Name = "校验",
                ByteCount = 1,
                ChecksumAlgorithm = Checksum switch
                {
                    ChecksumType.Xor => ChecksumAlgorithm.Xor,
                    ChecksumType.Sum8 => ChecksumAlgorithm.Sum8,
                    ChecksumType.CRC16 => ChecksumAlgorithm.CRC16,
                    _ => ChecksumAlgorithm.Xor,
                },
                ChecksumStartIndex = 0,
            });
        }

        // 帧尾
        if (!string.IsNullOrEmpty(FrameTail))
        {
            var tailBytes = HexToBytes(FrameTail);
            Segments.Add(new FrameSegment
            {
                Type = SegmentType.Tail,
                Name = "帧尾",
                ByteCount = tailBytes.Length,
                FixedHexValue = FrameTail,
            });
        }

        ComputeSegmentOffsets();
    }
}
