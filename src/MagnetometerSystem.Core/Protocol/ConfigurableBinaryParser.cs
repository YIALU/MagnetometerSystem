using MagnetometerSystem.Core.Helpers;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Protocol;

/// <summary>
/// 可配置的二进制帧协议解析器，由 ProtocolConfig 驱动
/// 支持用户自定义帧头、帧尾、校验、字段映射
/// </summary>
public class ConfigurableBinaryParser : IDataParser
{
    private readonly ByteRingBuffer _ringBuffer = new(131072);
    private readonly ProtocolConfig _config;
    private readonly byte[] _headerBytes;
    private readonly byte[] _tailBytes;

    public ConfigurableBinaryParser(ProtocolConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _headerBytes = config.FrameHeaderBytes;
        _tailBytes = config.FrameTailBytes;
    }

    public void Feed(byte[] data, int offset, int count)
    {
        if (count > _ringBuffer.FreeSpace)
            _ringBuffer.Skip(count - _ringBuffer.FreeSpace);
        _ringBuffer.Write(data, offset, count);
    }

    public bool TryParse(out MagnetometerReading? reading)
    {
        reading = null;

        // 计算数据区长度
        int dataLen = GetDataLength();
        if (dataLen < 0)
            return false; // 数据不足以判断

        // 计算整帧长度
        int headerLen = _headerBytes.Length;
        int lengthFieldLen = _config.HasLengthByte ? _config.LengthByteCount : 0;
        int checksumLen = _config.Checksum != ChecksumType.None ? 1 : 0;
        int tailLen = _tailBytes.Length;
        int frameLen = headerLen + lengthFieldLen + dataLen + checksumLen + tailLen;

        if (_ringBuffer.Count < frameLen)
            return false;

        // 搜索帧头
        if (!FindHeader())
            return false;

        // 重新检查长度（FindHeader 可能跳过了字节）
        dataLen = GetDataLength();
        if (dataLen < 0) return false;
        frameLen = headerLen + lengthFieldLen + dataLen + checksumLen + tailLen;
        if (_ringBuffer.Count < frameLen)
            return false;

        // 检查帧尾（如有）
        if (_tailBytes.Length > 0)
        {
            for (int i = 0; i < _tailBytes.Length; i++)
            {
                if (_ringBuffer.Peek(frameLen - tailLen + i) != _tailBytes[i])
                {
                    // 帧尾不匹配，跳过帧头继续搜索
                    _ringBuffer.Skip(headerLen);
                    return false;
                }
            }
        }

        // 校验（如有）— 在消费数据前用 Peek 验证
        if (_config.Checksum != ChecksumType.None)
        {
            int checksumPos = headerLen + lengthFieldLen + dataLen;
            byte expected = _ringBuffer.Peek(checksumPos);

            // 从 ringBuffer 中 Peek 数据计算校验
            byte computed = 0;
            for (int i = _config.ChecksumStartOffset; i < checksumPos; i++)
            {
                byte b = _ringBuffer.Peek(i);
                computed = _config.Checksum switch
                {
                    ChecksumType.Xor => (byte)(computed ^ b),
                    ChecksumType.Sum8 => (byte)(computed + b),
                    _ => computed
                };
            }

            if (computed != expected)
            {
                // 校验失败，跳过 1 字节重新搜索，避免丢失后续有效帧
                _ringBuffer.Skip(1);
                return false;
            }
        }

        // 读取整帧（校验通过后才消费）
        var frame = _ringBuffer.ReadBytes(frameLen);

        // 解析字段映射
        int dataStart = headerLen + lengthFieldLen;
        int maxChannelIndex = _config.FieldMappings.Count > 0
            ? _config.FieldMappings.Max(f => f.ChannelIndex) + 1
            : 0;
        var values = new double[maxChannelIndex];

        foreach (var field in _config.FieldMappings)
        {
            int fieldStart = dataStart + field.ByteOffset;
            if (fieldStart + field.ByteSize > frame.Length)
                continue;

            double rawValue = ReadFieldValue(frame, fieldStart, field);
            double finalValue = rawValue * field.Scale + field.Offset;

            if (field.ChannelIndex < values.Length)
                values[field.ChannelIndex] = finalValue;
        }

        reading = new MagnetometerReading
        {
            Timestamp = DateTime.Now,
            ChannelValues = values,
        };

        return true;
    }

    public void Reset()
    {
        _ringBuffer.Clear();
    }

    private bool FindHeader()
    {
        if (_headerBytes.Length == 0)
            return true;

        while (_ringBuffer.Count >= _headerBytes.Length)
        {
            bool match = true;
            for (int i = 0; i < _headerBytes.Length; i++)
            {
                if (_ringBuffer.Peek(i) != _headerBytes[i])
                {
                    match = false;
                    break;
                }
            }
            if (match) return true;
            _ringBuffer.Skip(1);
        }
        return false;
    }

    /// <summary>获取数据区长度（-1 表示数据不足）</summary>
    private int GetDataLength()
    {
        int headerLen = _headerBytes.Length;

        if (!_config.HasLengthByte)
        {
            // 固定长度
            if (_config.FixedDataLength > 0)
                return _config.FixedDataLength;

            // 根据字段映射自动计算
            if (_config.FieldMappings.Count > 0)
            {
                return _config.FieldMappings.Max(f => f.ByteOffset + f.ByteSize);
            }
            return 0;
        }

        // 有长度字节
        int lengthPos = headerLen;
        if (_ringBuffer.Count < lengthPos + _config.LengthByteCount)
            return -1;

        if (_config.LengthByteCount == 1)
        {
            return _ringBuffer.Peek(lengthPos);
        }
        else // 2 字节长度
        {
            byte b0 = _ringBuffer.Peek(lengthPos);
            byte b1 = _ringBuffer.Peek(lengthPos + 1);
            return _config.LengthBigEndian
                ? (b0 << 8) | b1
                : b0 | (b1 << 8);
        }
    }

    private double ReadFieldValue(byte[] frame, int offset, FieldMapping field)
    {
        // 如果需要翻转字节序
        byte[] fieldBytes = new byte[field.ByteSize];
        Array.Copy(frame, offset, fieldBytes, 0, field.ByteSize);

        if (field.BigEndian != !BitConverter.IsLittleEndian)
        {
            // 系统字节序与字段字节序不同，需要翻转
            Array.Reverse(fieldBytes);
        }

        return field.DataType switch
        {
            FieldDataType.Float => BitConverter.ToSingle(fieldBytes, 0),
            FieldDataType.Double => BitConverter.ToDouble(fieldBytes, 0),
            FieldDataType.Int16 => BitConverter.ToInt16(fieldBytes, 0),
            FieldDataType.UInt16 => BitConverter.ToUInt16(fieldBytes, 0),
            FieldDataType.Int32 => BitConverter.ToInt32(fieldBytes, 0),
            FieldDataType.UInt32 => BitConverter.ToUInt32(fieldBytes, 0),
            _ => 0
        };
    }

    private byte ComputeChecksum(byte[] frame, int start, int end)
    {
        return _config.Checksum switch
        {
            ChecksumType.Xor => ComputeXor(frame, start, end),
            ChecksumType.Sum8 => ComputeSum8(frame, start, end),
            _ => 0
        };
    }

    private static byte ComputeXor(byte[] data, int start, int end)
    {
        byte result = 0;
        for (int i = start; i < end; i++)
            result ^= data[i];
        return result;
    }

    private static byte ComputeSum8(byte[] data, int start, int end)
    {
        byte result = 0;
        for (int i = start; i < end; i++)
            result += data[i];
        return result;
    }
}
