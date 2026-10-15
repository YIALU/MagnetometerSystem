using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Processing;

namespace MagnetometerSystem.Core.Tests;

public class ChartSampleBufferTests
{
    private static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static MagnetometerReading Reading(double seconds, double[] display, double[]? raw = null) => new()
    {
        Timestamp = Start.AddSeconds(seconds), ChannelValues = display,
        OriginalChannelValues = raw, IsOrthogonalityCorrected = raw is not null,
    };

    [Fact]
    public void DisplayAndRawValuesAreKeptApartAndTimesStartAtTheFirstReading()
    {
        var buffer = new ChartSampleBuffer();
        buffer.Reset(2, ["Bx", "By"], ["nT", "nT"]);

        buffer.Append(Reading(10, [1000, 2000], raw: [1, 2]));
        buffer.Append(Reading(10.5, [1001, 2001]));   // 未校正：原始值就是显示值

        var snapshot = buffer.Capture(0);
        Assert.Equal([0, 0.5], snapshot.Times);
        Assert.Equal([1000, 1001], snapshot.Display[0]);
        Assert.Equal([2000, 2001], snapshot.Display[1]);
        Assert.Equal([1, 1001], snapshot.Raw[0]);
        Assert.Equal([2, 2001], snapshot.Raw[1]);
        Assert.Equal(2, snapshot.TotalCount);
    }

    [Fact]
    public void CaptureCopiesOnlyTheTimeWindowAfterTheBufferWraps()
    {
        var buffer = new ChartSampleBuffer(capacity: 5);
        buffer.Reset(1, ["Bx"], ["nT"]);
        for (int i = 0; i <= 7; i++) buffer.Append(Reading(i, [100 + i], raw: [i]));

        // 只保留最后 5 个点（3..7 秒）；窗口 2 秒取 5..7 秒，含正好在窗口边界上的点。
        var window = buffer.Capture(2);
        Assert.Equal([5, 6, 7], window.Times);
        Assert.Equal([105, 106, 107], window.Display[0]);
        Assert.Equal([5, 6, 7], window.Raw[0]);
        Assert.Equal(5, window.TotalCount);

        Assert.Equal([3, 4, 5, 6, 7], buffer.Capture(0).Times);
        Assert.Equal([3, 4, 5, 6, 7], buffer.Capture(60).Times);
        Assert.Equal(5, buffer.Count);
    }

    [Fact]
    public void LateChannelsArePaddedWithNaNAndExtendTheLayout()
    {
        var buffer = new ChartSampleBuffer();
        buffer.Reset(1, ["Bx"], ["nT"]);

        Assert.False(buffer.Append(Reading(0, [10])));
        Assert.True(buffer.Append(Reading(1, [11, 21, 31], raw: [1, 2, 3])));
        Assert.False(buffer.Append(Reading(2, [12])));   // 短帧：缺的通道补 NaN

        var layout = buffer.Layout;
        Assert.Equal(3, layout.Count);
        Assert.Equal(["Bx", "CH1", "CH2"], layout.Names);
        Assert.Equal(["nT", "", ""], layout.Units);
        var snapshot = buffer.Capture(0);
        Assert.Equal([10, 11, 12], snapshot.Display[0]);
        Assert.Equal([double.NaN, 21, double.NaN], snapshot.Display[1]);
        Assert.Equal([double.NaN, 31, double.NaN], snapshot.Display[2]);
        Assert.Equal([10, 1, 12], snapshot.Raw[0]);
        Assert.Equal([double.NaN, 2, double.NaN], snapshot.Raw[1]);
    }

