using System.Text.Json.Serialization;

namespace MagnetometerSystem.Core.Models;

public enum CommandEncoding
{
    /// <summary>ASCII 模板模式：模板中 {key} 替换后 UTF-8 编码发送</summary>
    AsciiTemplate,

    /// <summary>二进制帧模式：[帧头?] + [按参数编码的数据帧] + [校验?] + [帧尾?]</summary>
    BinaryFrame,

    /// <summary>
    /// CTMBS-3-X2000 文本帧模式：构造
    /// GET /&lt;len&gt;+&lt;deviceId&gt;+&lt;mnemonic&gt;[+&lt;param&gt;...] /http/1.1，
    /// &lt;len&gt; 为自参考长度（§3.3）。由 <see cref="Communication.Ctmbs3X2000FrameBuilder"/> 构建。
    /// </summary>
    CtmbsRequest,
}

public enum ChecksumKind
{
    None,
    Sum8,    // 8 位累加和
    Xor8,    // 8 位异或
    Crc16,   // CRC-16/MODBUS
}

public enum Endianness
{
    LittleEndian,
    BigEndian,
}

public enum CommandParameterType
{
    // ASCII 模板用
    String,
    Int,
    Double,
    Enum,

    // BinaryFrame 用（二进制编码）
    U8,
    U16,
    U32,
    I8,
    I16,
    I32,
    Float32,
    Float64,
    /// <summary>任意字节串（用户输入 hex 字符串，可指定 ByteLength 做长度校验）</summary>
    HexBytes,
}

/// <summary>二进制枚举参数的一个可选项：界面显示 <see cref="Label"/>，实际编码 <see cref="Value"/></summary>
public class EnumChoice
{
    public string Label { get; set; } = "";
    public long Value { get; set; }

    public EnumChoice() { }

    public EnumChoice(string label, long value)
    {
        Label = label;
        Value = value;
    }

    /// <summary>下拉框直接绑定对象时的显示文本</summary>
    public override string ToString() => Label;
}

/// <summary>设备命令参数定义</summary>
public class CommandParameter
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public CommandParameterType Type { get; set; } = CommandParameterType.String;
    public string DefaultValue { get; set; } = "";
    public string? Unit { get; set; }
    public double? Min { get; set; }
    public double? Max { get; set; }
    /// <summary>枚举可选值（ASCII 模板用；二进制参数请用 <see cref="EnumMap"/>）</summary>
    public List<string> EnumOptions { get; set; } = new();

    /// <summary>
    /// 二进制枚举映射：显示名 → 实际数值。
    /// 用于 U8/U16/U32 等数值型参数需要以下拉方式呈现的场合
    /// （如采样率 "25 Hz" → 25、波特率 "2000000" → 255）。
    /// 非空时 UI 出下拉框，编码时把选中项映射回数值再按 <see cref="Type"/> 编码。
    /// </summary>
    public List<EnumChoice> EnumMap { get; set; } = new();

    /// <summary>字节序（仅 U16/U32/I16/I32/Float32/Float64 生效）</summary>
    public Endianness Endian { get; set; } = Endianness.LittleEndian;

    /// <summary>字节长度（仅 HexBytes 生效，null 表示不校验长度）</summary>
    public int? ByteLength { get; set; }
}

/// <summary>设备命令定义</summary>
public class DeviceCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>响应需要独立传输处理，不能进入实时采集；当前普通命令发送入口拒绝发送。</summary>
    public bool RequiresIsolatedTransfer { get; set; }

    public CommandEncoding Encoding { get; set; } = CommandEncoding.AsciiTemplate;

    // ASCII 模板用
    public string Template { get; set; } = "";
    public bool AppendNewline { get; set; } = true;

    /// <summary>显式响应匹配；留空仅显示收到的数据，不确认设备执行成功。</summary>
    public string ExpectedResponse { get; set; } = "";
    public bool ExpectedResponseIsHex { get; set; }

    // BinaryFrame 用（全部可选）
    public string FrameHeader { get; set; } = "";  // hex, e.g. "AA 55"
    public string FrameTail { get; set; } = "";    // hex, e.g. "55 AA"
    public ChecksumKind Checksum { get; set; } = ChecksumKind.None;

    public List<CommandParameter> Parameters { get; set; } = new();
}

public class CommandGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public List<DeviceCommand> Commands { get; set; } = new();

    /// <summary>
    /// 是否为协议自带的内置组。内置组随协议切换出现/消失，不参与用户目录持久化，
    /// 也不允许改名/删除/增删命令 —— 否则改动会在下次加载协议时被覆盖。
    /// 不序列化：该标志由协议装配时赋予，不应从用户目录文件中读入。
    /// </summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; set; }
}

public class CommandCatalog
{
    public List<CommandGroup> Groups { get; set; } = new();

    public static CommandCatalog CreateDefault()
    {
        return new CommandCatalog
        {
            Groups = new List<CommandGroup>
            {
                new()
                {
                    Name = "基本命令",
                    Commands = new List<DeviceCommand>
                    {
                        new() { Name = "查询状态", Description = "查询设备状态", Template = "GET_STATUS" },
                        new() { Name = "读取序列号", Description = "读取设备序列号", Template = "GET_SN" },
                        new() { Name = "软件复位", Description = "设备软复位", Template = "RESET" },
                    }
                },
                new()
                {
                    Name = "采集控制",
                    Commands = new List<DeviceCommand>
                    {
                        new() { Name = "开始采集", Description = "启动数据采集", Template = "START" },
                        new() { Name = "停止采集", Description = "停止数据采集", Template = "STOP" },
                        new()
                        {
                            Name = "设置采样率 (ASCII)",
                            Description = "ASCII 方式：SET_RATE {rate}",
                            Encoding = CommandEncoding.AsciiTemplate,
                            Template = "SET_RATE {rate}",
                            Parameters = new()
                            {
                                new() {
                                    Name = "采样率", Key = "rate",
                                    Type = CommandParameterType.Double,
                                    DefaultValue = "100", Unit = "Hz",
                                    Min = 0.1, Max = 500,
                                }
                            }
                        },
                        new()
                        {
                            Name = "设置采样率 (二进制)",
                            Description = "二进制帧示例：AA55 + float32 + CRC16 + 55AA",
                            Encoding = CommandEncoding.BinaryFrame,
                            FrameHeader = "AA 55",
                            FrameTail = "55 AA",
                            Checksum = ChecksumKind.Crc16,
                            Parameters = new()
                            {
                                new() {
                                    Name = "采样率", Key = "rate",
                                    Type = CommandParameterType.Float32,
                                    DefaultValue = "100.0", Unit = "Hz",
                                    Endian = Endianness.LittleEndian,
                                }
                            }
                        },
                    }
                }
            }
        };
    }
}
