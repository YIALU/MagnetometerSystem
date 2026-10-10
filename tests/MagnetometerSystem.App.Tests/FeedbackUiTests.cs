using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MagnetometerSystem.App.ViewModels;
using MagnetometerSystem.App.Views.Dialogs;
using MagnetometerSystem.Core.Feedback;
using MagnetometerSystem.Infrastructure.Feedback;

namespace MagnetometerSystem.App.Tests;

public sealed class FeedbackUiTests
{
    [Fact]
    public Task AnonymousFormAllowsEmptyOptionalFieldsAndKeepsAcquisitionWindowEnabled() => WpfTestHost.RunAsync(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "feedback-ui-" + Guid.NewGuid(), "draft.json");
        var client = new Client(); var vm = new FeedbackViewModel(client, new(path));
        var owner = new Window { Width = 200, Height = 200, Left = -10000, Top = -10000, ShowActivated = false };
        owner.Show();
        var dialog = new FeedbackDialog(vm) { Owner = owner, Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            dialog.Show(); await WpfTestHost.PumpAsync();
            ((TextBox)dialog.FindName("ScenarioInput")).Text = "串口采集后导出";
            ((TextBox)dialog.FindName("DescriptionInput")).Text = "希望导出时保留更多小数位";
            await WpfTestHost.PumpAsync();
            Assert.True(owner.IsEnabled); Assert.True(vm.CanEdit);
            dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
            await WpfTestHost.PumpAsync();
            ((ScrollViewer)((Grid)dialog.Content).Children[0]).ScrollToEnd();
            await WpfTestHost.PumpAsync();
            SaveScreenshot((FrameworkElement)dialog.Content);
            await vm.SubmitCommand.ExecuteAsync(null);
            Assert.True(vm.HasSubmitted); Assert.False(vm.CanEdit);
            Assert.True(string.IsNullOrEmpty(client.Last!.Name)); Assert.True(string.IsNullOrEmpty(client.Last.Contact));
            Assert.False(File.Exists(path)); Assert.Contains("反馈已收到", vm.Status);
        }
        finally { dialog.Close(); await WpfTestHost.PumpAsync(); owner.Close(); if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    });

    [Fact]
    public Task LostResponseRestartAndRetryPreserveIdAndPrivateFields() => WpfTestHost.RunAsync(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "feedback-retry-" + Guid.NewGuid(), "draft.json");
        try
        {
            var client = new Client { Fail = true }; var first = new FeedbackViewModel(client, new(path)); await first.InitializeAsync();
            first.Scenario = "连接后显示曲线"; first.Description = "想增加快捷键"; first.Name = "用户"; first.Contact = "example@example.invalid";
            await first.SubmitCommand.ExecuteAsync(null); var id = client.Last!.FeedbackId;
            Assert.False(first.HasSubmitted); Assert.True(File.Exists(path));
            client.Fail = false;
            var restarted = new FeedbackViewModel(client, new(path)); await restarted.InitializeAsync();
            Assert.Equal(first.Contact, restarted.Contact); await restarted.SubmitCommand.ExecuteAsync(null);
            Assert.Equal(id, client.Last!.FeedbackId); Assert.True(restarted.HasSubmitted);
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    });

    [Fact]
    public Task RequiredFieldsRejectEmptySubmissionWithoutCallingNetwork() => WpfTestHost.RunAsync(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "feedback-validation-" + Guid.NewGuid(), "draft.json");
        var client = new Client(); var vm = new FeedbackViewModel(client, new(path)); await vm.InitializeAsync();
        await vm.SubmitCommand.ExecuteAsync(null); Assert.Null(client.Last); Assert.Contains("使用场景", vm.Status);
    });

    private static void SaveScreenshot(FrameworkElement root)
    {
        var directory = Environment.GetEnvironmentVariable("MAGNETOMETER_TEST_SCREENSHOTS"); if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual(); using (var context = visual.RenderOpen())
        { var rect = new Rect(0, 0, root.ActualWidth, root.ActualHeight); context.DrawRectangle(Brushes.White, null, rect); context.DrawRectangle(new VisualBrush(root), null, rect); }
        bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(directory, "feedback-dialog.png")); encoder.Save(file);
    }
    [Fact]
    public Task DraftSaveFailureDoesNotSubmitOrClaimDurableSuccess() => WpfTestHost.RunAsync(async () =>
    {
        var blockingFile = Path.GetTempFileName();
        try
        {
            var client = new Client(); var vm = new FeedbackViewModel(client, new(Path.Combine(blockingFile, "draft.json")));
            await vm.InitializeAsync(); vm.Scenario = "连接"; vm.Description = "问题";
            await vm.SubmitCommand.ExecuteAsync(null);
            Assert.Null(client.Last); Assert.False(vm.HasSubmitted); Assert.Contains("本次未提交", vm.Status);
        }
        finally { File.Delete(blockingFile); }
    });
    [Fact]
    public Task AttachedLogsAreFixedOnFirstSubmitAndResentUnchangedAfterRestart() => WpfTestHost.RunAsync(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "feedback-logs-" + Guid.NewGuid(), "draft.json");
        try
        {
            var logs = new LogSource("第一次读取的日志");
            var client = new Client { Fail = true }; var first = new FeedbackViewModel(client, new(path), logs); await first.InitializeAsync();
            Assert.True(first.IncludeLogs);
            first.Scenario = "长时间采集"; first.Description = "断开后数据少了"; await first.SaveDraftAsync();
            await first.SubmitCommand.ExecuteAsync(null);
            var sent = client.Last!;
            Assert.True(FeedbackLogPayload.TryDecode(sent.Logs!, out var text)); Assert.Equal("第一次读取的日志", text);

            logs.Text = "之后又写入的日志"; client.Fail = false;
            var restarted = new FeedbackViewModel(client, new(path), logs); await restarted.InitializeAsync();
            Assert.True(restarted.IncludeLogs);
            Assert.Equal("第一次读取的日志", await restarted.PreviewLogsAsync());
            await restarted.SubmitCommand.ExecuteAsync(null);
            Assert.True(restarted.HasSubmitted);
            Assert.Equal(sent, client.Last);
            Assert.Equal(1, logs.Calls);
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    });

    [Fact]
    public Task UncheckedOrUnavailableLogsStillSubmitText() => WpfTestHost.RunAsync(async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), "feedback-nologs-" + Guid.NewGuid(), "draft.json");
        var owner = new Window { Width = 200, Height = 200, Left = -10000, Top = -10000, ShowActivated = false };
        try
        {
            var client = new Client(); var vm = new FeedbackViewModel(client, new(path), new LogSource("不应发送"));
            owner.Show();
            var dialog = new FeedbackDialog(vm) { Owner = owner, Left = -10000, Top = -10000, ShowActivated = false };
            dialog.Show(); await WpfTestHost.PumpAsync();
            var checkbox = (CheckBox)dialog.FindName("IncludeLogsInput");
            Assert.True(checkbox.IsVisible); Assert.True(checkbox.IsChecked);
            checkbox.IsChecked = false; vm.Scenario = "导出"; vm.Description = "问题";
            await vm.SubmitCommand.ExecuteAsync(null);
            Assert.Null(client.Last!.Logs); Assert.True(vm.HasSubmitted);
            dialog.Close(); await WpfTestHost.PumpAsync();

            var failing = new FeedbackViewModel(client, new(path), new LogSource(null) { Fail = true }); await failing.InitializeAsync();
            failing.Scenario = "导出"; failing.Description = "日志读不到";
            await failing.SubmitCommand.ExecuteAsync(null);
            Assert.True(failing.HasSubmitted); Assert.Equal("", client.Last!.Logs);

            var withoutSource = new FeedbackViewModel(client, new(path)); await withoutSource.InitializeAsync();
            Assert.False(withoutSource.CanAttachLogs); Assert.False(withoutSource.IncludeLogs);
        }
        finally { owner.Close(); if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    });

    private sealed class LogSource(string? text) : IFeedbackLogSource
    {
        public string? Text { get; set; } = text; public bool Fail { get; set; } public int Calls { get; private set; }
        public Task<string?> CollectAsync(CancellationToken ct = default)
        { Calls++; return Fail ? Task.FromException<string?>(new IOException("locked")) : Task.FromResult(Text); }
    }
    private sealed class Client : IFeedbackClient
    {
        public FeedbackSubmission? Last { get; private set; } public bool Fail { get; set; }
        public Task<FeedbackReceipt> SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default)
        { Last = submission; return Fail ? Task.FromException<FeedbackReceipt>(new HttpRequestException("lost response")) : Task.FromResult(new FeedbackReceipt(submission.FeedbackId, "pending")); }
    }
}
