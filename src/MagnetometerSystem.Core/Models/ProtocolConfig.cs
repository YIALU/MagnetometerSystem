using System.Text.Json;
using System.Text.Json.Serialization;
using MagnetometerSystem.Core.Protocol;

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

    /// <summary>通道物理单位；无量纲字段可留空。</summary>
    public string Unit { get; set; } = "nT";

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
/// 解析器种类：决定 <see cref="Protocol.ParserFactory"/> 用哪类解析器。
/// 默认 <see cref="Auto"/> 按 <see cref="ProtocolConfig.Category"/> 分发；
/// 固有协议若帧格式无法用可配置解析器表达，在此显式指名专用解析器。
/// </summary>
public enum ParserKind
{
    /// <summary>按 Category 分发到 ConfigurableAscii/BinaryParser</summary>
    Auto,

    /// <summary>CTMBS-3-X2000 文本命令/响应协议专用解析器</summary>
    Ctmbs3X2000,
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

    /// <summary>解析器种类（默认 Auto，按 Category 分发）。固有协议可指定专用解析器。</summary>
    public ParserKind ParserKind { get; set; } = ParserKind.Auto;

    // ==== ASCII 协议参数 ====

    /// <summary>字段分隔符（逗号、空格、制表符等）</summary>
    public string AsciiDelimiter { get; set; } = ",";

    /// <summary>行结束符</summary>
    public string AsciiLineEnding { get; set; } = "\r\n";

    /// <summary>是否有表头行（首行跳过）</summary>
    public bool AsciiHasHeader { get; set; } = false;

    /// <summary>跳过的起始行数；兼容 AsciiHasHeader，取两者较大值。</summary>
    public int AsciiSkipLines { get; set; }

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

    /// <summary>CRC-16 变体（仅当 Checksum=CRC16 时有效）。必须与设备端一致。</summary>
    public Crc16Variant Crc16Variant { get; set; } = Crc16Variant.Modbus;

    /// <summary>CRC-16 校验值在帧中的字节序：false=低字节在前（Modbus RTU 习惯），true=高字节在前。</summary>
    public bool ChecksumBigEndian { get; set; } = false;

    // ==== 字段映射（旧模式，保留兼容） ====

    /// <summary>字段映射列表（描述数据区中各字段的位置和含义）</summary>
    public List<FieldMapping> FieldMappings { get; set; } = [];

    // ==== 帧段配置（新模式） ====

    /// <summary>帧段列表（顺序拼接，系统自动计算偏移）</summary>
    public List<FrameSegment> Segments { get; set; } = [];

    /// <summary>是否使用帧段模式</summary>
    [JsonIgnore]
    public bool UsesSegments => Segments.Count > 0;

    /// <summary>
    /// 随协议一起提供的设备命令组（内置协议自带，用户协议可留空）。
    /// 命令与设备强相关，切换协议时 UI 只展示当前协议的指令，避免混入无关命令。
    /// </summary>
    public List<CommandGroup> Commands { get; set; } = [];

    /// <summary>备注</summary>
    public string? Notes { get; set; }

    /// <summary>创建时间</summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // ==== 派生属性 ====

    /// <summary>从协议配置派生的通道数量</summary>
    [JsonIgnore]
    public int DerivedChannelCount => ParserKind switch
    {
        ParserKind.Ctmbs3X2000 => 4, // D / H / Z / T
        _ => Category == ProtocolCategory.Binary && UsesSegments
            ? Segments.Count(s => s.Type == SegmentType.DataField)
            : FieldMappings.Count,
    };

    /// <summary>
    /// 从协议配置派生的通道名称列表。
    /// 必须按 ChannelIndex 升序返回：parser 把值写入 values[ChannelIndex]（物理槽位），
    /// 而通道名/图表/数据库 JSON key 都按数组下标对齐。若按段的列表顺序返回，
    /// 用户在编辑器里上下调整段顺序后名字与数据会错位（图表标题错、DB 名值错配）。
    /// </summary>
    [JsonIgnore]
    public List<string> DerivedChannelNames => ParserKind switch
    {
        ParserKind.Ctmbs3X2000 => ["D", "H", "Z", "T"],
        _ => Category == ProtocolCategory.Binary && UsesSegments
            ? Segments.Where(s => s.Type == SegmentType.DataField)
                      .OrderBy(s => s.ChannelIndex)
                      .Select(s => s.Name).ToList()
            : FieldMappings.OrderBy(f => f.ChannelIndex).Select(f => f.Name).ToList(),
    };

