using MagnetometerSystem.Core.Helpers;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Protocol;

/// <summary>
/// 可配置的二进制帧协议解析器，由 ProtocolConfig 驱动
/// 支持用户自定义帧头、帧尾、校验、字段映射
/// 同时支持旧的 FieldMapping 模式和新的 FrameSegment 段式模式
/// </summary>
public class ConfigurableBinaryParser : IDataParser
{
    private readonly ByteRingBuffer _ringBuffer = new(131072);
    private readonly ProtocolConfig _config;
    private readonly byte[] _headerBytes;
    private readonly byte[] _tailBytes;
    private readonly bool _useSegments;

    // 段式模式缓存
    private readonly List<FrameSegment> _dataSegments = [];
    private readonly FrameSegment? _lengthSegment;
    private readonly FrameSegment? _checksumSegment;
    private readonly int _segmentFrameLength;

    public ConfigurableBinaryParser(ProtocolConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _useSegments = config.UsesSegments;

        if (_useSegments)
        {
            config.ComputeSegmentOffsets();

            // 从 Segments 提取帧头/帧尾
            var headerSeg = config.Segments.FirstOrDefault(s => s.Type == SegmentType.Header);
            var tailSeg = config.Segments.FirstOrDefault(s => s.Type == SegmentType.Tail);
            _headerBytes = headerSeg != null && !string.IsNullOrEmpty(headerSeg.FixedHexValue)
                ? ProtocolConfig.HexToBytes(headerSeg.FixedHexValue)
                : [];
            _tailBytes = tailSeg != null && !string.IsNullOrEmpty(tailSeg.FixedHexValue)
                ? ProtocolConfig.HexToBytes(tailSeg.FixedHexValue)
                : [];

            _lengthSegment = config.Segments.FirstOrDefault(s => s.Type == SegmentType.LengthField);
            _checksumSegment = config.Segments.FirstOrDefault(s => s.Type == SegmentType.Checksum);
            _dataSegments = config.Segments.Where(s => s.Type == SegmentType.DataField).ToList();
            _segmentFrameLength = config.TotalFrameLength;
        }
        else
        {
            _headerBytes = config.FrameHeaderBytes;
            _tailBytes = config.FrameTailBytes;
        }
    }

    public void Feed(byte[] data, int offset, int count)
    {
        if (count > _ringBuffer.FreeSpace)
            _ringBuffer.Skip(count - _ringBuffer.FreeSpace);
        _ringBuffer.Write(data, offset, count);
    }

    public bool TryParse(out MagnetometerReading? reading)
    {
        return _useSegments ? TryParseSegments(out reading) : TryParseLegacy(out reading);
    }

    public void Reset()
    {
        _ringBuffer.Clear();
    }

    // =====================================================================
    // 段式解析模式
    // =====================================================================

    private bool TryParseSegments(out MagnetometerReading? reading)
    {
        reading = null;

        int frameLen;

        if (_lengthSegment != null)
        {
            // 有长度字段：需要先读出长度值来确定帧长
            int headerLen = _headerBytes.Length;
            int lengthPos = _lengthSegment.ComputedOffset;

            if (_ringBuffer.Count < lengthPos + _lengthSegment.ByteCount)
                return false;

            // 搜索帧头
            if (!FindHeader())
                return false;

            if (_ringBuffer.Count < lengthPos + _lengthSegment.ByteCount)
                return false;

            // 读取长度值（长度值表示数据区字节数）
            int dataLen;
            if (_lengthSegment.ByteCount == 1)
            {
                dataLen = _ringBuffer.Peek(lengthPos);
            }
            else
            {
                byte b0 = _ringBuffer.Peek(lengthPos);
                byte b1 = _ringBuffer.Peek(lengthPos + 1);
                dataLen = _lengthSegment.LengthBigEndian
                    ? (b0 << 8) | b1
                    : b0 | (b1 << 8);
            }

            // 计算非数据区部分的长度
            int nonDataLen = _segmentFrameLength - _dataSegments.Sum(s => s.ByteCount);
            frameLen = nonDataLen + dataLen;
        }
        else
        {
            // 无长度字段：使用固定帧长
            frameLen = _segmentFrameLength;

            if (_ringBuffer.Count < frameLen)
                return false;

            if (!FindHeader())
                return false;

            if (_ringBuffer.Count < frameLen)
                return false;
        }

        if (_ringBuffer.Count < frameLen)
            return false;

        // 验证帧尾
        if (_tailBytes.Length > 0)
        {
            int tailOffset = frameLen - _tailBytes.Length;
            for (int i = 0; i < _tailBytes.Length; i++)
            {
                if (_ringBuffer.Peek(tailOffset + i) != _tailBytes[i])
                {
                    _ringBuffer.Skip(_headerBytes.Length);
                    return false;
                }
            }
        }

        // 验证校验
        if (_checksumSegment != null)
        {
            int checksumPos = _checksumSegment.ComputedOffset;
            byte expected = _ringBuffer.Peek(checksumPos);

            // 计算校验起始位置
            int checksumStart = 0;
            if (_checksumSegment.ChecksumStartIndex > 0 && _checksumSegment.ChecksumStartIndex < _config.Segments.Count)
            {
                checksumStart = _config.Segments[_checksumSegment.ChecksumStartIndex].ComputedOffset;
            }

            byte computed = 0;
            for (int i = checksumStart; i < checksumPos; i++)
            {
                byte b = _ringBuffer.Peek(i);
                computed = _checksumSegment.ChecksumAlgorithm switch
                {
                    ChecksumAlgorithm.Xor => (byte)(computed ^ b),
                    ChecksumAlgorithm.Sum8 => (byte)(computed + b),
                    _ => computed
                };
            }

            if (computed != expected)
            {
                _ringBuffer.Skip(1);
                return false;
            }
        }

        // 读取整帧
        var frame = _ringBuffer.ReadBytes(frameLen);

        // 提取数据字段
        int maxChannel = _dataSegments.Count > 0
            ? _dataSegments.Max(s => s.ChannelIndex) + 1
            : 0;
        var values = new double[maxChannel];

        foreach (var seg in _dataSegments)
        {
            int fieldStart = seg.ComputedOffset;
            if (fieldStart + seg.ByteCount > frame.Length)
                continue;

            double rawValue = ReadSegmentValue(frame, fieldStart, seg);
            double finalValue = rawValue * seg.Scale + seg.Offset;

            if (seg.ChannelIndex < values.Length)
                values[seg.ChannelIndex] = finalValue;
        }

        reading = new MagnetometerReading
        {
            Timestamp = DateTime.Now,
            ChannelValues = values,
        };

        return true;
    }

