using MagnetometerSystem.Core.Helpers;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Protocol;

/// <summary>
/// 二进制帧协议解析器
/// 帧格式: [帧头 2B] [数据长度 1B] [通道数据 N*4B float/N*8B double] [校验 1B] [帧尾 1B]
/// 帧头默认: 0xAA 0x55
/// 帧尾默认: 0x0D
/// 校验: 异或校验（从帧头到数据的所有字节异或）
/// </summary>
public class BinaryFrameParser : IDataParser
{
    private readonly ByteRingBuffer _ringBuffer = new(131072);
    private readonly byte _header1;
    private readonly byte _header2;
    private readonly byte _tail;
    private readonly bool _useDouble;  // true = 8 字节 double, false = 4 字节 float

    public BinaryFrameParser(byte header1 = 0xAA, byte header2 = 0x55, byte tail = 0x0D, bool useDouble = false)
    {
        _header1 = header1;
        _header2 = header2;
        _tail = tail;
        _useDouble = useDouble;
    }

    public void Feed(byte[] data, int offset, int count)
    {
        _ringBuffer.Write(data, offset, count);
    }

    public bool TryParse(out MagnetometerReading? reading)
    {
        reading = null;

        // 最小帧长: 帧头(2) + 长度(1) + 至少一个数据(4/8) + 校验(1) + 帧尾(1) = 9/13
        int minFrameLen = 2 + 1 + (_useDouble ? 8 : 4) + 1 + 1;
        if (_ringBuffer.Count < minFrameLen)
            return false;

        // 搜索帧头
        while (_ringBuffer.Count >= minFrameLen)
        {
            if (_ringBuffer.Peek(0) == _header1 && _ringBuffer.Peek(1) == _header2)
                break;
            _ringBuffer.Skip(1); // 跳过一个字节继续找
        }

        if (_ringBuffer.Count < minFrameLen)
            return false;

        // 读取数据长度（字节数）
        int dataLen = _ringBuffer.Peek(2);
        int valueSize = _useDouble ? 8 : 4;
        int channelCount = dataLen / valueSize;

        // 完整帧长度: 帧头(2) + 长度(1) + 数据(dataLen) + 校验(1) + 帧尾(1)
        int frameLen = 2 + 1 + dataLen + 1 + 1;

        if (_ringBuffer.Count < frameLen)
            return false;

        // 检查帧尾
        if (_ringBuffer.Peek(frameLen - 1) != _tail)
        {
            // 帧尾不匹配，丢弃帧头继续搜索
            _ringBuffer.Skip(2);
            return false;
        }

        // 读取整帧
        var frame = _ringBuffer.ReadBytes(frameLen);

        // 校验（异或校验，从帧头到数据的所有字节）
        byte checksum = 0;
        for (int i = 0; i < 2 + 1 + dataLen; i++)
            checksum ^= frame[i];

        if (checksum != frame[frameLen - 2])
        {
            // 校验失败
            return false;
        }

        // 解析通道数据
        var values = new double[channelCount];
        for (int i = 0; i < channelCount; i++)
        {
            int offset = 3 + i * valueSize;
            if (_useDouble)
                values[i] = BitConverter.ToDouble(frame, offset);
            else
                values[i] = BitConverter.ToSingle(frame, offset);
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
}
