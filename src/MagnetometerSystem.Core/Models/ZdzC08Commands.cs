namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 《ZDZ_C08 采集模块通信协议》定义的内置命令集。
/// 全部为无帧头/帧尾/校验的裸字节指令，命令码本身即前缀。
/// </summary>
public static class ZdzC08Commands
{
    /// <summary>采样率标识表（文档"采样率（HZ）"表；原表 1250HZ 为 125HZ 笔误）</summary>
    public static readonly (string Label, long Id)[] SampleRates =
    [
        ("1 Hz", 1), ("10 Hz", 10), ("25 Hz", 25), ("50 Hz", 50),
        ("125 Hz", 125), ("250 Hz", 250), ("500 Hz", 255),
    ];

    /// <summary>波特率标识表（文档"波特率（HZ）"表）</summary>
    public static readonly (string Label, long Id)[] BaudRates =
    [
        ("9600", 1), ("38400", 2), ("115200", 3), ("230400", 4), ("2000000", 255),
    ];

    /// <summary>磁场灵敏度/零点的通道号：0~5 依次对应两组三分量</summary>
    private static readonly (string Label, long Id)[] MagChannels =
    [
        ("0 - X1", 0), ("1 - Y1", 1), ("2 - Z1", 2),
        ("3 - X2", 3), ("4 - Y2", 4), ("5 - Z2", 5),
    ];

    private static List<EnumChoice> Choices((string Label, long Id)[] items) =>
        [.. items.Select(i => new EnumChoice(i.Label, i.Id))];

    /// <summary>无参数裸指令</summary>
    private static DeviceCommand Raw(string name, string hex, string description,
        bool requiresIsolatedTransfer = false) => new()
    {
        Name = name,
        Description = description,
        Encoding = CommandEncoding.BinaryFrame,
        FrameHeader = hex,
        Checksum = ChecksumKind.None,
        RequiresIsolatedTransfer = requiresIsolatedTransfer,
    };

    /// <summary>形如 CC Fx 00 00 00 XX 的单字节枚举参数指令</summary>
    private static DeviceCommand ByteEnum(
        string name, string prefixHex, string paramName, string paramKey,
        (string Label, long Id)[] options, string defaultLabel, string description) => new()
    {
        Name = name,
        Description = description,
        Encoding = CommandEncoding.BinaryFrame,
        FrameHeader = prefixHex,
        Checksum = ChecksumKind.None,
        Parameters =
        [
            new()
            {
                Name = paramName,
                Key = paramKey,
                Type = CommandParameterType.U8,
                EnumMap = Choices(options),
                DefaultValue = defaultLabel,
            },
        ],
    };

