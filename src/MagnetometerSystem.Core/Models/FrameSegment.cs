using System.ComponentModel;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MagnetometerSystem.Core.Protocol;

namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 帧段类型
/// </summary>
public enum SegmentType
{
    /// <summary>帧头（固定字节）</summary>
    Header,

    /// <summary>长度字段</summary>
    LengthField,

    /// <summary>数据字段（通道数据）</summary>
    DataField,

    /// <summary>校验字段</summary>
    Checksum,

    /// <summary>帧尾（固定字节）</summary>
    Tail,

    /// <summary>填充/保留字节</summary>
    Padding,
}

/// <summary>
/// 帧段：描述帧中的一个连续字节段，用于"搭积木"式帧格式配置
/// </summary>
public class FrameSegment : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>段类型</summary>
    public SegmentType Type { get; set; }

    /// <summary>显示名称（如 "X通道"、"帧头"）</summary>
    public string Name { get; set; } = "";

    /// <summary>该段占用字节数</summary>
    private int _byteCount;
    public int ByteCount
    {
        get => _byteCount;
        set
        {
            int clamped = Math.Max(1, value);
            if (_byteCount != clamped)
            {
                _byteCount = clamped;
                OnPropertyChanged(nameof(ByteCount));
                // 帧头/帧尾/填充：ByteCount 变化后重新校验 Hex 长度
                if (Type is SegmentType.Header or SegmentType.Tail or SegmentType.Padding)
                {
                    FixedHexValue = _fixedHexValue; // 触发截断/补零
                }
            }
        }
    }

    // ---- Header/Tail/Padding 专用 ----

    /// <summary>
    /// 固定字节值（Hex），如 "AA55"。
    /// setter 自动：转大写、去除非法字符、按 ByteCount 截断或补零。
    /// </summary>
    private string _fixedHexValue = "";
    public string FixedHexValue
    {
        get => _fixedHexValue;
        set
        {
            // 去空格、0x 前缀，转大写，只保留合法 hex 字符
            var raw = (value ?? "").Replace(" ", "").Replace("0x", "").Replace("0X", "");
            raw = Regex.Replace(raw, "[^0-9A-Fa-f]", "").ToUpperInvariant();

            // 按 ByteCount 截断或右补零
            int expectedLen = ByteCount * 2;
            if (expectedLen > 0)
            {
                if (raw.Length > expectedLen)
                    raw = raw[..expectedLen];
                else if (raw.Length < expectedLen)
                    raw = raw.PadRight(expectedLen, '0');
            }

            if (_fixedHexValue != raw)
            {
                _fixedHexValue = raw;
                OnPropertyChanged(nameof(FixedHexValue));
            }
        }
    }

    // ---- DataField 专用 ----

    /// <summary>数据类型</summary>
    private FieldDataType _dataType = FieldDataType.Float;
    public FieldDataType DataType
    {
        get => _dataType;
        set
        {
            if (_dataType != value)
            {
                _dataType = value;
                OnPropertyChanged(nameof(DataType));
                // DataField 类型时自动计算 ByteCount
                if (Type == SegmentType.DataField)
                {
                    ByteCount = GetByteCountForDataType(value);
                }
            }
        }
    }

    /// <summary>是否为大端序</summary>
    public bool BigEndian { get; set; } = false;

    /// <summary>映射到哪个通道</summary>
    public int ChannelIndex { get; set; }

    /// <summary>缩放系数</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>偏移量</summary>
    public double Offset { get; set; } = 0.0;

    // ---- LengthField 专用 ----

    /// <summary>长度值是否为大端序</summary>
    public bool LengthBigEndian { get; set; } = false;

    // ---- Checksum 专用 ----

    /// <summary>校验算法</summary>
    private ChecksumAlgorithm _checksumAlgorithm = ChecksumAlgorithm.Xor;
    public ChecksumAlgorithm ChecksumAlgorithm
    {
        get => _checksumAlgorithm;
        set
        {
            if (_checksumAlgorithm != value)
            {
                _checksumAlgorithm = value;
                OnPropertyChanged(nameof(ChecksumAlgorithm));
            }
        }
    }

    /// <summary>校验计算起始段索引（0=从第一段开始）</summary>
    public int ChecksumStartIndex { get; set; } = 0;

    /// <summary>CRC-16 变体（仅当 ChecksumAlgorithm=CRC16 时有效）。必须与设备端一致。</summary>
    public Crc16Variant Crc16Variant { get; set; } = Crc16Variant.Modbus;

    /// <summary>CRC-16 校验值字节序：false=低字节在前（Modbus RTU 习惯），true=高字节在前。CRC16 时该段 ByteCount 应为 2。</summary>
    public bool ChecksumBigEndian { get; set; } = false;

    // ---- 自动计算 ----

    /// <summary>由段顺序自动计算的字节偏移</summary>
    [JsonIgnore]
    public int ComputedOffset { get; set; }

    /// <summary>根据数据类型获取字节数</summary>
    public static int GetByteCountForDataType(FieldDataType dataType) => dataType switch
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
/// 校验算法（用于 FrameSegment，与旧的 ChecksumType 对应）
/// </summary>
public enum ChecksumAlgorithm
{
    Xor,
    Sum8,
    CRC16,
}
