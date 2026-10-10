using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using MagnetometerSystem.App.ViewModels;

namespace MagnetometerSystem.App.Views.Dialogs;

public partial class FeedbackDialog : Window
{
    private readonly FeedbackViewModel _vm;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _closeReady;
    private bool _closing;
    public FeedbackDialog(FeedbackViewModel vm)
    {
        InitializeComponent(); DataContext = _vm = vm;
        Loaded += async (_, _) => { await _vm.InitializeAsync(); _saveTimer.Start(); };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await _vm.SaveDraftAsync(); if (!_closeReady) _saveTimer.Start(); };
        Closing += async (_, e) =>
        {
            if (_closeReady) return;
            e.Cancel = true;
            if (_closing) return;
            if (_vm.IsSubmitting) { _vm.Status = "正在提交，请等待回执后关闭。"; return; }
            _closing = true; _saveTimer.Stop(); await _vm.SaveDraftAsync(); _closeReady = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        };
        Closed += (_, _) => _saveTimer.Stop();
    }
    public Task SaveDraftAsync() => _vm.SaveDraftAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void PreviewLogs_Click(object sender, RoutedEventArgs e)
    {
        string? text;
        try { text = await _vm.PreviewLogsAsync(); }
        catch { text = null; }
        var box = new TextBox
        {
            Text = string.IsNullOrEmpty(text) ? "没有找到可附带的日志。" : text,
            IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(0), Padding = new Thickness(8)
        };
        var preview = new Window
        {
            Title = "将随反馈发送的日志", Owner = this, Width = 760, Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Content = box
        };
        preview.Loaded += (_, _) => box.ScrollToEnd();
        preview.Show();
    }

    private void IssueLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true }); }
        catch { _vm.Status = "无法打开浏览器，请稍后重试。"; }
        e.Handled = true;
    }
}
