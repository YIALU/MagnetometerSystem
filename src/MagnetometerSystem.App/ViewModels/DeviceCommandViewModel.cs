using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;
using MagnetometerSystem.Infrastructure.Configuration;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 参数运行时绑定 —— 保存当前输入值，供 UI 双向绑定
/// </summary>
public partial class CommandParameterBinding : ObservableObject
{
    public CommandParameter Definition { get; }

    [ObservableProperty]
    private string _value = "";

    public CommandParameterBinding(CommandParameter def)
    {
        Definition = def;
        Value = def.DefaultValue;
    }
}

/// <summary>
/// 设备命令 ViewModel — 支持命令目录、命令组、参数化命令，
/// 保留底部"自由发送"区兼容手动输入。
/// </summary>
public partial class DeviceCommandViewModel : ObservableObject
{
    private readonly DataBus _dataBus;
    private readonly IAppConfigService _configService;
    private IDeviceConnection? _connection;
    private const int MaxLogLength = 50_000;
    private const string CatalogKey = "device.commandCatalog";

    // ---- 目录 / 组 / 命令 ----
    public ObservableCollection<CommandGroup> Groups { get; } = new();

    [ObservableProperty]
    private CommandGroup? _selectedGroup;

    public ObservableCollection<DeviceCommand> CurrentCommands { get; } = new();

    [ObservableProperty]
    private DeviceCommand? _selectedCommand;

    public ObservableCollection<CommandParameterBinding> CurrentParameters { get; } = new();

    [ObservableProperty]
    private string _previewText = "";

    // ---- 自由发送 ----
    [ObservableProperty]
    private string _freeCommandText = "";

    [ObservableProperty]
    private bool _freeIsHexMode;

    [ObservableProperty]
    private bool _freeAppendNewline = true;

    [ObservableProperty]
    private bool _showFreeSend;

    // ---- 日志 / 状态 ----
    [ObservableProperty]
    private string _communicationLog = "";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _pauseAutoScroll;

    // ---- 日志缓冲（高吞吐下按 100ms 节拍批量 flush，避免每帧都触发 UI 重绘）----
    private readonly StringBuilder _logBuffer = new();
    private readonly object _logBufferLock = new();
    private readonly DispatcherTimer _logFlushTimer;

    public DeviceCommandViewModel(DataBus dataBus, IAppConfigService configService)
    {
        _dataBus = dataBus;
        _configService = configService;
        _connection = dataBus.CurrentConnection;
        IsConnected = _connection != null;
        if (_connection != null)
            _connection.DataReceived += OnDataReceived;

        _dataBus.ConnectionChanged += OnConnectionChanged;

        _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _logFlushTimer.Tick += (_, _) => FlushLogBuffer();
        _logFlushTimer.Start();

        _ = LoadCatalogAsync();
    }

    private async Task LoadCatalogAsync()
    {
        CommandCatalog? catalog = null;
        try
        {
            catalog = await _configService.GetAsync<CommandCatalog>(CatalogKey);
        }
        catch { /* fall through to default */ }

        catalog ??= CommandCatalog.CreateDefault();

        Application.Current?.Dispatcher.Invoke(() =>
        {
            Groups.Clear();
            foreach (var g in catalog.Groups) Groups.Add(g);
            SelectedGroup = Groups.FirstOrDefault();
        });
    }

    private async Task SaveCatalogAsync()
    {
        var catalog = new CommandCatalog { Groups = Groups.ToList() };
        try { await _configService.SetAsync(CatalogKey, catalog); }
        catch (Exception ex) { AppendToLog($"[ERR] 保存目录失败: {ex.Message}\n"); }
    }

    partial void OnSelectedGroupChanged(CommandGroup? value)
    {
        CurrentCommands.Clear();
        if (value != null)
        {
            foreach (var c in value.Commands) CurrentCommands.Add(c);
        }
        SelectedCommand = CurrentCommands.FirstOrDefault();
    }

