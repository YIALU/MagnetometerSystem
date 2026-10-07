using MagnetometerSystem.Core.Helpers;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Protocol;

/// <summary>
/// 可配置的二进制帧协议解析器，由 ProtocolConfig 驱动
/// 支持用户自定义帧头、帧尾、校验、字段映射
/// 同时支持旧的 FieldMapping 模式和新的 FrameSegment 段式模式
/// </summary>
public class ConfigurableBinaryParser : IDataParser, IParserDiagnostics
{
    public long RejectedFrameCount { get; private set; }
    public string? LastError { get; private set; }
    public ParseRecordLog Records { get; } = new();
    private readonly ByteRingBuffer _ringBuffer = new(131072);

    // 一次 TryParse 尝试内的诊断：帧头前丢弃的字节、拒绝原因与候选帧字节、通过的整帧。
    private int _noiseSkipped;
    private string? _rejectReason;
    private int _rejectLength;
    private byte[]? _rejectBytes;
    private byte[]? _acceptedFrame;
    private const int MaxPreviewSource = 1024;
    private readonly ProtocolConfig _config;
    private readonly byte[] _headerBytes;
    private readonly byte[] _tailBytes;
    private readonly bool _useSegments;

    // 段式模式缓存
    private readonly List<FrameSegment> _dataSegments = [];
    private readonly FrameSegment? _lengthSegment;
    private readonly FrameSegment? _checksumSegment;
    private readonly int _segmentFrameLength;
    private readonly int _payloadStart;
    private readonly int _payloadEnd;
    private readonly int _requiredPayloadLength;

    /// <summary>启用了 ValidateFixedValue 的 Padding 段：(帧内偏移, 期望字节)</summary>
    private readonly List<(int Offset, byte[] Expected)> _constantChecks = [];

    public ConfigurableBinaryParser(ProtocolConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        config.ValidateRequiredChecksum();
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
            if (_lengthSegment != null)
            {
                // LengthField counts every payload byte, including Padding. Mapped fields
                // describe a stable prefix; checksum/tail follow the actual payload length.
                _payloadStart = _lengthSegment.ComputedOffset + _lengthSegment.ByteCount;
                _payloadEnd = config.Segments
                    .Where(s => s.ComputedOffset >= _payloadStart && s.Type is SegmentType.Checksum or SegmentType.Tail)
                    .Select(s => s.ComputedOffset).DefaultIfEmpty(_segmentFrameLength).Min();
                _requiredPayloadLength = config.Segments
                    .Where(s => s.ComputedOffset >= _payloadStart && s.ComputedOffset < _payloadEnd
                        && (s.Type == SegmentType.DataField || s.ValidateFixedValue))
                    .Select(s => s.ComputedOffset + s.ByteCount - _payloadStart).DefaultIfEmpty(0).Max();
            }

            // 收集需要参与校验的固定值段（信息 ID、固定长度字段等）
            foreach (var seg in config.Segments)
            {
                if (seg.Type != SegmentType.Padding || !seg.ValidateFixedValue)
                    continue;
                if (string.IsNullOrEmpty(seg.FixedHexValue))
                    continue;
                var expected = ProtocolConfig.HexToBytes(seg.FixedHexValue);
                if (expected.Length == seg.ByteCount)
                    _constantChecks.Add((seg.ComputedOffset, expected));
            }
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
        {
            Records.Skipped(_ringBuffer.Count, "接收缓冲区溢出，丢弃未成帧的数据");
            _ringBuffer.Clear();
            RejectedFrameCount++;
            LastError = "二进制接收缓冲区溢出，已丢弃不完整帧";
            if (count > _ringBuffer.Capacity) { offset += count - _ringBuffer.Capacity; count = _ringBuffer.Capacity; }
        }
        _ringBuffer.Write(data, offset, count);
    }