    [JsonIgnore]
    public List<string> DerivedChannelUnits => ParserKind == ParserKind.Ctmbs3X2000
        ? ["nT", "nT", "nT", "°C"]
        : Category == ProtocolCategory.Binary && UsesSegments
            ? Segments.Where(s => s.Type == SegmentType.DataField).OrderBy(s => s.ChannelIndex).Select(s => s.Unit).ToList()
            : FieldMappings.OrderBy(f => f.ChannelIndex).Select(f => f.Unit).ToList();

    /// <summary>连接前校验，避免将缺失、重叠或非数值通道静默写成零。</summary>
    public void Validate()
    {
        if (ParserKind == ParserKind.Ctmbs3X2000) return;
        if (!Enum.IsDefined(Category) || !Enum.IsDefined(ParserKind))
            throw new ArgumentException("不支持的协议类别或解析器");
        if (AsciiSkipLines < 0) throw new ArgumentException("跳过行数不能为负数");
        if (Category == ProtocolCategory.Ascii && AsciiLineEnding is not ("\r\n" or "\n" or "\r"))
            throw new ArgumentException("ASCII 行结束符应为 CRLF、LF 或 CR");
        var indices = Category == ProtocolCategory.Binary && UsesSegments
            ? Segments.Where(s => s.Type == SegmentType.DataField).Select(s => s.ChannelIndex).ToArray()
            : FieldMappings.Select(f => f.ChannelIndex).ToArray();
        if (!indices.Order().SequenceEqual(Enumerable.Range(0, indices.Length)))
            throw new ArgumentException("通道索引必须从 0 连续且唯一，删除字段后请重新排序");
        if (FieldMappings.Any(f => f.ByteOffset < 0 || !double.IsFinite(f.Scale) || !double.IsFinite(f.Offset)))
            throw new ArgumentException("字段位置、倍率或偏移无效");
        if (Category != ProtocolCategory.Binary) return;
        if (UsesSegments)
        {
            ComputeSegmentOffsets();
            if (TotalFrameLength <= 0 || TotalFrameLength > 131072)
                throw new ArgumentException("二进制帧长度必须为 1~131072 字节");
            foreach (var kind in new[] { SegmentType.Header, SegmentType.Tail, SegmentType.LengthField, SegmentType.Checksum })
                if (Segments.Count(s => s.Type == kind) > 1) throw new ArgumentException($"只支持一个 {kind} 段");
            foreach (var s in Segments)
            {
                if (s.ByteCount <= 0 || !double.IsFinite(s.Scale) || !double.IsFinite(s.Offset))
                    throw new ArgumentException($"帧段 {s.Name} 的长度、倍率或偏移无效");
                if (s.Type == SegmentType.LengthField && s.ByteCount is not (1 or 2))
                    throw new ArgumentException("长度字段必须为 1 或 2 字节");
                if (s.Type == SegmentType.DataField && s.ByteCount != FrameSegment.GetByteCountForDataType(s.DataType))
                    throw new ArgumentException($"字段 {s.Name} 的字节数不匹配类型");
                if ((s.Type is SegmentType.Header or SegmentType.Tail || s.ValidateFixedValue)
                    && HexToBytes(s.FixedHexValue).Length != s.ByteCount)
                    throw new ArgumentException($"帧段 {s.Name} 的固定值长度不匹配");
                if (s.Type == SegmentType.Checksum && (s.ChecksumStartIndex < 0 || s.ChecksumStartIndex >= Segments.IndexOf(s)))
                    throw new ArgumentException("校验范围起始段必须位于校验段之前");
                if (s.Type == SegmentType.Checksum && s.ByteCount != (s.ChecksumAlgorithm == ChecksumAlgorithm.CRC16 ? 2 : 1))
                    throw new ArgumentException("CRC16 校验段必须为 2 字节，其他校验段必须为 1 字节");
            }
            if (Segments.Any(s => s.Type == SegmentType.Header && s != Segments[0])
                || Segments.Any(s => s.Type == SegmentType.Tail && s != Segments[^1]))
                throw new ArgumentException("帧头必须在首段，帧尾必须在末段");
        }
        else if (HasLengthByte && LengthByteCount is not (1 or 2))
            throw new ArgumentException("长度字段必须为 1 或 2 字节");
    }

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
        // 深度需容纳最深的一条链：
        // root → Commands[] → CommandGroup → Commands[] → DeviceCommand
        //      → Parameters[] → CommandParameter → EnumMap[] → EnumChoice = 9 层。
        // 留一层余量。仍保留上限以防恶意构造的深层嵌套 JSON。
        MaxDepth = 12,
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
    /// 创建 CCT-5 磁梯度仪 RS422 ADC 数据上传帧（TYPE=A1）的固有协议。
    /// 固定 43 字节：HEAD(FF5A) + TYPE(A1) + LEN(24) + device_id(4) + 8×float32(LE) + CRC16 + TAIL(33)。
    /// CRC-16/IBM(ARC)，帧内高字节在前；计算范围为 TYPE+LEN+PAYLOAD（从 TYPE 段起到 CRC 前）。
    /// 通道顺序按协议文档：X1,X2,Y1,Y2,Z1,Z2 + 两路悬空 ADC6/ADC7。device_id 不作为通道展示。
    /// </summary>
    public static ProtocolConfig CreateCct5Gradiometer()
    {
        var config = new ProtocolConfig
        {
            Name = "CCT-5 磁梯度仪 (FF5A, 8通道 Float, CRC16/IBM)",
            Category = ProtocolCategory.Binary,
            Segments =
            [
                new() { Type = SegmentType.Header,   Name = "帧头",     ByteCount = 2, FixedHexValue = "FF5A" },
                new() { Type = SegmentType.Padding,  Name = "类型(A1)", ByteCount = 1, FixedHexValue = "A1" },
                new() { Type = SegmentType.Padding,  Name = "长度(24)", ByteCount = 1, FixedHexValue = "24" },
                new() { Type = SegmentType.Padding,  Name = "设备编号", ByteCount = 4 },
                new() { Type = SegmentType.DataField, Name = "X1", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 0 },
                new() { Type = SegmentType.DataField, Name = "X2", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 1 },
                new() { Type = SegmentType.DataField, Name = "Y1", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 2 },
                new() { Type = SegmentType.DataField, Name = "Y2", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 3 },
                new() { Type = SegmentType.DataField, Name = "Z1", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 4 },
                new() { Type = SegmentType.DataField, Name = "Z2", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 5 },
                new() { Type = SegmentType.DataField, Name = "ADC6", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 6 },
                new() { Type = SegmentType.DataField, Name = "ADC7", ByteCount = 4, DataType = FieldDataType.Float, ChannelIndex = 7 },
                // CRC 计算从索引 1（类型段，偏移 2）开始，覆盖 TYPE+LEN+PAYLOAD
                new() { Type = SegmentType.Checksum, Name = "CRC", ByteCount = 2,
                        ChecksumAlgorithm = ChecksumAlgorithm.CRC16, Crc16Variant = Crc16Variant.Ibm,
                        ChecksumBigEndian = true, ChecksumStartIndex = 1 },
                new() { Type = SegmentType.Tail, Name = "帧尾", ByteCount = 1, FixedHexValue = "33" },
            ],
        };
        config.ComputeSegmentOffsets();
        return config;
    }

