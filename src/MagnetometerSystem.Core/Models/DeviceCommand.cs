namespace MagnetometerSystem.Core.Models;

public enum CommandEncoding
{
    /// <summary>ASCII 模板模式：模板中 {key} 替换后 UTF-8 编码发送</summary>
    AsciiTemplate,

    /// <summary>二进制帧模式：[帧头?] + [按参数编码的数据帧] + [校验?] + [帧尾?]</summary>
    BinaryFrame,
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
    public List<string> EnumOptions { get; set; } = new();

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

    public CommandEncoding Encoding { get; set; } = CommandEncoding.AsciiTemplate;

    // ASCII 模板用
    public string Template { get; set; } = "";
    public bool AppendNewline { get; set; } = true;

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