    public bool TryParse(out MagnetometerReading? reading)
    {
        // 坏候选消耗字节后继续找帧，让同一收包中的有效帧立即输出。
        while (true)
        {
            var before = _ringBuffer.Count;
            _noiseSkipped = 0;
            _rejectReason = null;
            _rejectBytes = _acceptedFrame = null;
            var parsed = _useSegments ? TryParseSegments(out reading) : TryParseLegacy(out reading);
            if (_noiseSkipped > 0) Records.Skipped(_noiseSkipped, "未对齐帧头，丢弃");
            if (parsed)
            {
                Records.Accepted(_acceptedFrame!.Length, reading!.ChannelValues.Length, _acceptedFrame, hex: true);
                return true;
            }
            if (_ringBuffer.Count >= before) return false;
            RejectedFrameCount++;
            if (_rejectReason is { } reason)
            {
                LastError = $"二进制帧{reason}，已重新同步";
                Records.Rejected(_rejectLength, reason, _rejectBytes, hex: true);
            }
            else
            {
                LastError = "二进制帧头前有无法识别的字节，已丢弃并重新同步";
            }
        }
    }

    public void Reset()
    {
        _ringBuffer.Clear();
    }

    /// <summary>整帧已读出但数值无效：整帧丢弃并记录原因。</summary>
    private bool RejectConsumedFrame(byte[] frame, string reason)
    {
        _acceptedFrame = null;
        _rejectReason = reason;
        _rejectLength = frame.Length;
        _rejectBytes = frame.Length <= MaxPreviewSource ? frame : frame[..MaxPreviewSource];
        return false;
    }

