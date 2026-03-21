using System.Collections;

namespace MagnetometerSystem.Core.Helpers;

/// <summary>
/// 高性能环形缓冲区，固定容量，O(1) 追加，满时自动覆盖最旧数据
/// </summary>
public class CircularBuffer<T> : IEnumerable<T>
{
    private readonly T[] _buffer;
    private int _head;  // 下一个写入位置
    private int _count;

    public CircularBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _buffer = new T[capacity];
    }

    /// <summary>容量</summary>
    public int Capacity => _buffer.Length;

    /// <summary>当前元素数量</summary>
    public int Count => _count;

    /// <summary>是否已满</summary>
    public bool IsFull => _count == _buffer.Length;

    /// <summary>追加一个元素</summary>
    public void Add(T item)
    {
        _buffer[_head] = item;
        _head = (_head + 1) % _buffer.Length;
        if (_count < _buffer.Length)
            _count++;
    }

    /// <summary>批量追加</summary>
    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items)
            Add(item);
    }

    /// <summary>按逻辑索引访问（0 = 最旧，Count-1 = 最新）</summary>
    public T this[int index]
    {
        get
        {
            if (index < 0 || index >= _count)
                throw new IndexOutOfRangeException();
            int start = IsFull ? _head : 0;
            int actualIndex = (start + index) % _buffer.Length;
            return _buffer[actualIndex];
        }
    }

    /// <summary>获取最新的元素</summary>
    public T? Last => _count > 0 ? this[_count - 1] : default;

    /// <summary>清空缓冲区</summary>
    public void Clear()
    {
        _head = 0;
        _count = 0;
        Array.Clear(_buffer, 0, _buffer.Length);
    }

    /// <summary>将所有元素复制到数组（从最旧到最新）</summary>
    public T[] ToArray()
    {
        var result = new T[_count];
        for (int i = 0; i < _count; i++)
            result[i] = this[i];
        return result;
    }

    public IEnumerator<T> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
