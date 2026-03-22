using MagnetometerSystem.Core.Helpers;

namespace MagnetometerSystem.Core.Tests;

public class ByteRingBufferTests
{
    [Fact]
    public void WriteAndRead_BasicRoundTrip()
    {
        var buffer = new ByteRingBuffer(64);
        byte[] input = { 0x01, 0x02, 0x03, 0x04 };

        buffer.Write(input, 0, input.Length);
        Assert.Equal(4, buffer.Count);

        var output = new byte[4];
        int read = buffer.Read(output, 0, 4);

        Assert.Equal(4, read);
        Assert.Equal(input, output);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void WriteAndRead_WrapAround()
    {
        var buffer = new ByteRingBuffer(8);

        // Write 6 bytes, then read 4 (advances read pointer to position 4)
        byte[] data1 = { 1, 2, 3, 4, 5, 6 };
        buffer.Write(data1, 0, 6);
        var discard = new byte[4];
        buffer.Read(discard, 0, 4);
        Assert.Equal(2, buffer.Count); // 5, 6 remain

        // Write 6 more bytes: write pointer wraps around the end of the 8-byte buffer
        byte[] data2 = { 7, 8, 9, 10, 11, 12 };
        buffer.Write(data2, 0, 6);
        Assert.Equal(8, buffer.Count); // buffer is now full

        // Read all 8 bytes
        var output = new byte[8];
        int read = buffer.Read(output, 0, 8);

        Assert.Equal(8, read);
        Assert.Equal(new byte[] { 5, 6, 7, 8, 9, 10, 11, 12 }, output);
    }

    [Fact]
    public void Write_ExceedsCapacity_Throws()
    {
        var buffer = new ByteRingBuffer(4);
        byte[] data = { 1, 2, 3, 4, 5 };

        Assert.Throws<InvalidOperationException>(() => buffer.Write(data, 0, data.Length));
    }

    [Fact]
    public void Peek_ReadsWithoutAdvancing()
    {
        var buffer = new ByteRingBuffer(16);
        byte[] data = { 0xAA, 0xBB, 0xCC };
        buffer.Write(data, 0, data.Length);

        Assert.Equal(0xAA, buffer.Peek(0));
        Assert.Equal(0xBB, buffer.Peek(1));
        Assert.Equal(0xCC, buffer.Peek(2));

        // Count unchanged after peek
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void Peek_OutOfRange_Throws()
    {
        var buffer = new ByteRingBuffer(16);
        byte[] data = { 1, 2 };
        buffer.Write(data, 0, 2);

        Assert.Throws<InvalidOperationException>(() => buffer.Peek(2));
    }

    [Fact]
    public void IndexOf_FindsDelimiter()
    {
        var buffer = new ByteRingBuffer(32);
        byte[] data = { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x0A }; // "Hello\n"
        buffer.Write(data, 0, data.Length);

        int idx = buffer.IndexOf(0x0A); // newline
        Assert.Equal(5, idx);
    }

    [Fact]
    public void IndexOf_NotFound_ReturnsNegativeOne()
    {
        var buffer = new ByteRingBuffer(32);
        byte[] data = { 1, 2, 3 };
        buffer.Write(data, 0, data.Length);

        Assert.Equal(-1, buffer.IndexOf(0xFF));
    }

    [Fact]
    public void IndexOf_AfterWrap_FindsCorrectly()
    {
        var buffer = new ByteRingBuffer(8);

        // Fill then read to push read pointer forward
        byte[] fill = { 0, 0, 0, 0, 0, 0 };
        buffer.Write(fill, 0, 6);
        buffer.Skip(6);

        // Now write data that wraps around the buffer boundary
        byte[] data = { 0x41, 0x42, 0x43, 0x0D }; // "ABC\r"
        buffer.Write(data, 0, data.Length);

        int idx = buffer.IndexOf(0x0D);
        Assert.Equal(3, idx);
    }

    [Fact]
    public void Skip_AdvancesReadPointer()
    {
        var buffer = new ByteRingBuffer(16);
        byte[] data = { 10, 20, 30, 40, 50 };
        buffer.Write(data, 0, data.Length);

        buffer.Skip(3);
        Assert.Equal(2, buffer.Count);

        Assert.Equal(40, buffer.Peek(0));
        Assert.Equal(50, buffer.Peek(1));
    }

    [Fact]
    public void ReadBytes_ReturnsCorrectSlice()
    {
        var buffer = new ByteRingBuffer(16);
        byte[] data = { 0xAA, 0xBB, 0xCC, 0xDD };
        buffer.Write(data, 0, data.Length);

        byte[] first2 = buffer.ReadBytes(2);

        Assert.Equal(new byte[] { 0xAA, 0xBB }, first2);
        Assert.Equal(2, buffer.Count);
    }

    [Fact]
    public void Clear_ResetsBuffer()
    {
        var buffer = new ByteRingBuffer(16);
        byte[] data = { 1, 2, 3 };
        buffer.Write(data, 0, data.Length);

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal(16, buffer.FreeSpace);
    }

    [Fact]
    public void FreeSpace_ReflectsAvailableCapacity()
    {
        var buffer = new ByteRingBuffer(10);

        Assert.Equal(10, buffer.FreeSpace);

        byte[] data = { 1, 2, 3 };
        buffer.Write(data, 0, 3);
        Assert.Equal(7, buffer.FreeSpace);

        buffer.Read(new byte[2], 0, 2);
        Assert.Equal(9, buffer.FreeSpace);
    }
}
