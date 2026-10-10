using MagnetometerSystem.App.Services;
using MagnetometerSystem.Core.Storage;

namespace MagnetometerSystem.App.Tests;

/// <summary>不弹窗的对话框：按预设回答，并记下每次请求。</summary>
internal sealed class FakeDialogService : IDialogService
{
    public bool ConfirmAnswer { get; set; }
    public string? OpenFilePath { get; set; }
    public string? SaveFilePath { get; set; }
    public SessionInfo? Session { get; set; }

    public List<(string Message, string Title)> Confirmations { get; } = [];
    public List<(string Message, string Title)> Notifications { get; } = [];
    public List<(string Title, string Filter, string DefaultExt)> OpenFileRequests { get; } = [];
    public List<(string Title, string Filter, string DefaultExt, string FileName)> SaveFileRequests { get; } = [];
    public List<IDataStorageService> SessionRequests { get; } = [];
    public List<HelpTopic> HelpTopics { get; } = [];

    public bool Confirm(string message, string title)
    {
        Confirmations.Add((message, title));
        return ConfirmAnswer;
    }

    public void Notify(string message, string title) => Notifications.Add((message, title));

    public string? PickOpenFile(string title, string filter, string defaultExt)
    {
        OpenFileRequests.Add((title, filter, defaultExt));
        return OpenFilePath;
    }

    public string? PickSaveFile(string title, string filter, string defaultExt, string fileName)
    {
        SaveFileRequests.Add((title, filter, defaultExt, fileName));
        return SaveFilePath;
    }

    public SessionInfo? PickSession(IDataStorageService storage)
    {
        SessionRequests.Add(storage);
        return Session;
    }

    public void ShowHelp(HelpTopic topic) => HelpTopics.Add(topic);
}