    [Fact]
    public void ResetShrinksTheLayoutAndKeepsUnusedChannelsAligned()
    {
        var buffer = new ChartSampleBuffer();
        buffer.Reset(1, ["Bx"], ["nT"]);
        buffer.Append(Reading(0, [1, 2, 3]));

        // 重新连接一个单通道协议：只复制布局内的通道，旧数据清空。
        buffer.Reset(1, ["Z"], ["µT"]);
        Assert.Equal(0, buffer.Count);
        Assert.Equal(1, buffer.Layout.Count);
        Assert.Equal(["Z"], buffer.Layout.Names);
        Assert.Equal(["µT"], buffer.Layout.Units);
        buffer.Append(Reading(5, [100]));
        Assert.Single(buffer.Capture(0).Display);

        // 之后又来了三通道读数：之前分配的通道一直跟着时间轴补 NaN，不会错位。
        Assert.True(buffer.Append(Reading(6, [101, 201, 301])));
        var snapshot = buffer.Capture(0);
        Assert.Equal([0, 1], snapshot.Times);
        Assert.Equal([100, 101], snapshot.Display[0]);
        Assert.Equal([double.NaN, 201], snapshot.Display[1]);
        Assert.Equal([double.NaN, 301], snapshot.Raw[2]);
        Assert.Equal(["Z", "CH1", "CH2"], buffer.Layout.Names);
    }

    [Fact]
    public void ClearKeepsTheLayoutAndRestartsTimeAtZero()
    {
        var buffer = new ChartSampleBuffer();
        buffer.Reset(2, ["Bx", "温度"], ["nT", "°C"]);
        buffer.Append(Reading(10, [1, 25]));
        buffer.Append(Reading(11, [2, 25]));

        buffer.Clear();
        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.Capture(0).Times);
        Assert.Equal(["Bx", "温度"], buffer.Layout.Names);

        buffer.Append(Reading(20, [3, 26]));
        Assert.Equal([0], buffer.Capture(0).Times);
    }

    [Fact]
    public void SnapshotsCopyTheWholeBuffer()
    {
        var buffer = new ChartSampleBuffer();
        buffer.Reset(2, ["Bx", "By"], ["nT", "µT"]);
        for (int i = 0; i < 4; i++) buffer.Append(Reading(i, [1000 + i, 2000 + i], raw: [i, 2 * i]));

        var raw = buffer.SnapshotRaw();
        Assert.Equal([0, 1, 2, 3], raw.Times);
        Assert.Equal([0, 1, 2, 3], raw.Channels[0]);
        Assert.Equal([0, 2, 4, 6], raw.Channels[1]);
        Assert.Equal(["Bx", "By"], raw.Names);
        Assert.Equal(["nT", "µT"], raw.Units);

        var display = buffer.SnapshotDisplay();
        Assert.Equal([1000, 1001, 1002, 1003], display[0]);
        Assert.Equal([2000, 2001, 2002, 2003], buffer.SnapshotDisplay(1));
        Assert.Null(buffer.SnapshotDisplay(2));
        Assert.Null(buffer.SnapshotDisplay(-1));

        // 副本与缓冲无关：改副本不影响下一次复制。
        raw.Channels[0][0] = 99;
        raw.Names[0] = "改名";
        display[0][0] = 99;
        Assert.Equal(0, buffer.SnapshotRaw().Channels[0][0]);
        Assert.Equal("Bx", buffer.SnapshotRaw().Names[0]);
        Assert.Equal(1000, buffer.SnapshotDisplay(0)![0]);
    }

    [Fact]
    public async Task CopiesTakenWhileReadingsArriveStayAligned()
    {
        var buffer = new ChartSampleBuffer(capacity: 1000);
        buffer.Reset(3, ["A", "B", "C"], ["nT", "nT", "nT"]);
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 20000; i++)
                buffer.Append(new MagnetometerReading
                {
                    Timestamp = Start.AddTicks(i * TimeSpan.TicksPerMillisecond),
                    ChannelValues = [i, i, i], OriginalChannelValues = [-i, -i, -i],
                });
        });

        while (!writer.IsCompleted)
        {
            var snapshot = buffer.Capture(0.2);
            Assert.All(snapshot.Display, values => Assert.Equal(snapshot.Times.Length, values.Length));
            Assert.All(snapshot.Raw, values => Assert.Equal(snapshot.Times.Length, values.Length));
            for (int i = 0; i < snapshot.Times.Length; i++)
            {
                // 同一行的时间、显示值和原始值来自同一条读数。
                Assert.Equal(snapshot.Times[i] * 1000, snapshot.Display[2][i], 6);
                Assert.Equal(-snapshot.Display[0][i], snapshot.Raw[1][i]);
            }
        }
        await writer;
        Assert.Equal(1000, buffer.Count);
    }
}