    /// <summary>
    /// 构建 ZDZ_C08 / CTMBS-3 数采卡 101 字节上传帧的段布局。
    /// <paramref name="magneticOnly"/>=true 时只映射 X1..Z2 六个磁分量，其余数据段退化为 Padding。
    /// </summary>
    /// <remarks>
    /// 帧结构（依据《三分量梯度数采卡通信协议》表 1，与《ZDZ_C08 采集模块通信协议》存储记录同构）：
    /// FF5A + 信息ID(AD00) + 长度(5C00=92) + 92 字节数据区 + CRC16(2) + 帧尾(33)。
    /// 信息 ID 与长度字段值恒定，打开 ValidateFixedValue 作为帧同步锚点 —— 92 字节浮点载荷中
    /// 偶然出现 FF5A 的概率不低，仅靠帧头帧尾定长容易误锁。
    /// CRC 段按 Padding 处理，不校验：文档称"序号 2~26 之间所有字节的 CRC-16"，多项式 0x8005，
    /// 但按该范围以 ARC/MSB-0x8005 × init{0000,FFFF} 组合计算均与文档样本包的 BD 67 不符；
    /// 进一步对 init 做 GF(2) 线性反解并跨 ZDZ 实测样本交叉验证，无一致解。参数待固件侧确认。
    /// </remarks>
    private static List<FrameSegment> BuildZdzC08Segments(bool magneticOnly)
    {
        int ch = 0;
        // 数据段：非 magneticOnly 时映射为通道，否则退化为同宽度的 Padding
        FrameSegment Data(string name, FieldDataType type, bool magnetic = false)
        {
            int width = FrameSegment.GetByteCountForDataType(type);
            if (magneticOnly && !magnetic)
                return new() { Type = SegmentType.Padding, Name = name, ByteCount = width };
            return new()
            {
                Type = SegmentType.DataField,
                Name = name,
                ByteCount = width,
                DataType = type,
                ChannelIndex = ch++,
            };
        }

        return
        [
            new() { Type = SegmentType.Header, Name = "帧头", ByteCount = 2, FixedHexValue = "FF5A" },
            new() { Type = SegmentType.Padding, Name = "信息ID", ByteCount = 2,
                    FixedHexValue = "AD00", ValidateFixedValue = true },
            new() { Type = SegmentType.Padding, Name = "数据长度(92)", ByteCount = 2,
                    FixedHexValue = "5C00", ValidateFixedValue = true },

            // 梯度仪六分量，单位 nT
            Data("X1", FieldDataType.Float, magnetic: true),
            Data("Y1", FieldDataType.Float, magnetic: true),
            Data("Z1", FieldDataType.Float, magnetic: true),
            Data("X2", FieldDataType.Float, magnetic: true),
            Data("Y2", FieldDataType.Float, magnetic: true),
            Data("Z2", FieldDataType.Float, magnetic: true),

            // 设备端计算的磁梯度值，等于 X1-X2 / Y1-Y2 / Z1-Z2
            Data("ΔX", FieldDataType.Float),
            Data("ΔY", FieldDataType.Float),
            Data("ΔZ", FieldDataType.Float),

            Data("GPS纬度", FieldDataType.Double),
            Data("GPS经度", FieldDataType.Double),

            Data("加速度X", FieldDataType.Float),
            Data("加速度Y", FieldDataType.Float),
            Data("加速度Z", FieldDataType.Float),

            Data("陀螺仪X", FieldDataType.Float),
            Data("陀螺仪Y", FieldDataType.Float),
            Data("陀螺仪Z", FieldDataType.Float),

            Data("磁力仪X", FieldDataType.Float),
            Data("磁力仪Y", FieldDataType.Float),
            Data("磁力仪Z", FieldDataType.Float),

            Data("入水深度", FieldDataType.Float),

            new() { Type = SegmentType.Padding, Name = "CRC(不校验)", ByteCount = 2 },
            new() { Type = SegmentType.Tail, Name = "帧尾", ByteCount = 1, FixedHexValue = "33" },
        ];
    }

