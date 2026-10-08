using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Database;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

/// <summary>分析页结果表：通道多时在表内滚动，不挤掉曲线；通道少时按内容收缩。</summary>
public class AnalysisLayoutTests
{
    [Theory]
    [InlineData(21, true)]
    [InlineData(2, false)]
    public Task ResultTable_ScrollsWhenChannelsExceedItsShare(int channels, bool expectScroll) => WpfTestHost.RunAsync(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"analysis_layout_{Guid.NewGuid():N}.db");
        var db = new DatabaseInitializer(path);
        await db.InitializeAsync();
        var storage = new SqliteStorageService(db, new DataBus());
        Window? window = null;
        try
        {
            var names = Enumerable.Range(0, channels).Select(i => $"CH{i}").ToArray();
            var id = await storage.StartSessionAsync("多通道", new SensorConfig
            {
                Type = SensorType.Generic, SampleRate = 10, ChannelCountOverride = channels,
                ChannelNamesOverride = names, ChannelUnitsOverride = names.Select(_ => "nT").ToArray(),
            }, new ConnectionConfig());
            var start = DateTime.Now.AddMinutes(-1);
            await storage.SaveReadingsAsync(Enumerable.Range(0, 300).Select(i => new MagnetometerReading
            {
                SessionId = id, Timestamp = start.AddMilliseconds(100 * i), SensorType = SensorType.Generic,
                ChannelValues = Enumerable.Range(0, channels).Select(c => 50000.0 + c + Math.Sin(i * 0.1)).ToArray(),
            }).ToList());
            await storage.EndSessionAsync(id);
            using (var conn = new SqliteConnection(db.ConnectionString))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE sessions SET started_at = $s, ended_at = $e WHERE id = $id";
                cmd.Parameters.AddWithValue("$s", start.ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$e", start.AddSeconds(30).ToUniversalTime().ToString("O"));
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }

            var vm = new AnalysisViewModel(storage);
            await vm.EnsureLoadedAsync();
            foreach (var c in vm.Channels) c.IsSelected = true;
            await vm.RunCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
            Assert.Equal(channels, vm.Results.Count);

            var view = new AnalysisView { DataContext = vm };
            window = new Window
            {
                Content = view, Width = 1300, Height = 820, Left = -10000, Top = -10000,
                ShowActivated = false, ShowInTaskbar = false,
            };
            window.Show();
            await WpfTestHost.PumpAsync();
            window.UpdateLayout();

            var column = (FrameworkElement)view.FindName("ResultsColumn");
            var list = Descendants<ListBox>(view).Single(l => ReferenceEquals(l.ItemsSource, vm.Results));
            var scroller = Descendants<ScrollViewer>(list).First();
            var panel = Ancestor<Border>(list, b => Grid.GetRow(b) == 0 && ReferenceEquals(b.Parent, column));
            var plot = (FrameworkElement)view.FindName("Plot");

            Assert.True(panel.ActualHeight <= column.ActualHeight * 0.55 + 1, $"{panel.ActualHeight} / {column.ActualHeight}");
            Assert.True(plot.ActualHeight >= 150, $"曲线高度 {plot.ActualHeight}");
            if (expectScroll)
            {
                Assert.True(scroller.ScrollableHeight > 0, "结果多于可见行时表内应可滚动");
                list.ScrollIntoView(vm.Results[^1]);
                await WpfTestHost.PumpAsync();
                window.UpdateLayout();
                var last = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(vm.Results[^1]);
                Assert.NotNull(last);
                var bottom = last.TransformToAncestor(scroller).Transform(new Point(0, last.ActualHeight)).Y;
                Assert.True(bottom <= scroller.ActualHeight + 1, $"最后一个通道应能滚动到可见区域：{bottom} / {scroller.ActualHeight}");
            }
            else
            {
                Assert.Equal(0, scroller.ScrollableHeight);
                Assert.True(panel.ActualHeight < column.ActualHeight * 0.55 - 20, "结果少时面板按内容收缩，空间留给曲线");
            }
        }
        finally
        {
            window?.Close();
            storage.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static T Ancestor<T>(DependencyObject start, Func<T, bool> predicate) where T : DependencyObject
    {
        for (var node = VisualTreeHelper.GetParent(start); node != null; node = VisualTreeHelper.GetParent(node))
            if (node is T match && predicate(match)) return match;
        throw new InvalidOperationException($"找不到满足条件的 {typeof(T).Name}");
    }
}
