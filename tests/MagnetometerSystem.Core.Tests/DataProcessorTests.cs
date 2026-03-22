using MagnetometerSystem.Core.Processing;

namespace MagnetometerSystem.Core.Tests;

public class DataProcessorTests
{
    private readonly DataProcessor _processor = new();

    [Fact]
    public void MovingAverage_KnownData_SmoothsCorrectly()
    {
        // Window size 3, halfW = 1, so each point averages with its immediate neighbors
        double[] data = { 1, 2, 3, 4, 5 };
        var result = _processor.MovingAverage(data, 3);

        Assert.Equal(5, result.Length);
        // Index 0: avg(1,2) = 1.5 (start=0, end=1)
        Assert.Equal(1.5, result[0], precision: 10);
        // Index 1: avg(1,2,3) = 2.0
        Assert.Equal(2.0, result[1], precision: 10);
        // Index 2: avg(2,3,4) = 3.0
        Assert.Equal(3.0, result[2], precision: 10);
        // Index 3: avg(3,4,5) = 4.0
        Assert.Equal(4.0, result[3], precision: 10);
        // Index 4: avg(4,5) = 4.5
        Assert.Equal(4.5, result[4], precision: 10);
    }

    [Fact]
    public void MovingAverage_ConstantData_ReturnsConstant()
    {
        double[] data = { 7, 7, 7, 7, 7 };
        var result = _processor.MovingAverage(data, 3);

        foreach (var val in result)
            Assert.Equal(7.0, val, precision: 10);
    }

    [Fact]
    public void MovingAverage_WindowSize1_ReturnsSameData()
    {
        double[] data = { 1, 3, 5, 7, 9 };
        var result = _processor.MovingAverage(data, 1);

        // halfW = 0, so each point just averages with itself
        Assert.Equal(data.Length, result.Length);
        for (int i = 0; i < data.Length; i++)
            Assert.Equal(data[i], result[i], precision: 10);
    }

    [Fact]
    public void MovingAverage_EmptyArray_ReturnsEmpty()
    {
        var result = _processor.MovingAverage(Array.Empty<double>(), 3);
        Assert.Empty(result);
    }

    [Fact]
    public void MovingAverage_NullArray_ReturnsEmpty()
    {
        var result = _processor.MovingAverage(null!, 3);
        Assert.Empty(result);
    }

    [Fact]
    public void MovingAverage_WindowLargerThanData_AveragesAll()
    {
        double[] data = { 2, 4, 6 };
        var result = _processor.MovingAverage(data, 100);

        // halfW = 50, so every index covers the full array: avg(2,4,6) = 4.0
        Assert.Equal(3, result.Length);
        Assert.Equal(4.0, result[0], precision: 10);
        Assert.Equal(4.0, result[1], precision: 10);
        Assert.Equal(4.0, result[2], precision: 10);
    }

    [Fact]
    public void MedianFilter_KnownData_ReturnsMedians()
    {
        // Window size 3 (odd), halfW=1
        double[] data = { 5, 1, 3, 9, 2 };
        var result = _processor.MedianFilter(data, 3);

        Assert.Equal(5, result.Length);
        // Index 0: window [5,1], sorted [1,5], median index 1 -> 5
        Assert.Equal(5.0, result[0], precision: 10);
        // Index 1: window [5,1,3], sorted [1,3,5], median -> 3
        Assert.Equal(3.0, result[1], precision: 10);
        // Index 2: window [1,3,9], sorted [1,3,9], median -> 3
        Assert.Equal(3.0, result[2], precision: 10);
        // Index 3: window [3,9,2], sorted [2,3,9], median -> 3
        Assert.Equal(3.0, result[3], precision: 10);
        // Index 4: window [9,2], sorted [2,9], median index 1 -> 9
        Assert.Equal(9.0, result[4], precision: 10);
    }

    [Fact]
    public void MedianFilter_EvenWindowSize_IsRoundedToOdd()
    {
        // Even window size 4 becomes 5 (windowSize++ when even)
        double[] data = { 10, 20, 30, 40, 50 };
        var result = _processor.MedianFilter(data, 4);

        // Window becomes 5, halfW=2
        Assert.Equal(5, result.Length);
        // Index 2 (center): window covers all [10,20,30,40,50], median -> 30
        Assert.Equal(30.0, result[2], precision: 10);
    }

    [Fact]
    public void MedianFilter_EmptyArray_ReturnsEmpty()
    {
        var result = _processor.MedianFilter(Array.Empty<double>(), 3);
        Assert.Empty(result);
    }

    [Fact]
    public void MedianFilter_WindowSize1_ReturnsSameData()
    {
        double[] data = { 3, 1, 4, 1, 5 };
        var result = _processor.MedianFilter(data, 1);

        for (int i = 0; i < data.Length; i++)
            Assert.Equal(data[i], result[i], precision: 10);
    }
}
