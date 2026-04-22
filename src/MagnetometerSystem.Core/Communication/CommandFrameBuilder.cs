using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Communication;

/// <summary>
/// 设备命令帧构建器：按命令定义 + 运行时参数值构建字节流与可读预览
/// </summary>
public static class CommandFrameBuilder
{
    public sealed record FramePreview(
        byte[] HeaderBytes,
        byte[] DataBytes,
        byte[] ChecksumBytes,
        byte[] TailBytes)
    {
        public byte[] FullBytes
        {
            get
            {
                var buf = new byte[HeaderBytes.Length + DataBytes.Length + ChecksumBytes.Length + TailBytes.Length];
                int p = 0;
                Buffer.BlockCopy(HeaderBytes, 0, buf, p, HeaderBytes.Length); p += HeaderBytes.Length;
                Buffer.BlockCopy(DataBytes, 0, buf, p, DataBytes.Length); p += DataBytes.Length;
                Buffer.BlockCopy(ChecksumBytes, 0, buf, p, ChecksumBytes.Length); p += ChecksumBytes.Length;
                Buffer.BlockCopy(TailBytes, 0, buf, p, TailBytes.Length);
                return buf;
            }
        }
    }

    /// <summary>按命令 + 参数值构建 AsciiTemplate 字符串</summary>
    public static string RenderAsciiTemplate(
        DeviceCommand cmd, IReadOnlyDictionary<string, string> paramValues)
    {
        var result = cmd.Template ?? "";
        foreach (var (key, val) in paramValues)
        {
            result = result.Replace("{" + key + "}", val ?? "");
        }
        return result;
    }