    /// <summary>
    /// 创建"磁梯度数采卡-pt"内置协议：101 字节定长帧，21 个通道全展开
    /// （6 磁分量 + 3 梯度 + 2 GPS + 3 加速度 + 3 陀螺 + 3 磁力仪 + 深度）。
    /// </summary>
    public static ProtocolConfig CreateZdzC08()
    {
        var config = new ProtocolConfig
        {
            Name = "磁梯度数采卡-pt",
            Category = ProtocolCategory.Binary,
            Segments = BuildZdzC08Segments(magneticOnly: false),
            Commands = ZdzC08Commands.CreateGroups(),
            Notes = "ZDZ_C08 / CTMBS-3 数采卡 101 字节上传帧，全字段。串口 115200 8N1。"
                  + "磁分量与梯度单位 nT，加速度 m/s²，陀螺 °/s，深度 m。CRC 段不校验。",
        };
        config.ComputeSegmentOffsets();
        return config;
    }

    /// <summary>
    /// 创建"磁梯度数采卡-pt (仅磁场6通道)"内置协议：帧结构同 <see cref="CreateZdzC08"/>，
    /// 但只映射 X1..Z2，适合只关心磁场、不希望图表被 21 条曲线塞满的场景。
    /// </summary>
    public static ProtocolConfig CreateZdzC08MagneticOnly()
    {
        var config = new ProtocolConfig
        {
            Name = "磁梯度数采卡-pt (仅磁场6通道)",
            Category = ProtocolCategory.Binary,
            Segments = BuildZdzC08Segments(magneticOnly: true),
            Commands = ZdzC08Commands.CreateGroups(),
            Notes = "ZDZ_C08 / CTMBS-3 数采卡 101 字节上传帧，仅映射 X1/Y1/Z1/X2/Y2/Z2（单位 nT）。"
                  + "其余字段按保留区跳过。CRC 段不校验。",
        };
        config.ComputeSegmentOffsets();
        return config;
    }

