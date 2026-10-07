using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MagnetometerSystem.App.Views.Dialogs;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Database;
using MagnetometerSystem.Infrastructure.Update;
using Microsoft.Data.Sqlite;

namespace MagnetometerSystem.App.Tests;

/// <summary>对话框只继承全局样式：逐个打开（屏幕外），确认没有绑定错误；设置 MAGNETOMETER_TEST_SCREENSHOTS 时输出截图。</summary>
public class DialogRenderTests
{
    [Fact]
    public Task AllDialogsRenderWithoutBindingErrors() => WpfTestHost.RunAsync(async () =>
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"dialogs-{Guid.NewGuid():N}.db");
        var db = new DatabaseInitializer(dbPath); await db.InitializeAsync();
        var storage = new SqliteStorageService(db, new DataBus());
        var errors = new StringWriter(); var listener = new TextWriterTraceListener(errors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        var screenshot = Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_SCREENSHOTS");
        try
        {
            var command = new DeviceCommand
            {
                Name = "设置输出频率", Encoding = CommandEncoding.AsciiTemplate, Template = "RATE {rate}",
                Parameters = [new CommandParameter { Key = "rate", Name = "频率", Type = CommandParameterType.Int, DefaultValue = "10" }],
            };
            var update = new UpdateDialog(new GiteeUpdateService(new UpdateOptions { CurrentVersion = "1.0.0", PackageKind = AppPackageKind.Portable }),
                new UpdateInfo { Version = "9.9.9", TagName = "v9.9.9", HtmlUrl = "https://example.invalid/release", ReleaseNotes = "- 修复\n- 改进" });
            var dialogs = new (string Name, Window Dialog)[]
            {
                ("about", new AboutDialog()),
                ("command-edit", new CommandEditDialog(command, "编辑命令")),
                ("csv-help", new CsvFormatHelpDialog()),
                ("profile-help", new ProfileUsageHelpDialog()),
                ("session-picker", new SessionPickerDialog(storage)),
                ("update", update),
            };
            foreach (var (name, dialog) in dialogs)
            {
                dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                dialog.Left = -10000; dialog.Top = -10000;
                dialog.ShowActivated = false; dialog.ShowInTaskbar = false;
                dialog.Show();
                await WpfTestHost.PumpAsync();
                dialog.UpdateLayout();
                if (!string.IsNullOrEmpty(screenshot) && dialog.Content is FrameworkElement content)
                    Save(content, Path.Combine(screenshot, $"dialog-{name}.png"));
                dialog.Close();
            }
            Assert.DoesNotContain("System.Windows.Data Error", errors.ToString());
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
            storage.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(dbPath + suffix);
        }
    });

    private static void Save(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)element.ActualWidth), Math.Max(1, (int)element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