    /// <summary>记录候选帧被拒绝的原因和开头的字节（在丢弃之前调用）。</summary>
    private void Fail(string reason, int candidateLength)
    {
        _rejectReason = reason;
        _rejectLength = candidateLength;
        var n = Math.Min(Math.Min(candidateLength, _ringBuffer.Count), MaxPreviewSource);
        _rejectBytes = new byte[n];
        for (int i = 0; i < n; i++) _rejectBytes[i] = _ringBuffer.Peek(i);
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

            int nonDataLen = _segmentFrameLength - (_payloadEnd - _payloadStart);
            frameLen = nonDataLen + dataLen;
            if (dataLen < _requiredPayloadLength || frameLen <= 0 || frameLen > _ringBuffer.Capacity)
            {
                Fail($"长度字段为 {dataLen}，少于协议数据区所需的 {_requiredPayloadLength} 字节或超出缓冲", lengthPos + _lengthSegment.ByteCount);
                _ringBuffer.Skip(1);
                return false;
            }
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
                    Fail("帧尾不匹配", frameLen);
                    _ringBuffer.Skip(1);
                    return false;
                }
            }
        }

        // 验证固定值段（信息 ID 等）：载荷里偶然出现帧头时，这些锚点能挡掉误锁
        foreach (var (configuredOffset, expected) in _constantChecks)
        {
            int offset = GetSegmentOffset(configuredOffset, frameLen);
            if (offset < 0 || offset + expected.Length > frameLen)
            {
                Fail($"固定值段超出帧长（帧内偏移 {offset}）", frameLen);
                _ringBuffer.Skip(1);
                return false;
            }
            for (int i = 0; i < expected.Length; i++)
            {
                if (_ringBuffer.Peek(offset + i) != expected[i])
                {
                    Fail($"固定值不匹配（帧内偏移 {offset}）", frameLen);
                    _ringBuffer.Skip(1);
                    return false;
                }
            }
        }

        // 验证校验
        if (_checksumSegment != null)
        {
            int checksumPos = GetSegmentOffset(_checksumSegment.ComputedOffset, frameLen);

            // 计算校验起始位置
            int checksumStart = 0;
            if (_checksumSegment.ChecksumStartIndex > 0 && _checksumSegment.ChecksumStartIndex < _config.Segments.Count)
            {
                checksumStart = GetSegmentOffset(_config.Segments[_checksumSegment.ChecksumStartIndex].ComputedOffset, frameLen);
            }
            if (checksumStart < 0 || checksumStart > checksumPos
                || checksumPos + _checksumSegment.ByteCount > frameLen)
            {
                Fail("校验位置超出帧长", frameLen);
                _ringBuffer.Skip(1);
                return false;
            }

            if (_checksumSegment.ChecksumAlgorithm == ChecksumAlgorithm.CRC16)
            {
                // CRC-16：2 字节校验值（该段 ByteCount 应配为 2），按字节序组合后比较
                var crcData = new byte[checksumPos - checksumStart];
                for (int i = 0; i < crcData.Length; i++)
                    crcData[i] = _ringBuffer.Peek(checksumStart + i);
                ushort computed = Crc16.Compute(crcData, _checksumSegment.Crc16Variant);
                byte c0 = _ringBuffer.Peek(checksumPos);
                byte c1 = _ringBuffer.Peek(checksumPos + 1);
                ushort expected = _checksumSegment.ChecksumBigEndian
                    ? (ushort)((c0 << 8) | c1)
                    : (ushort)(c0 | (c1 << 8));
                if (computed != expected)
                {
                    Fail($"CRC-16 校验失败：计算 {computed:X4}，帧内 {expected:X4}", frameLen);
                    _ringBuffer.Skip(1);
                    return false;
                }
            }
            else
            {
                byte expected = _ringBuffer.Peek(checksumPos);
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
                    Fail($"{(_checksumSegment.ChecksumAlgorithm == ChecksumAlgorithm.Xor ? "XOR" : "累加和")} 校验失败：计算 {computed:X2}，帧内 {expected:X2}", frameLen);
                    _ringBuffer.Skip(1);
                    return false;
                }
            }
        }

        // 读取整帧
        var frame = _ringBuffer.ReadBytes(frameLen);
        _acceptedFrame = frame;

        // 提取数据字段
        int maxChannel = _dataSegments.Count > 0
            ? _dataSegments.Max(s => s.ChannelIndex) + 1
            : 0;
        var values = new double[maxChannel];

        foreach (var seg in _dataSegments)
        {
            int fieldStart = GetSegmentOffset(seg.ComputedOffset, frameLen);
            if (fieldStart + seg.ByteCount > frame.Length)
                continue;

            double rawValue = ReadSegmentValue(frame, fieldStart, seg);
            double finalValue = rawValue * seg.Scale + seg.Offset;
            if (!double.IsFinite(finalValue)) return RejectConsumedFrame(frame, $"通道 {seg.ChannelIndex} 的值不是有限数值");

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

    private int GetSegmentOffset(int configuredOffset, int frameLength) =>
        _lengthSegment != null && configuredOffset >= _payloadEnd
            ? configuredOffset + frameLength - _segmentFrameLength
            : configuredOffset;

    private double ReadSegmentValue(byte[] frame, int offset, FrameSegment seg)
    {
        try
        {
            if (offset < 0 || seg.ByteCount <= 0 || offset + seg.ByteCount > frame.Length)
                return 0;

            byte[] fieldBytes = new byte[seg.ByteCount];
            Array.Copy(frame, offset, fieldBytes, 0, seg.ByteCount);

            if (seg.BigEndian != !BitConverter.IsLittleEndian)
            {
                Array.Reverse(fieldBytes);
            }

            return seg.DataType switch
            {
                FieldDataType.Float => fieldBytes.Length >= 4 ? BitConverter.ToSingle(fieldBytes, 0) : 0,
                FieldDataType.Double => fieldBytes.Length >= 8 ? BitConverter.ToDouble(fieldBytes, 0) : 0,
                FieldDataType.Int16 => fieldBytes.Length >= 2 ? BitConverter.ToInt16(fieldBytes, 0) : 0,
                FieldDataType.UInt16 => fieldBytes.Length >= 2 ? BitConverter.ToUInt16(fieldBytes, 0) : 0,
                FieldDataType.Int32 => fieldBytes.Length >= 4 ? BitConverter.ToInt32(fieldBytes, 0) : 0,
                FieldDataType.UInt32 => fieldBytes.Length >= 4 ? BitConverter.ToUInt32(fieldBytes, 0) : 0,
                _ => 0
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(
                $"[ReadSegmentValue] ch={seg.ChannelIndex} type={seg.DataType} bytes={seg.ByteCount} ex={ex.Message}");
            return 0;
        }
    }

    // =====================================================================
    // 旧模式解析（FieldMapping）— 原有逻辑不变
    // =====================================================================

    private bool TryParseLegacy(out MagnetometerReading? reading)
    {
        reading = null;

        int headerLen = _headerBytes.Length;
        int lengthFieldLen = _config.HasLengthByte ? _config.LengthByteCount : 0;
        int checksumLen = _config.Checksum == ChecksumType.None
            ? 0
            : (_config.Checksum == ChecksumType.CRC16 ? 2 : 1);
        int tailLen = _tailBytes.Length;

        // 先对齐帧头再读长度：未对齐（前导垃圾字节）时从错误位置读出的长度可能极大，
        // 导致 frameLen 过大、Count<frameLen 永远提前返回而卡死。必须先 FindHeader 对齐。
        if (!FindHeader())
            return false;

        int dataLen = GetDataLength();
        if (dataLen < 0)
            return false;

        int frameLen = headerLen + lengthFieldLen + dataLen + checksumLen + tailLen;
        int requiredData = _config.FieldMappings.Count == 0 ? 0 : _config.FieldMappings.Max(f => f.ByteOffset + f.ByteSize);
        if (frameLen <= 0 || frameLen > _ringBuffer.Capacity || dataLen < requiredData)
        {
            Fail($"长度字段无效（数据区 {dataLen} 字节，至少需要 {requiredData}）", headerLen + lengthFieldLen);
            _ringBuffer.Skip(1);
            return false;
        }
        if (_ringBuffer.Count < frameLen)
            return false;

        if (_tailBytes.Length > 0)
        {
            for (int i = 0; i < _tailBytes.Length; i++)
            {
                if (_ringBuffer.Peek(frameLen - tailLen + i) != _tailBytes[i])
                {
                    Fail("帧尾不匹配", frameLen);
                    _ringBuffer.Skip(1);
                    return false;
                }
            }
        }

        if (_config.Checksum != ChecksumType.None)
        {
            int checksumPos = headerLen + lengthFieldLen + dataLen;
            int start = _config.ChecksumStartOffset;

            if (_config.Checksum == ChecksumType.CRC16)
            {
                // CRC-16：2 字节校验值，按配置字节序组合后与计算值比较
                var crcData = new byte[checksumPos - start];
                for (int i = 0; i < crcData.Length; i++)
                    crcData[i] = _ringBuffer.Peek(start + i);
                ushort computed = Crc16.Compute(crcData, _config.Crc16Variant);
                byte c0 = _ringBuffer.Peek(checksumPos);
                byte c1 = _ringBuffer.Peek(checksumPos + 1);
                ushort expected = _config.ChecksumBigEndian
                    ? (ushort)((c0 << 8) | c1)
                    : (ushort)(c0 | (c1 << 8));
                if (computed != expected)
                {
                    Fail($"CRC-16 校验失败：计算 {computed:X4}，帧内 {expected:X4}", frameLen);
                    _ringBuffer.Skip(1);
                    return false;
                }
            }
            else
            {
                byte expected = _ringBuffer.Peek(checksumPos);
                byte computed = 0;
                for (int i = start; i < checksumPos; i++)
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
                    Fail($"{(_config.Checksum == ChecksumType.Xor ? "XOR" : "累加和")} 校验失败：计算 {computed:X2}，帧内 {expected:X2}", frameLen);
                    _ringBuffer.Skip(1);
                    return false;
                }
            }
        }

        var frame = _ringBuffer.ReadBytes(frameLen);
        _acceptedFrame = frame;

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
            if (!double.IsFinite(finalValue)) return RejectConsumedFrame(frame, $"通道 {field.ChannelIndex} 的值不是有限数值");

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
            _noiseSkipped++;
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
        try
        {
            if (offset < 0 || field.ByteSize <= 0 || offset + field.ByteSize > frame.Length)
                return 0;

            byte[] fieldBytes = new byte[field.ByteSize];
            Array.Copy(frame, offset, fieldBytes, 0, field.ByteSize);

            if (field.BigEndian != !BitConverter.IsLittleEndian)
            {
                Array.Reverse(fieldBytes);
            }

            return field.DataType switch
            {
                FieldDataType.Float => fieldBytes.Length >= 4 ? BitConverter.ToSingle(fieldBytes, 0) : 0,
                FieldDataType.Double => fieldBytes.Length >= 8 ? BitConverter.ToDouble(fieldBytes, 0) : 0,
                FieldDataType.Int16 => fieldBytes.Length >= 2 ? BitConverter.ToInt16(fieldBytes, 0) : 0,
                FieldDataType.UInt16 => fieldBytes.Length >= 2 ? BitConverter.ToUInt16(fieldBytes, 0) : 0,
                FieldDataType.Int32 => fieldBytes.Length >= 4 ? BitConverter.ToInt32(fieldBytes, 0) : 0,
                FieldDataType.UInt32 => fieldBytes.Length >= 4 ? BitConverter.ToUInt32(fieldBytes, 0) : 0,
                _ => 0
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(
                $"[ReadFieldValue] ch={field.ChannelIndex} type={field.DataType} bytes={field.ByteSize} ex={ex.Message}");
            return 0;
        }
    }
}