    /// <summary>
    /// 创建 CTMBS-3-X2000 台站式三分量数采（eq_precursors）固有协议。
    /// 走 TCP 端口 81，请求帧 GET /&lt;len&gt;+&lt;deviceId&gt;+&lt;mnemonic&gt;[+params] /http/1.1
    /// （自参考长度，见 §3.3）；实时数据帧 $&lt;L&gt;\n&lt;L digits&gt;&lt;payload&gt;\nack\n。
    /// 解析为 D/H/Z/T 四通道读数。内置命令仅含实时数据流所需：lin / dat+0 / stp。
    /// 需先 lin 登录再 dat+0 启动推送；登录默认 administrator / 01234567。
    /// </summary>
    public static ProtocolConfig CreateCtmbs3X2000()
    {
        var config = new ProtocolConfig
        {
            Name = "CTMBS-3-X2000 台站式三分量",
            Category = ProtocolCategory.Ascii,
            ParserKind = ParserKind.Ctmbs3X2000,
            Commands = Ctmbs3X2000Commands.CreateGroups(),
            Notes = "CTMBS-3-X2000 台站式三分量数采（eq_precursors）实时数据协议，TCP 端口 81。"
                  + "实时帧 $<L>\\n<L digits><payload>\\nack\\n，payload = HHMMSS 台站码 仪器ID 采样率 "
                  + "4 ch1..ch4码 ch1..ch4值，解析 D/H/Z/T 4 通道。"
                  + "需先 lin 登录再 dat+0 启动 1Hz 推送，stp 停止。登录默认 administrator/01234567。"
                  + "本期仅实时数据接收，HTTP 令牌下载与其余命令留待后续。",
        };
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
        int dataOffset = 0;
        foreach (var field in FieldMappings.OrderBy(f => f.ByteOffset))
        {
            if (field.ByteOffset < dataOffset)
                throw new ArgumentException("旧协议字段重叠，不能自动转换为顺序帧段");
            if (field.ByteOffset > dataOffset)
                Segments.Add(new FrameSegment { Type = SegmentType.Padding, Name = "保留区", ByteCount = field.ByteOffset - dataOffset });
            Segments.Add(new FrameSegment
            {
                Type = SegmentType.DataField,
                Name = field.Name,
                Unit = field.Unit,
                ByteCount = field.ByteSize,
                DataType = field.DataType,
                BigEndian = field.BigEndian,
                ChannelIndex = field.ChannelIndex,
                Scale = field.Scale,
                Offset = field.Offset,
            });
            dataOffset = field.ByteOffset + field.ByteSize;
        }
        if (!HasLengthByte && FixedDataLength > dataOffset)
            Segments.Add(new FrameSegment { Type = SegmentType.Padding, Name = "保留区", ByteCount = FixedDataLength - dataOffset });

        // 校验
        if (Checksum != ChecksumType.None)
        {
            ComputeSegmentOffsets();
            int checksumStartIndex = Segments.FindIndex(s => s.ComputedOffset == ChecksumStartOffset);
            if (checksumStartIndex < 0)
            {
                // 字节起点落在字段内部时，段索引无法无损表达；继续使用兼容解析器。
                Segments.Clear();
                return;
            }
            Segments.Add(new FrameSegment
            {
                Type = SegmentType.Checksum,
                Name = "校验",
                ByteCount = Checksum == ChecksumType.CRC16 ? 2 : 1,
                ChecksumAlgorithm = Checksum switch
                {
                    ChecksumType.Xor => ChecksumAlgorithm.Xor,
                    ChecksumType.Sum8 => ChecksumAlgorithm.Sum8,
                    ChecksumType.CRC16 => ChecksumAlgorithm.CRC16,
                    _ => ChecksumAlgorithm.Xor,
                },
                ChecksumStartIndex = checksumStartIndex,
                Crc16Variant = Crc16Variant,
                ChecksumBigEndian = ChecksumBigEndian,
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