    /// <summary>
    /// 构建 ZDZ_C08 内置命令组。
    /// </summary>
    /// <remarks>
    /// 文档表格中 "CC 57 4D 4B 00 00(通道号)" 字面为 6 字节但括号标注在末两字节上，
    /// 此处按 "CC 57 4D 4B 00" + 通道号(1 字节) 理解，共 6 字节。
    /// </remarks>
    public static List<CommandGroup> CreateGroups() =>
    [
        new()
        {
            Name = "采集控制",
            IsBuiltIn = true,
            Commands =
            [
                Raw("启动采集", "80 8F", "开始输出实时数据帧"),
                Raw("停止采集", "00 8F", "停止输出实时数据帧"),
                ByteEnum("修改采样率", "CC F4 00 00 00", "采样率", "rate",
                    SampleRates, "25 Hz",
                    "设备回显同一指令表示成功。修改采样率后存储抽点率会被复位为 1。"),
                ByteEnum("修改波特率", "CC F5 00 00 00", "波特率", "baud",
                    BaudRates, "115200",
                    "设备回显同一指令表示成功。生效后需把上位机串口波特率改为相同值，否则将无法通信。"),
                Raw("打开滤波", "FE EF", "设备回显 FE EF 表示成功"),
                Raw("关闭滤波", "FE 00", "设备回显 FE 00 表示成功"),
            ],
        },
        new()
        {
            Name = "参数标定",
            IsBuiltIn = true,
            Commands =
            [
                new()
                {
                    Name = "设置磁场灵敏度和零点",
                    Description = "CC 57 4D 4B 00 + 通道号 + 灵敏度(double) + 零点(double)，设备回显同一指令表示成功",
                    Encoding = CommandEncoding.BinaryFrame,
                    FrameHeader = "CC 57 4D 4B 00",
                    Checksum = ChecksumKind.None,
                    Parameters =
                    [
                        new()
                        {
                            Name = "通道号", Key = "ch", Type = CommandParameterType.U8,
                            EnumMap = Choices(MagChannels), DefaultValue = "0 - X1",
                        },
                        new()
                        {
                            Name = "灵敏度", Key = "sensitivity", Type = CommandParameterType.Float64,
                            DefaultValue = "1", Endian = Endianness.LittleEndian,
                        },
                        new()
                        {
                            Name = "零点", Key = "zero", Type = CommandParameterType.Float64,
                            DefaultValue = "0", Endian = Endianness.LittleEndian,
                        },
                    ],
                },
                new()
                {
                    Name = "查询磁场灵敏度和零点",
                    Description = "CC 52 4D 4B 00 + 通道号。设备返回原指令 + 灵敏度(double) + 零点(double)，"
                                + "响应内容目前仅显示在通信日志中。",
                    Encoding = CommandEncoding.BinaryFrame,
                    FrameHeader = "CC 52 4D 4B 00",
                    Checksum = ChecksumKind.None,
                    Parameters =
                    [
                        new()
                        {
                            Name = "通道号", Key = "ch", Type = CommandParameterType.U8,
                            EnumMap = Choices(MagChannels), DefaultValue = "0 - X1",
                        },
                    ],
                },
                new()
                {
                    Name = "设置正交度系数",
                    Description = "CC 57 5A 4A 00 00 + 30 个 double（X1Y1Z1、X2Y2Z2 两组 6 行 5 列矩阵，"
                                + "先行后列填充，共 240 字节）。当前需以 Hex 直接粘贴，矩阵编辑面板待实现。",
                    Encoding = CommandEncoding.BinaryFrame,
                    FrameHeader = "CC 57 5A 4A 00 00",
                    Checksum = ChecksumKind.None,
                    Parameters =
                    [
                        new()
                        {
                            Name = "矩阵数据", Key = "matrix", Type = CommandParameterType.HexBytes,
                            ByteLength = 240, DefaultValue = "",
                        },
                    ],
                },
                Raw("读取正交度系数", "CC 52 5A 4A 00 00",
                    "设备返回原指令 + 矩阵数据，响应内容目前仅显示在通信日志中"),
            ],
        },
        new()
        {
            Name = "存储管理",
            IsBuiltIn = true,
            Commands =
            [
                ByteEnum("修改存储抽点率", "CC F6 00 00 00", "抽点率", "decimation",
                    [("1", 1), ("2", 2), ("5", 5), ("10", 10), ("25", 25), ("50", 50), ("100", 100)],
                    "1",
                    "每采集 XX 点存储 1 个点。修改采样率后该值会被设备复位为 1。"),
                Raw("读取存储目录", "CD 00 6D 6C",
                    "设备逐条返回 6D 75 6C 75 + UTC(4B) + 块数(4B)。"
                    + "实测 UTC 与块数均为大端，且固件不发送文档所列的 FF ED 0D 0A 结束标识。"),
                new()
                {
                    Name = "选取数据时间",
                    Description = "DA 00 6D 6C + 开始 UTC + 结束 UTC（均取自读取目录的返回值，开始 ≤ 结束）。"
                                + "设备回显同一指令表示时间正确。导出单条目录项时两者填相同值。",
                    Encoding = CommandEncoding.BinaryFrame,
                    FrameHeader = "DA 00 6D 6C",
                    Checksum = ChecksumKind.None,
                    Parameters =
                    [
                        new()
                        {
                            Name = "开始UTC", Key = "start", Type = CommandParameterType.HexBytes,
                            ByteLength = 4, DefaultValue = "",
                        },
                        new()
                        {
                            Name = "结束UTC", Key = "end", Type = CommandParameterType.HexBytes,
                            ByteLength = 4, DefaultValue = "",
                        },
                    ],
                },
                Raw("读取存储数据", "90 9F",
                    "暂不可用：需要独立的设备存储下载模式，避免历史记录混入实时会话。"
                    + "设备按 512 字节包连续发送，以 ED 9F 00 00 结束。",
                    requiresIsolatedTransfer: true),
                Raw("停止读取存储", "00 9F", "中断正在进行的存储数据传输"),
            ],
        },
    ];
}
