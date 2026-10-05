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
    private sealed class Client : IFeedbackClient
    {
        public FeedbackSubmission? Last { get; private set; } public bool Fail { get; set; }
        public Task<FeedbackReceipt> SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default)
        { Last = submission; return Fail ? Task.FromException<FeedbackReceipt>(new HttpRequestException("lost response")) : Task.FromResult(new FeedbackReceipt(submission.FeedbackId, "pending")); }
    }
}