    partial void OnSelectedCommandChanged(DeviceCommand? value)
    {
        DetachParameterListeners();
        CurrentParameters.Clear();
        if (value != null)
        {
            foreach (var p in value.Parameters)
            {
                var binding = new CommandParameterBinding(p);
                binding.PropertyChanged += OnParameterBindingChanged;
                CurrentParameters.Add(binding);
            }
        }
        UpdatePreview();
    }

    private void DetachParameterListeners()
    {
        foreach (var b in CurrentParameters)
            b.PropertyChanged -= OnParameterBindingChanged;
    }

    private void OnParameterBindingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandParameterBinding.Value))
            UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (SelectedCommand == null)
        {
            PreviewText = "";
            return;
        }

        var values = BuildParamDict();
        try
        {
            if (SelectedCommand.Encoding == CommandEncoding.AsciiTemplate)
            {
                var rendered = CommandFrameBuilder.RenderAsciiTemplate(SelectedCommand, values);
                PreviewText = SelectedCommand.AppendNewline ? rendered + "\\r\\n" : rendered;
            }
            else
            {
                var preview = CommandFrameBuilder.BuildBinaryFrame(SelectedCommand, values);
                var sb = new StringBuilder();
                if (preview.HeaderBytes.Length > 0)
                    sb.AppendLine($"帧头 : {CommandFrameBuilder.ToHexString(preview.HeaderBytes)}");
                sb.AppendLine($"数据 : {CommandFrameBuilder.ToHexString(preview.DataBytes)}");
                if (preview.ChecksumBytes.Length > 0)
                    sb.AppendLine($"校验 : {CommandFrameBuilder.ToHexString(preview.ChecksumBytes)}");
                if (preview.TailBytes.Length > 0)
                    sb.AppendLine($"帧尾 : {CommandFrameBuilder.ToHexString(preview.TailBytes)}");
                var full = preview.FullBytes;
                sb.Append($"完整 : {CommandFrameBuilder.ToHexString(full)}  ({full.Length} 字节)");
                PreviewText = sb.ToString();
            }
        }
        catch (Exception ex)
        {
            PreviewText = $"[预览错误] {ex.Message}";
        }
    }

    private Dictionary<string, string> BuildParamDict()
    {
        var dict = new Dictionary<string, string>();
        foreach (var p in CurrentParameters)
            dict[p.Definition.Key] = p.Value ?? "";
        return dict;
    }

    // ---- 发送 ----

    [RelayCommand]
    private async Task SendSelectedCommandAsync()
    {
        if (SelectedCommand == null) return;
        if (_connection == null)
        {
            AppendToLog("[ERR] 未连接设备\n");
            return;
        }

        try
        {
            var values = BuildParamDict();
            byte[] data;
            string display;

            if (SelectedCommand.Encoding == CommandEncoding.AsciiTemplate)
            {
                data = CommandFrameBuilder.BuildAsciiBytes(SelectedCommand, values);
                var rendered = CommandFrameBuilder.RenderAsciiTemplate(SelectedCommand, values);
                display = rendered + (SelectedCommand.AppendNewline ? "\\r\\n" : "");
            }
            else
            {
                data = CommandFrameBuilder.BuildBinaryFrame(SelectedCommand, values).FullBytes;
                display = CommandFrameBuilder.ToHexString(data);
            }

            await _connection.SendAsync(data);
            AppendToLog($"[TX {DateTime.Now:HH:mm:ss}] {display}\n");
        }
        catch (Exception ex)
        {
            AppendToLog($"[ERR] 发送失败: {ex.Message}\n");
        }
    }

    [RelayCommand]
    private async Task SendFreeCommandAsync()
    {
        if (_connection == null)
        {
            AppendToLog("[ERR] 未连接设备\n");
            return;
        }
        if (string.IsNullOrEmpty(FreeCommandText)) return;

        try { await SendRawAsync(FreeCommandText, FreeIsHexMode, FreeAppendNewline); }
        catch (Exception ex) { AppendToLog($"[ERR] 发送失败: {ex.Message}\n"); }
    }

    private async Task SendRawAsync(string text, bool isHex, bool appendNewline)
    {
        if (_connection == null) return;

        byte[] data;
        string display;

        if (isHex)
        {
            var hex = Regex.Replace(text, @"[\s\-]", "");
            if (hex.Length == 0)
            {
                AppendToLog("[ERR] Hex 命令为空\n");
                return;
            }
            if (hex.Length % 2 != 0)
            {
                AppendToLog("[ERR] Hex 字符串长度不合法\n");
                return;
            }
            try
            {
                data = Enumerable.Range(0, hex.Length / 2)
                    .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                    .ToArray();
            }
            catch (FormatException)
            {
                AppendToLog("[ERR] Hex 字符串包含非法字符\n");
                return;
            }
            display = BitConverter.ToString(data).Replace("-", " ");
        }
        else
        {
            var toSend = appendNewline ? text + "\r\n" : text;
            data = Encoding.UTF8.GetBytes(toSend);
            display = text + (appendNewline ? "\\r\\n" : "");
        }

        await _connection.SendAsync(data);
        AppendToLog($"[TX {DateTime.Now:HH:mm:ss}] {display}\n");
    }

    // ---- 目录管理 ----

    [RelayCommand]
    private async Task AddGroupAsync()
    {
        var name = PromptInput("新建命令组", "组名:", "新命令组");
        if (string.IsNullOrWhiteSpace(name)) return;

        var group = new CommandGroup { Name = name };
        Groups.Add(group);
        SelectedGroup = group;
        await SaveCatalogAsync();
    }

    [RelayCommand]
    private async Task RenameGroupAsync(CommandGroup? group)
    {
        group ??= SelectedGroup;
        if (group == null) return;

        var name = PromptInput("重命名命令组", "组名:", group.Name);
        if (string.IsNullOrWhiteSpace(name) || name == group.Name) return;

        group.Name = name;
        // 触发 UI 刷新
        var idx = Groups.IndexOf(group);
        if (idx >= 0)
        {
            Groups.RemoveAt(idx);
            Groups.Insert(idx, group);
            SelectedGroup = group;
        }
        await SaveCatalogAsync();
    }

    [RelayCommand]
    private async Task DeleteGroupAsync(CommandGroup? group)
    {
        group ??= SelectedGroup;
        if (group == null) return;

        var result = MessageBox.Show(
            $"确定删除命令组 '{group.Name}' 及其 {group.Commands.Count} 条命令？",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        Groups.Remove(group);
        SelectedGroup = Groups.FirstOrDefault();
        await SaveCatalogAsync();
    }

    [RelayCommand]
    private async Task AddCommandAsync()
    {
        if (SelectedGroup == null)
        {
            MessageBox.Show("请先选择或创建命令组", "提示");
            return;
        }

        var cmd = new DeviceCommand { Name = "新命令", Template = "" };
        var dlg = new Views.Dialogs.CommandEditDialog(cmd, "新建命令");
        dlg.Owner = Application.Current?.MainWindow;
        if (dlg.ShowDialog() != true) return;

        SelectedGroup.Commands.Add(cmd);
        CurrentCommands.Add(cmd);
        SelectedCommand = cmd;
        await SaveCatalogAsync();
    }

    [RelayCommand]
    private async Task EditCommandAsync(DeviceCommand? cmd)
    {
        cmd ??= SelectedCommand;
        if (cmd == null) return;

        var dlg = new Views.Dialogs.CommandEditDialog(cmd, "编辑命令");
        dlg.Owner = Application.Current?.MainWindow;
        if (dlg.ShowDialog() != true) return;

        // 刷新参数绑定
        OnSelectedCommandChanged(cmd);
        // 刷新 UI 列表中的显示
        var idx = CurrentCommands.IndexOf(cmd);
        if (idx >= 0)
        {
            CurrentCommands.RemoveAt(idx);
            CurrentCommands.Insert(idx, cmd);
            SelectedCommand = cmd;
        }
        await SaveCatalogAsync();
    }

    [RelayCommand]
    private async Task DeleteCommandAsync(DeviceCommand? cmd)
    {
        cmd ??= SelectedCommand;
        if (cmd == null || SelectedGroup == null) return;

        var result = MessageBox.Show(
            $"确定删除命令 '{cmd.Name}'？", "确认删除",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        SelectedGroup.Commands.Remove(cmd);
        CurrentCommands.Remove(cmd);
        SelectedCommand = CurrentCommands.FirstOrDefault();
        await SaveCatalogAsync();
    }

    [RelayCommand]
    private async Task ImportCatalogAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入命令目录",
            Filter = "JSON 文件 (*.json)|*.json",
            DefaultExt = ".json"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var json = await File.ReadAllTextAsync(dlg.FileName);
            var catalog = System.Text.Json.JsonSerializer.Deserialize<CommandCatalog>(json);
            if (catalog == null)
            {
                MessageBox.Show("文件格式错误", "错误");
                return;
            }

            Groups.Clear();
            foreach (var g in catalog.Groups) Groups.Add(g);
            SelectedGroup = Groups.FirstOrDefault();
            await SaveCatalogAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败: {ex.Message}", "错误");
        }
    }

    [RelayCommand]
    private async Task ExportCatalogAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出命令目录",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = "command-catalog.json",
            DefaultExt = ".json"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var catalog = new CommandCatalog { Groups = Groups.ToList() };
            var json = System.Text.Json.JsonSerializer.Serialize(catalog,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(dlg.FileName, json);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败: {ex.Message}", "错误");
        }
    }

    [RelayCommand]
    private void ClearLog() => CommunicationLog = "";

    // ---- 连接 / 接收 ----

    private void OnConnectionChanged(IDeviceConnection? connection)
    {
        if (_connection != null)
            _connection.DataReceived -= OnDataReceived;

        _connection = connection;
        IsConnected = connection != null;

        if (_connection != null)
            _connection.DataReceived += OnDataReceived;
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        bool isAscii = data.All(b => (b >= 0x20 && b <= 0x7E) || b == '\r' || b == '\n' || b == '\t');
        var display = isAscii
            ? Encoding.UTF8.GetString(data).Replace("\r\n", "\\r\\n").Replace("\r", "\\r").Replace("\n", "\\n")
            : BitConverter.ToString(data).Replace("-", " ");

        // 直接进缓冲区（线程安全），UI 由 _logFlushTimer 节拍刷新
        EnqueueLog($"[RX {timestamp}] {display}\n");
    }

    private void EnqueueLog(string line)
    {
        lock (_logBufferLock)
        {
            _logBuffer.Append(line);
        }
    }

    /// <summary>UI 线程：把缓冲合并进 CommunicationLog 并按上限裁剪。100ms/次。</summary>
    private void FlushLogBuffer()
    {
        string pending;
        lock (_logBufferLock)
        {
            if (_logBuffer.Length == 0) return;
            pending = _logBuffer.ToString();
            _logBuffer.Clear();
        }

        var current = CommunicationLog;
        // 估算合并后长度，超出 MaxLogLength 时只保留尾部
        int total = current.Length + pending.Length;
        string updated = total <= MaxLogLength
            ? current + pending
            : (current + pending)[^MaxLogLength..];

        CommunicationLog = updated;
    }

    private void AppendToLog(string line)
    {
        // 同步 UI 线程调用（错误/状态信息），仍走缓冲以避免与 RX 流竞争抖动
        EnqueueLog(line);
    }

    // ---- 辅助 ----

    private static string? PromptInput(string title, string prompt, string defaultValue)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 380,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(15) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
        var tb = new System.Windows.Controls.TextBox { Text = defaultValue, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(tb);
        var btns = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var ok = new System.Windows.Controls.Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new System.Windows.Controls.Button { Content = "取消", Width = 70, IsCancel = true };
        string? result = null;
        ok.Click += (s, e) => { result = tb.Text; dialog.DialogResult = true; };
        cancel.Click += (s, e) => { dialog.DialogResult = false; };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(btns);
        dialog.Content = panel;
        dialog.ShowDialog();
        return result;
    }
}
