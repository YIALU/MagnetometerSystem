using MagnetometerSystem.Core.Helpers;

namespace MagnetometerSystem.Core.Tests;

public class CircularBufferTests
{
    [Fact]
    public void Constructor_ZeroCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CircularBuffer<int>(0));
    }

    [Fact]
    public void Add_BelowCapacity_CountIncreases()
    {
        var buffer = new CircularBuffer<int>(5);

        buffer.Add(10);
        buffer.Add(20);

        Assert.Equal(2, buffer.Count);
        Assert.Equal(5, buffer.Capacity);
        Assert.False(buffer.IsFull);
    }

    [Fact]
    public void Add_BeyondCapacity_OverwritesOldest()
    {
        var buffer = new CircularBuffer<int>(3);

        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        Assert.True(buffer.IsFull);

        // This overwrites '1'
        buffer.Add(4);

        Assert.Equal(3, buffer.Count);
        Assert.True(buffer.IsFull);

        // Oldest is now 2, newest is 4
        Assert.Equal(2, buffer[0]);
        Assert.Equal(3, buffer[1]);
        Assert.Equal(4, buffer[2]);
    }

    [Fact]
    public void Add_MultipleWraps_MaintainsCorrectOrder()
    {
        var buffer = new CircularBuffer<int>(3);

        // Add 1..6 (wraps twice)
        for (int i = 1; i <= 6; i++)
            buffer.Add(i);

        Assert.Equal(3, buffer.Count);
        Assert.Equal(4, buffer[0]); // oldest
        Assert.Equal(5, buffer[1]);
        Assert.Equal(6, buffer[2]); // newest
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var buffer = new CircularBuffer<int>(5);
        buffer.Add(1);
        buffer.Add(2);

        Assert.Throws<IndexOutOfRangeException>(() => buffer[2]);
        Assert.Throws<IndexOutOfRangeException>(() => buffer[-1]);
    }

    [Fact]
    public void ToArray_ReturnsOldestToNewest()
    {
        var buffer = new CircularBuffer<int>(3);
        buffer.Add(10);
        buffer.Add(20);
        buffer.Add(30);
        buffer.Add(40); // overwrites 10

        var arr = buffer.ToArray();

        Assert.Equal(new[] { 20, 30, 40 }, arr);
    }

    [Fact]
    public void Last_ReturnsNewestElement()
    {
        var buffer = new CircularBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        Assert.Equal(3, buffer.Last);

        buffer.Add(4); // overwrites 1
        Assert.Equal(4, buffer.Last);
    }

    [Fact]
    public void Clear_ResetsCountAndIsFull()
    {
        var buffer = new CircularBuffer<int>(3);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);
        Assert.True(buffer.IsFull);

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.False(buffer.IsFull);
    }

    [Fact]
    public void AddRange_AddsMultipleItems()
    {
        var buffer = new CircularBuffer<int>(5);
        buffer.AddRange(new[] { 1, 2, 3 });

        Assert.Equal(3, buffer.Count);
        Assert.Equal(1, buffer[0]);
        Assert.Equal(3, buffer[2]);
    }

    [Fact]
    public void Enumerable_IteratesOldestToNewest()
    {
        var buffer = new CircularBuffer<string>(3);
        buffer.Add("a");
        buffer.Add("b");
        buffer.Add("c");
        buffer.Add("d"); // overwrites "a"

        var items = buffer.ToList();

        Assert.Equal(new[] { "b", "c", "d" }, items);
    }
}
