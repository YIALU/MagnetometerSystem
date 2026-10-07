using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MagnetometerSystem.App.Controls;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.App.Tests;

public class WorkspaceControlsTests
{
    [Fact]
    public Task ToolbarPanel_WrapsTheRightGroupInsteadOfClippingEitherGroup() => WpfTestHost.RunAsync(() =>
    {
        var left = new Border { Width = 420, Height = 30 };
        var right = new Border { Width = 360, Height = 30 };
        var panel = new ToolbarPanel { Spacing = 12, Children = { left, right } };

        // 放得下：同一行，右组贴右边。
        panel.Measure(new Size(900, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 900, panel.DesiredSize.Height));
        Assert.False(panel.IsWrapped);
        Assert.Equal(30, panel.DesiredSize.Height);
        Assert.Equal(new Rect(540, 0, 360, 30), LayoutInformation.GetLayoutSlot(right));

        // 放不下（如 1100 px 窗口里的图表栏）：右组换到第二行，两组都完整。
        panel.Measure(new Size(620, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 620, panel.DesiredSize.Height));
        Assert.True(panel.IsWrapped);
        Assert.Equal(72, panel.DesiredSize.Height);
        Assert.Equal(new Rect(0, 0, 420, 30), LayoutInformation.GetLayoutSlot(left));
        Assert.Equal(new Rect(260, 42, 360, 30), LayoutInformation.GetLayoutSlot(right));
        return Task.CompletedTask;
    });

    [Fact]
    public Task WeightPanel_ManySegmentsNeverProduceNegativeWidths() => WpfTestHost.RunAsync(() =>
    {
        // 与内置 21 通道“磁梯度数采卡-pt”协议相同的帧段：最小宽度之和超过较窄的编辑区宽度。
        var segments = ProtocolConfig.CreateZdzC08().Segments;
        var panel = new WeightPanel { MinItemWidth = 38 };
        foreach (var segment in segments)
        {
            var item = new Border();
            WeightPanel.SetWeight(item, segment.ByteCount);
            panel.Children.Add(item);
        }
        foreach (var width in new[] { 2400.0, 900, 600, 120, 0 })
        {
            panel.Measure(new Size(width, 46));
            panel.Arrange(new Rect(0, 0, width, 46));
            var widths = panel.Children.Cast<Border>().Select(b => b.RenderSize.Width).ToArray();
            Assert.All(widths, w => Assert.True(w >= 0, $"宽度 {width} 时出现负宽度"));
            Assert.True(widths.Sum() <= width + 0.01);
            if (segments.Count * panel.MinItemWidth <= width)
                Assert.All(widths, w => Assert.True(w >= panel.MinItemWidth - 0.01));
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task ProjectionPlot_SubscribesToSamplesOnlyWhileLoaded() => WpfTestHost.RunAsync(async () =>
    {
        var samples = new CountingCollection();
        var plot = new ProjectionPlot { Points = samples, Width = 120, Height = 120 };
        Assert.Equal(0, samples.Subscribers);
        var window = new Window
        {
            Content = plot, Width = 200, Height = 200,
            Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false,
        };
        try
        {
            window.Show(); await WpfTestHost.PumpAsync();
            Assert.Equal(1, samples.Subscribers);

            // 切页卸载后，ViewModel 持有的集合不能再留住旧控件。
            window.Content = null; await WpfTestHost.PumpAsync();
            Assert.Equal(0, samples.Subscribers);
            window.Content = plot; await WpfTestHost.PumpAsync();
            Assert.Equal(1, samples.Subscribers);

            var replacement = new CountingCollection();
            plot.Points = replacement;
            Assert.Equal(0, samples.Subscribers);
            Assert.Equal(1, replacement.Subscribers);
            window.Close(); await WpfTestHost.PumpAsync();
            Assert.Equal(0, replacement.Subscribers);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void ProjectionPlot_SamplesLargeCollectionsWithoutReadingEveryItem()
    {
        // 长时连续校正采集可达数百万样本：预览只按下标读取均匀抽样的点，不复制整个集合。
        var samples = new CountingList(1_000_000);
        var picked = ProjectionPlot.Sample(samples);
        Assert.InRange(picked.Count, ProjectionPlot.MaxDrawnPoints / 2, ProjectionPlot.MaxDrawnPoints);
        Assert.InRange(samples.Reads, 1, ProjectionPlot.MaxDrawnPoints);
        Assert.Equal(0, picked[0][0]);                        // 从头开始
        Assert.True(picked[^1][0] > 990_000);                 // 覆盖到末尾
    }

    [Theory]
    [InlineData(59, "00:00:59")]
    [InlineData(25 * 3600 + 3 * 60 + 12, "25:03:12")]   // 超过 24 小时不回绕
    [InlineData(100 * 3600, "100:00:00")]
    public void LinkTimerShowsTotalHours(int seconds, string expected) =>
        Assert.Equal(expected, MagnetometerSystem.App.ViewModels.MainViewModel.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    private sealed class CountingList(int count) : IList
    {
        public int Reads { get; private set; }
        public object? this[int index]
        {
            get { Reads++; return new double[] { index, 0, 0 }; }
            set => throw new NotSupportedException();
        }
        public int Count => count;
        public bool IsFixedSize => true;
        public bool IsReadOnly => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(object? value) => throw new NotSupportedException();
        public int IndexOf(object? value) => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public void CopyTo(Array array, int index) => throw new NotSupportedException();
        public IEnumerator GetEnumerator() => throw new NotSupportedException("预览不能遍历整个集合");
    }

    private sealed class CountingCollection : INotifyCollectionChanged, IEnumerable
    {
        private NotifyCollectionChangedEventHandler? _handlers;
        public int Subscribers => _handlers?.GetInvocationList().Length ?? 0;

        public event NotifyCollectionChangedEventHandler? CollectionChanged
        {
            add => _handlers += value;
            remove => _handlers -= value;
        }

        public IEnumerator GetEnumerator()
        {
            yield return new double[] { 1, 2, 3 };
        }
    }
}