    /// <summary>ASCII 模板最终要发送的字节（UTF-8 + 可选 \r\n）</summary>
    public static byte[] BuildAsciiBytes(DeviceCommand cmd, IReadOnlyDictionary<string, string> paramValues)
    {
        var text = RenderAsciiTemplate(cmd, paramValues);
        if (cmd.AppendNewline) text += "\r\n";
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>二进制帧分段预览（帧头/数据/校验/帧尾）</summary>
    public static FramePreview BuildBinaryFrame(
        DeviceCommand cmd, IReadOnlyDictionary<string, string> paramValues)
    {
        var header = ParseHexBytes(cmd.FrameHeader);
        var tail = ParseHexBytes(cmd.FrameTail);

        using var ms = new MemoryStream();
        foreach (var p in cmd.Parameters)
        {
            paramValues.TryGetValue(p.Key, out var v);
            var bytes = EncodeParameter(p, v ?? p.DefaultValue);
            ms.Write(bytes, 0, bytes.Length);
        }
        var data = ms.ToArray();

        byte[] checksum = cmd.Checksum switch
        {
            ChecksumKind.None => Array.Empty<byte>(),
            ChecksumKind.Sum8 => new[] { Sum8(data) },
            ChecksumKind.Xor8 => new[] { Xor8(data) },
            ChecksumKind.Crc16 => Crc16Modbus(data),
            _ => Array.Empty<byte>(),
        };

        return new FramePreview(header, data, checksum, tail);
    }

    // ---- 参数编码 ----

    public static byte[] EncodeParameter(CommandParameter p, string value)
    {
        value ??= "";

        switch (p.Type)
        {
            case CommandParameterType.String:
                return Encoding.UTF8.GetBytes(value);

            case CommandParameterType.Int:
            case CommandParameterType.I32:
                return EncodeSignedInt(value, 4, p.Endian);

            case CommandParameterType.Double:
            case CommandParameterType.Float64:
                return EncodeFloat64(value, p.Endian);

            case CommandParameterType.Enum:
                return Encoding.UTF8.GetBytes(value);

            case CommandParameterType.U8:
                return new[] { (byte)ParseUInt(value, 0, 255) };
            case CommandParameterType.U16:
                return EncodeUnsignedInt(value, 2, p.Endian, ushort.MaxValue);
            case CommandParameterType.U32:
                return EncodeUnsignedInt(value, 4, p.Endian, uint.MaxValue);

            case CommandParameterType.I8:
                {
                    var v = ParseSignedInt(value, sbyte.MinValue, sbyte.MaxValue);
                    return new[] { unchecked((byte)(sbyte)v) };
                }
            case CommandParameterType.I16:
                return EncodeSignedInt(value, 2, p.Endian);

            case CommandParameterType.Float32:
                return EncodeFloat32(value, p.Endian);

            case CommandParameterType.HexBytes:
                {
                    var bytes = ParseHexBytes(value);
                    if (p.ByteLength.HasValue && bytes.Length != p.ByteLength.Value)
                        throw new FormatException(
                            $"参数 '{p.Name}' 期望 {p.ByteLength} 字节，实际 {bytes.Length}");
                    return bytes;
                }

            default:
                return Array.Empty<byte>();
        }
    }

    private static byte[] EncodeUnsignedInt(string value, int byteLen, Endianness endian, ulong max)
    {
        var v = ParseUInt(value, 0, max);
        var buf = new byte[byteLen];
        switch (byteLen)
        {
            case 2:
                if (endian == Endianness.LittleEndian)
                    BinaryPrimitives.WriteUInt16LittleEndian(buf, (ushort)v);
                else
                    BinaryPrimitives.WriteUInt16BigEndian(buf, (ushort)v);
                break;
            case 4:
                if (endian == Endianness.LittleEndian)
                    BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)v);
                else
                    BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)v);
                break;
        }
        return buf;
    }

    private static byte[] EncodeSignedInt(string value, int byteLen, Endianness endian)
    {
        var v = ParseSignedInt(value,
            byteLen == 2 ? short.MinValue : int.MinValue,
            byteLen == 2 ? short.MaxValue : int.MaxValue);
        var buf = new byte[byteLen];
        switch (byteLen)
        {
            case 2:
                if (endian == Endianness.LittleEndian)
                    BinaryPrimitives.WriteInt16LittleEndian(buf, (short)v);
                else
                    BinaryPrimitives.WriteInt16BigEndian(buf, (short)v);
                break;
            case 4:
                if (endian == Endianness.LittleEndian)
                    BinaryPrimitives.WriteInt32LittleEndian(buf, (int)v);
                else
                    BinaryPrimitives.WriteInt32BigEndian(buf, (int)v);
                break;
        }
        return buf;
    }

    private static byte[] EncodeFloat32(string value, Endianness endian)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
            throw new FormatException($"无效的 Float32 值: '{value}'");
        var buf = new byte[4];
        if (endian == Endianness.LittleEndian)
            BinaryPrimitives.WriteSingleLittleEndian(buf, f);
        else
            BinaryPrimitives.WriteSingleBigEndian(buf, f);
        return buf;
    }

    private static byte[] EncodeFloat64(string value, Endianness endian)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            throw new FormatException($"无效的 Float64 值: '{value}'");
        var buf = new byte[8];
        if (endian == Endianness.LittleEndian)
            BinaryPrimitives.WriteDoubleLittleEndian(buf, d);
        else
            BinaryPrimitives.WriteDoubleBigEndian(buf, d);
        return buf;
    }

    private static ulong ParseUInt(string value, ulong min, ulong max)
    {
        if (!ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"无效的无符号整数: '{value}'");
        if (v < min || v > max) throw new OverflowException($"值 {v} 超出范围 [{min},{max}]");
        return v;
    }

    private static long ParseSignedInt(string value, long min, long max)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new FormatException($"无效的有符号整数: '{value}'");
        if (v < min || v > max) throw new OverflowException($"值 {v} 超出范围 [{min},{max}]");
        return v;
    }

    // ---- Hex / Checksum ----

    public static byte[] ParseHexBytes(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return Array.Empty<byte>();
        var cleaned = Regex.Replace(hex, @"[\s\-]", "");
        if (cleaned.Length == 0) return Array.Empty<byte>();
        if (cleaned.Length % 2 != 0) throw new FormatException("Hex 字符串长度必须为偶数");
        var bytes = new byte[cleaned.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);
        }
        return bytes;
    }

    public static string ToHexString(byte[] bytes) =>
        bytes.Length == 0 ? "" : BitConverter.ToString(bytes).Replace("-", " ");

    private static byte Sum8(byte[] data)
    {
        byte s = 0;
        foreach (var b in data) s = unchecked((byte)(s + b));
        return s;
    }

    private static byte Xor8(byte[] data)
    {
        byte x = 0;
        foreach (var b in data) x ^= b;
        return x;
    }

    /// <summary>CRC-16/MODBUS，返回 2 字节 LE（常见协议惯例）</summary>
    private static byte[] Crc16Modbus(byte[] data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 0x0001) != 0) crc = (ushort)((crc >> 1) ^ 0xA001);
                else crc >>= 1;
            }
        }
        var buf = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, crc);
        return buf;
    }
}
