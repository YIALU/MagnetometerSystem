using System.Windows;
using Microsoft.Win32;
using MagnetometerSystem.App.Views.Dialogs;
using MagnetometerSystem.Core.Storage;

namespace MagnetometerSystem.App.Services;

/// <summary>用 WPF 消息框、系统文件对话框和程序自带的对话框实现 <see cref="IDialogService"/>。</summary>
public sealed class WpfDialogService : IDialogService
{
    public bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void Notify(string message, string title) => MessageBox.Show(message, title);

    public string? PickOpenFile(string title, string filter, string defaultExt)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, DefaultExt = defaultExt };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultExt, string fileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, DefaultExt = defaultExt };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public SessionInfo? PickSession(IDataStorageService storage)
    {
        var picker = new SessionPickerDialog(storage) { Owner = Application.Current?.MainWindow };
        return picker.ShowDialog() == true ? picker.SelectedSession : null;
    }

    public void ShowHelp(HelpTopic topic)
    {
        Window dialog = topic switch
        {
            HelpTopic.CalibrationCsvFormat => new CsvFormatHelpDialog(),
            HelpTopic.ProfileUsage => new ProfileUsageHelpDialog(),
            _ => throw new ArgumentOutOfRangeException(nameof(topic), topic, null),
        };
        dialog.Owner = Application.Current?.MainWindow;
        dialog.ShowDialog();
    }
}
