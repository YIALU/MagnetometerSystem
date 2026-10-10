using MagnetometerSystem.Core.Storage;

namespace MagnetometerSystem.App.Services;

/// <summary>帮助对话框的内容。</summary>
public enum HelpTopic
{
    /// <summary>正交度校正可导入的 CSV 格式。</summary>
    CalibrationCsvFormat,

    /// <summary>如何手动用已保存的正交度参数计算校正结果。</summary>
    ProfileUsage,
}

/// <summary>
/// 视图模型用到的对话框：确认、提示、选文件、选会话和帮助。
/// 正式程序用 <see cref="WpfDialogService"/>；测试换成按预设回答、不弹窗的实现，导入、导出、删除命令因此可以测试。
/// </summary>
public interface IDialogService
{
    /// <summary>“是 / 否”确认（警告图标），选“是”时返回 true。</summary>
    bool Confirm(string message, string title);

    /// <summary>只有“确定”按钮的提示。</summary>
    void Notify(string message, string title);

    /// <summary>选择要打开的文件，取消时返回 null。</summary>
    string? PickOpenFile(string title, string filter, string defaultExt);

    /// <summary>选择保存位置，<paramref name="fileName"/> 是建议的文件名；取消时返回 null。</summary>
    string? PickSaveFile(string title, string filter, string defaultExt, string fileName);

    /// <summary>从数据库中选一个会话，取消时返回 null。</summary>
    SessionInfo? PickSession(IDataStorageService storage);

    /// <summary>显示帮助，关闭后返回。</summary>
    void ShowHelp(HelpTopic topic);
}
