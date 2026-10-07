using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MagnetometerSystem.App.Converters;

/// <summary>枚举值显示为中文；未登记的值显示原名。界面上不直接显示英文枚举名。</summary>
public class EnumDisplayConverter : IValueConverter
{
    private static readonly Dictionary<string, string> Names = new()
    {
        ["ConnectionType.Serial"] = "串口",
        ["ConnectionType.Tcp"] = "TCP",
        ["ProtocolCategory.Ascii"] = "ASCII 文本",
        ["ProtocolCategory.Binary"] = "二进制",
        ["FilterType.MovingAverage"] = "移动平均",
        ["FilterType.Median"] = "中值",
        ["ExportDataSource.Raw"] = "原始值",
        ["ExportDataSource.Corrected"] = "校正值",
        ["ExportDataSource.RawAndCorrected"] = "原始值 + 校正值",
        ["PlaybackState.Ready"] = "就绪",
        ["PlaybackState.Loading"] = "加载中",
        ["PlaybackState.Playing"] = "播放中",
        ["PlaybackState.Paused"] = "已暂停",
        ["PlaybackState.Completed"] = "已播完",
        ["SegmentType.Header"] = "帧头",
        ["SegmentType.LengthField"] = "长度",
        ["SegmentType.DataField"] = "数据",
        ["SegmentType.Checksum"] = "校验",
        ["SegmentType.Tail"] = "帧尾",
        ["SegmentType.Padding"] = "填充",
        ["ChecksumAlgorithm.Xor"] = "XOR 异或",
        ["ChecksumAlgorithm.Sum8"] = "累加和",
        ["ChecksumAlgorithm.CRC16"] = "CRC-16",
        ["ChecksumType.None"] = "无校验",
        ["ChecksumType.Xor"] = "XOR 异或",
        ["ChecksumType.Sum8"] = "累加和",
        ["ChecksumType.CRC16"] = "CRC-16",
        ["SensorType.Generic"] = "通用",
        ["SensorType.SingleAxisFluxgate"] = "单轴磁通门",
        ["SensorType.TriaxialFluxgate"] = "三轴磁通门",
        ["SensorType.DualTriaxialFluxgate"] = "双三轴磁通门",
        ["SensorType.ProtonMagnetometer"] = "质子磁力仪",
        ["CalibrationCollectionMode.Continuous"] = "连续采集",
        ["CalibrationCollectionMode.Manual48"] = "手动 48 点",
        ["ParseTestInputKind.Hex"] = "HEX",
        ["ParseTestInputKind.Text"] = "文本",
        ["CalibrationDataSource.Live"] = "实时数据",
        ["CalibrationDataSource.File"] = "导入文件",
        ["CalibrationDataSource.Session"] = "已保存会话",
        ["CommandParameterType.String"] = "文本",
        ["CommandParameterType.Int"] = "整数",
        ["CommandParameterType.Double"] = "小数",
        ["CommandParameterType.Enum"] = "枚举",
        ["CommandParameterType.HexBytes"] = "HEX 字节串",
        ["Endianness.LittleEndian"] = "小端",
        ["Endianness.BigEndian"] = "大端",
        ["ChecksumKind.None"] = "无校验",
        ["ChecksumKind.Sum8"] = "累加和",
        ["ChecksumKind.Xor8"] = "XOR 异或",
        ["ChecksumKind.Crc16"] = "CRC-16/MODBUS",
        ["TrafficKind.Tx"] = "发送",
        ["TrafficKind.Rx"] = "接收",
        ["TrafficKind.Note"] = "说明",
        ["ParseOutcome.Accepted"] = "通过",
        ["ParseOutcome.Rejected"] = "拒绝",
        ["ParseOutcome.Skipped"] = "丢弃",
    };

    public static string Display(object? value) => value is Enum e
        ? Names.GetValueOrDefault($"{e.GetType().Name}.{e}", e.ToString())
        : value?.ToString() ?? "";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Display(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"#AARRGGBB" / "#RRGGBB" 字符串转画刷；无效值显示为灰色。</summary>
public class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            if (value is string hex && ColorConverter.ConvertFromString(hex) is Color c)
            {
                var brush = new SolidColorBrush(c);
                brush.Freeze();
                return brush;
            }
        }
        catch (FormatException) { }
        return Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