    private double ReadSegmentValue(byte[] frame, int offset, FrameSegment seg)
    {
        byte[] fieldBytes = new byte[seg.ByteCount];
        Array.Copy(frame, offset, fieldBytes, 0, seg.ByteCount);

        if (seg.BigEndian != !BitConverter.IsLittleEndian)
        {
            Array.Reverse(fieldBytes);
        }

        return seg.DataType switch
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

    // =====================================================================
    // 旧模式解析（FieldMapping）— 原有逻辑不变
    // =====================================================================

    private bool TryParseLegacy(out MagnetometerReading? reading)
    {
        reading = null;

        int dataLen = GetDataLength();
        if (dataLen < 0)
            return false;

        int headerLen = _headerBytes.Length;
        int lengthFieldLen = _config.HasLengthByte ? _config.LengthByteCount : 0;
        int checksumLen = _config.Checksum != ChecksumType.None ? 1 : 0;
        int tailLen = _tailBytes.Length;
        int frameLen = headerLen + lengthFieldLen + dataLen + checksumLen + tailLen;

        if (_ringBuffer.Count < frameLen)
            return false;

        if (!FindHeader())
            return false;

        dataLen = GetDataLength();
        if (dataLen < 0) return false;
        frameLen = headerLen + lengthFieldLen + dataLen + checksumLen + tailLen;
        if (_ringBuffer.Count < frameLen)
            return false;

        if (_tailBytes.Length > 0)
        {
            for (int i = 0; i < _tailBytes.Length; i++)
            {
                if (_ringBuffer.Peek(frameLen - tailLen + i) != _tailBytes[i])
                {
                    _ringBuffer.Skip(headerLen);
                    return false;
                }
            }
        }

        if (_config.Checksum != ChecksumType.None)
        {
            int checksumPos = headerLen + lengthFieldLen + dataLen;
            byte expected = _ringBuffer.Peek(checksumPos);

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
                _ringBuffer.Skip(1);
                return false;
            }
        }

        var frame = _ringBuffer.ReadBytes(frameLen);

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

    // =====================================================================
    // 共用辅助方法
    // =====================================================================

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

    private int GetDataLength()
    {
        int headerLen = _headerBytes.Length;

        if (!_config.HasLengthByte)
        {
            if (_config.FixedDataLength > 0)
                return _config.FixedDataLength;

            if (_config.FieldMappings.Count > 0)
            {
                return _config.FieldMappings.Max(f => f.ByteOffset + f.ByteSize);
            }
            return 0;
        }

        int lengthPos = headerLen;
        if (_ringBuffer.Count < lengthPos + _config.LengthByteCount)
            return -1;

        if (_config.LengthByteCount == 1)
        {
            return _ringBuffer.Peek(lengthPos);
        }
        else
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
        byte[] fieldBytes = new byte[field.ByteSize];
        Array.Copy(frame, offset, fieldBytes, 0, field.ByteSize);

        if (field.BigEndian != !BitConverter.IsLittleEndian)
        {
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
}
