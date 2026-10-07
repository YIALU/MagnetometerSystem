using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using MagnetometerSystem.App.Controls;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.App.Tests;

public class WorkspaceControlsTests
{
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

    [Theory]
    [InlineData(59, "00:00:59")]
    [InlineData(25 * 3600 + 3 * 60 + 12, "25:03:12")]   // 超过 24 小时不回绕
    [InlineData(100 * 3600, "100:00:00")]
    public void LinkTimerShowsTotalHours(int seconds, string expected) =>
        Assert.Equal(expected, MagnetometerSystem.App.ViewModels.MainViewModel.FormatElapsed(TimeSpan.FromSeconds(seconds)));

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
