namespace MagnetometerSystem.Core.Protocol;

/// <summary>
/// CRC-16 变体。校验算法必须与设备端完全一致才有意义，故提供多种常见变体由协议配置选择。
/// 标准校验值（输入 ASCII "123456789"）：
///   Modbus=0x4B37, CcittFalse=0x29B1, XModem=0x31C3, Ibm/ARC=0xBB3D
/// </summary>
public enum Crc16Variant
{
    /// <summary>CRC-16/MODBUS：poly 0x8005(反射 0xA001)，init 0xFFFF，输入/输出反射。串口/工业设备最常见。</summary>
    Modbus,
    /// <summary>CRC-16/CCITT-FALSE：poly 0x1021，init 0xFFFF，不反射。</summary>
    CcittFalse,
    /// <summary>CRC-16/XMODEM：poly 0x1021，init 0x0000，不反射。</summary>
    XModem,
    /// <summary>CRC-16/ARC(IBM)：poly 0x8005(反射 0xA001)，init 0x0000，输入/输出反射。</summary>
    Ibm,
}

/// <summary>
/// CRC-16 计算器。按变体分发到"反射 LSB-first"或"MSB-first"两种标准算法。
/// </summary>
public static class Crc16
{
    public static ushort Compute(ReadOnlySpan<byte> data, Crc16Variant variant) => variant switch
    {
        // 反射型：使用反射多项式 0xA001(=reflect(0x8005)) 的 LSB-first 算法
        Crc16Variant.Modbus => ReflectedLsb(data, reflectedPoly: 0xA001, init: 0xFFFF),
        Crc16Variant.Ibm => ReflectedLsb(data, reflectedPoly: 0xA001, init: 0x0000),
        // 非反射型：MSB-first 算法
        Crc16Variant.CcittFalse => Msb(data, poly: 0x1021, init: 0xFFFF),
        Crc16Variant.XModem => Msb(data, poly: 0x1021, init: 0x0000),
        _ => 0,
    };

    private static ushort ReflectedLsb(ReadOnlySpan<byte> data, ushort reflectedPoly, ushort init)
    {
        ushort crc = init;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (ushort)(((crc & 1) != 0) ? (crc >> 1) ^ reflectedPoly : crc >> 1);
        }
        return crc;
    }

    private static ushort Msb(ReadOnlySpan<byte> data, ushort poly, ushort init)
    {
        ushort crc = init;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (ushort)(((crc & 0x8000) != 0) ? (crc << 1) ^ poly : crc << 1);
        }
        return crc;
    }
}
