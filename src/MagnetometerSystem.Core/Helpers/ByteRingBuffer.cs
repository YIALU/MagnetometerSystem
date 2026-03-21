namespace MagnetometerSystem.Core.Helpers;

/// <summary>
/// 字节环形缓冲区，专用于协议解析中的粘包/断包处理
/// </summary>
public class ByteRingBuffer
{
    private readonly byte[] _buffer;
    private int _readPos;
    private int _writePos;
    private int _count;

    public ByteRingBuffer(int capacity = 65536)
    {
        _buffer = new byte[capacity];
    }

    public int Capacity => _buffer.Length;
    public int Count => _count;
    public int FreeSpace => _buffer.Length - _count;

    /// <summary>写入数据</summary>
    public void Write(byte[] data, int offset, int count)
    {
        if (count > FreeSpace)
            throw new InvalidOperationException($"缓冲区空间不足: 需要 {count}, 剩余 {FreeSpace}");

        for (int i = 0; i < count; i++)
        {
            _buffer[_writePos] = data[offset + i];
            _writePos = (_writePos + 1) % _buffer.Length;
        }
        _count += count;
    }

    /// <summary>查看指定偏移处的字节（不移动读指针）</summary>
    public byte Peek(int offset = 0)
    {
        if (offset >= _count)
            throw new InvalidOperationException("超出可读范围");
        return _buffer[(_readPos + offset) % _buffer.Length];
    }

    /// <summary>读取并移动读指针</summary>
    public int Read(byte[] dest, int offset, int count)
    {
        int toRead = Math.Min(count, _count);
        for (int i = 0; i < toRead; i++)
        {
            dest[offset + i] = _buffer[_readPos];
            _readPos = (_readPos + 1) % _buffer.Length;
        }
        _count -= toRead;
        return toRead;
    }

    /// <summary>跳过指定数量的字节</summary>
    public void Skip(int count)
    {
        int toSkip = Math.Min(count, _count);
        _readPos = (_readPos + toSkip) % _buffer.Length;
        _count -= toSkip;
    }

    /// <summary>查找指定字节的位置（相对于当前读位置）</summary>
    public int IndexOf(byte value)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_buffer[(_readPos + i) % _buffer.Length] == value)
                return i;
        }
        return -1;
    }

    /// <summary>将缓冲区中从读指针开始的 count 个字节拷贝到字节数组</summary>
    public byte[] ReadBytes(int count)
    {
        var result = new byte[count];
        Read(result, 0, count);
        return result;
    }

    /// <summary>将缓冲区中从读指针开始的 count 个字节转为字符串（不移动指针）</summary>
    public string PeekString(int count, System.Text.Encoding? encoding = null)
    {
        encoding ??= System.Text.Encoding.ASCII;
        var bytes = new byte[Math.Min(count, _count)];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = _buffer[(_readPos + i) % _buffer.Length];
        return encoding.GetString(bytes);
    }

    /// <summary>清空</summary>
    public void Clear()
    {
        _readPos = 0;
        _writePos = 0;
        _count = 0;
    }
}
