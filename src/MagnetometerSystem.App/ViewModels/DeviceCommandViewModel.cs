using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;
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

    /// <summary>枚举可选项（有值时 UI 出下拉框，否则出文本框）</summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>是否以下拉方式呈现</summary>
    public bool IsChoice => Choices.Count > 0;

    /// <summary>是否以文本框呈现（XAML 里没有 bool 取反，直接给一个反向属性）</summary>
    public bool IsFreeText => !IsChoice;

    public CommandParameterBinding(CommandParameter def)
    {
        Definition = def;

        // 二进制枚举优先；退化到 ASCII 模板用的 EnumOptions
        Choices = def.EnumMap.Count > 0
            ? [.. def.EnumMap.Select(c => c.Label)]
            : (def.Type == CommandParameterType.Enum ? [.. def.EnumOptions] : Array.Empty<string>());

        // 默认值不在候选里时回落到首项，避免下拉空白且发出去的是空值
        Value = IsChoice && !Choices.Contains(def.DefaultValue)
            ? Choices[0]
            : def.DefaultValue;
    }
}

/// <summary>收发记录的方向：发送、接收，或没有字节的说明（超时、发送失败等）。</summary>
public enum TrafficKind { Tx, Rx, Note }

/// <summary>
/// 收发记录的一行。Result 只写有证据的结论：“已写出”只表示本机写出完成，
/// “应答匹配 / ACK / ERR”来自收到的字节，超时表示执行结果未知。Level：ok / warn / err / 空。
/// </summary>
public sealed record TrafficEntry(DateTime Time, TrafficKind Kind, int ByteCount, string Content, string Result, string Level, string? Command)
{
    public string TimeText => Time.ToString("HH:mm:ss.fff");
    public string KindText => Kind switch { TrafficKind.Tx => "发送", TrafficKind.Rx => "接收", _ => "说明" };
    public string SizeText => ByteCount > 0 ? $"{ByteCount} B" : "";
    public override string ToString() =>
        $"{TimeText}\t{KindText}\t{Content}\t{Result}{(Command is null ? "" : "\t" + Command)}";
}

/// <summary>
/// 设备命令 ViewModel — 支持命令目录、命令组、参数化命令，
/// 保留底部"自由发送"区兼容手动输入。
/// </summary>
public partial class DeviceCommandViewModel : ObservableObject, IDisposable
{
    private readonly DataBus _dataBus;
    private readonly IAppConfigService _configService;
    private IDeviceConnection? _connection;
    private volatile bool _disposed;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _responseGate = new();
    private CancellationTokenSource? _responseCts;
    private IDeviceConnection? _pendingConnection;
    private byte[]? _expectedResponse;
    private bool _pendingCtmbs;
    private Ctmbs3X2000Parser? _pendingRealtimeParser;
    private readonly List<byte> _responseBuffer = [];
    private long _responseVersion;
    private long _connectionVersion;
    private long _pendingSentTicks;
    private string? _pendingCommandName;
    private const int MaxTrafficEntries = 500;
    private const int MaxRxContentChars = 240;
    private const string CatalogKey = "device.commandCatalog";

    // ---- 目录 / 组 / 命令 ----
    // Groups = 协议自带的内置组（随协议切换）+ 用户目录组。
    // 两者必须分开持有：SaveCatalogAsync 只能写回 _userGroups，
    // 否则内置组会被烙进用户目录，每次加载协议时重复叠加。
    public ObservableCollection<CommandGroup> Groups { get; } = new();

    /// <summary>用户自定义命令组（持久化到配置）</summary>
    private readonly List<CommandGroup> _userGroups = [];

    /// <summary>当前协议自带的内置命令组（不持久化）</summary>
    private List<CommandGroup> _protocolGroups = [];

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
    private string _freeLineEnding = "CRLF";

    public string[] FreeLineEndings { get; } = ["CRLF", "LF", "CR", "None"];

    partial void OnFreeLineEndingChanged(string value) => FreeAppendNewline = value != "None";
    partial void OnFreeAppendNewlineChanged(bool value)
    {
        if (!value) FreeLineEnding = "None";
        else if (FreeLineEnding == "None") FreeLineEnding = "CRLF";
    }
    partial void OnFreeIsHexModeChanged(bool value) => FreeLineEnding = value ? "None" : "CRLF";

    [ObservableProperty]
    private bool _showFreeSend;

    // ---- 日志 / 状态 ----
    /// <summary>收发记录（最近 500 条），按 100 ms 节拍从缓冲并入。</summary>
    public ObservableCollection<TrafficEntry> TrafficEntries { get; } = new();

    /// <summary>界面来不及刷新、在缓冲中被挤掉的记录数（高速连续数据时可能出现）。</summary>
    [ObservableProperty]
    private long _droppedTrafficEntries;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty] private bool _isSending;
    [ObservableProperty] private string _writeStatus = "尚未发送";
    [ObservableProperty] private string _responseStatus = "尚未等待响应";
    [ObservableProperty] private int _lastSendByteCount;
    [ObservableProperty] private int _responseTimeoutMs = 3000;

    [ObservableProperty]
    private bool _pauseAutoScroll;

    // ---- 日志缓冲（高吞吐下按 100ms 节拍批量 flush，避免每帧都触发 UI 重绘）----
    private readonly Queue<TrafficEntry> _logBuffer = new();
    private readonly object _logBufferLock = new();
    private readonly DispatcherTimer _logFlushTimer;

    public DeviceCommandViewModel(DataBus dataBus, IAppConfigService configService)
    {
        _dataBus = dataBus;
        _configService = configService;
        _connection = dataBus.CurrentConnection;
        IsConnected = _connection?.IsConnected == true;
        if (_connection != null)
        {
            _connection.DataReceived += OnDataReceived;
            _connection.ConnectionStateChanged += OnConnectionStateChanged;
        }

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

        OnUi(() =>
        {
            _userGroups.Clear();
            foreach (var g in catalog.Groups)
            {
                g.IsBuiltIn = false; // 用户目录里的组一律可编辑，防止读入陈旧标志
                _userGroups.Add(g);
            }
            RebuildGroups();
        });
    }

    /// <summary>
    /// 把内置组与用户组合并进 Groups（内置在前），并尽量保住当前选中项。
    /// </summary>
    private void RebuildGroups()
    {
        var previouslySelected = SelectedGroup;

        Groups.Clear();
        foreach (var g in _protocolGroups) Groups.Add(g);
        foreach (var g in _userGroups) Groups.Add(g);

        SelectedGroup = previouslySelected != null && Groups.Contains(previouslySelected)
            ? previouslySelected
            : Groups.FirstOrDefault();
    }

    /// <summary>
    /// 切换协议时替换内置命令组。协议无自带命令时传入空列表即可。
    /// </summary>
    public void SetProtocolCommands(IEnumerable<CommandGroup>? groups)
    {
        _protocolGroups = groups?.ToList() ?? [];
        foreach (var g in _protocolGroups)
            g.IsBuiltIn = true;

        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.Invoke(RebuildGroups);
        else
            RebuildGroups();
    }

    private async Task SaveCatalogAsync()
    {
        // 只持久化用户组：内置组由协议提供，写进去会在下次加载时重复叠加
        var catalog = new CommandCatalog { Groups = [.. _userGroups] };
        try { await _configService.SetAsync(CatalogKey, catalog); }
        catch (Exception ex) { AddNote($"保存命令目录失败：{ex.Message}", "err"); }
    }

    /// <summary>内置组只读，拦下所有会被下次加载覆盖掉的编辑操作</summary>
    private bool RejectIfBuiltIn(CommandGroup? group)
    {
        if (group?.IsBuiltIn != true) return false;
        MessageBox.Show(
            $"'{group.Name}' 是协议自带的内置命令组，不能修改。\n\n"
            + "如需自定义，请新建命令组，或在协议配置中调整。",
            "内置命令组", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    partial void OnSelectedGroupChanged(CommandGroup? value)
    {
        CurrentCommands.Clear();
        if (value != null)
        {
            foreach (var c in value.Commands) CurrentCommands.Add(c);
        }
        SelectedCommand = CurrentCommands.FirstOrDefault();
        OnPropertyChanged(nameof(CanEditSelectedGroup));
        OnPropertyChanged(nameof(SelectedGroupIsBuiltIn));
    }

    /// <summary>选中组是否可编辑（内置组只读；未选中时按钮也应禁用）</summary>
    public bool CanEditSelectedGroup => SelectedGroup is { IsBuiltIn: false };

    /// <summary>选中组是否为协议自带的内置组（用于显示只读提示）</summary>
    public bool SelectedGroupIsBuiltIn => SelectedGroup?.IsBuiltIn == true;

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
            else if (SelectedCommand.Encoding == CommandEncoding.CtmbsRequest)
            {
                var rendered = Ctmbs3X2000FrameBuilder.RenderRequest(SelectedCommand, values);
                PreviewText = rendered + (SelectedCommand.AppendNewline ? "\\n" : "");
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
            WriteStatus = "写出失败: 未连接设备";
            AddNote("未连接设备，命令未发送", "err", SelectedCommand.Name);
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
            else if (SelectedCommand.Encoding == CommandEncoding.CtmbsRequest)
            {
                data = Ctmbs3X2000FrameBuilder.BuildRequestBytes(SelectedCommand, values);
                display = Ctmbs3X2000FrameBuilder.RenderRequest(SelectedCommand, values)
                        + (SelectedCommand.AppendNewline ? "\\n" : "");
            }
            else
            {
                data = CommandFrameBuilder.BuildBinaryFrame(SelectedCommand, values).FullBytes;
                display = CommandFrameBuilder.ToHexString(data);
            }

            await SendFrameAsync(data, display, SelectedCommand, IsCtmbsRealtimeRequest(SelectedCommand, values));
        }
        catch (Exception ex)
        {
            WriteStatus = "写出失败: " + ex.Message;
            AddNote($"发送失败：{ex.Message}", "err", SelectedCommand?.Name);
        }
    }

    [RelayCommand]
    private async Task SendFreeCommandAsync()
    {
        if (_connection == null)
        {
            WriteStatus = "写出失败: 未连接设备";
            AddNote("未连接设备，命令未发送", "err", FreeSendName);
            return;
        }
        if (string.IsNullOrEmpty(FreeCommandText)) return;

        try { await SendRawAsync(FreeCommandText, FreeIsHexMode, FreeAppendNewline); }
        catch (Exception ex) { WriteStatus = "写出失败: " + ex.Message; AddNote($"发送失败：{ex.Message}", "err", FreeSendName); }
    }

    private async Task SendRawAsync(string text, bool isHex, bool appendNewline)
    {
        byte[] data;
        string display;

        if (isHex)
        {
            var hex = Regex.Replace(text, @"[\s\-]", "");
            if (hex.Length == 0)
                throw new ArgumentException("Hex 命令为空");
            if (hex.Length % 2 != 0)
                throw new ArgumentException("Hex 字符串长度不合法");
            try
            {
                data = Enumerable.Range(0, hex.Length / 2)
                    .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                    .ToArray();
            }
            catch (FormatException)
            {
                throw new ArgumentException("Hex 字符串包含非法字符");
            }
            display = BitConverter.ToString(data).Replace("-", " ");
        }
        else
        {
            data = Encoding.UTF8.GetBytes(text);
            display = text;
        }

        string ending = !appendNewline ? "" : FreeLineEnding switch
        {
            "CRLF" => "\r\n", "LF" => "\n", "CR" => "\r", "None" => "",
            _ => throw new ArgumentException("请选择 CRLF、LF、CR 或 None 行尾"),
        };
        if (ending.Length > 0)
        {
            data = [.. data, .. Encoding.ASCII.GetBytes(ending)];
            display += ending.Replace("\r", "\\r").Replace("\n", "\\n");
        }

        await SendFrameAsync(data, display);
    }

    private static bool IsCtmbsRealtimeRequest(DeviceCommand command, IReadOnlyDictionary<string, string> values)
    {
        if (command.Encoding != CommandEncoding.CtmbsRequest || command.Template != "dat") return false;
        var parameters = command.Parameters.Where(p => p.Key != "deviceId").ToArray();
        if (parameters.Length != 1) return false;
        var mode = values.TryGetValue(parameters[0].Key, out var value) && !string.IsNullOrEmpty(value)
            ? value : parameters[0].DefaultValue;
        return mode == "0";
    }

    private async Task SendFrameAsync(byte[] data, string display, DeviceCommand? command = null, bool awaitCtmbsRealtime = false)
    {
        if (command?.RequiresIsolatedTransfer == true)
        {
            const string reason = "此命令需要独立传输，当前尚未实现设备存储下载隔离，暂不可用。";
            WriteStatus = "未发送: " + reason;
            LastSendByteCount = 0;
            AddNote(reason, "err", command.Name);
            return;
        }
        if (data.Length == 0) throw new ArgumentException("命令不能为空");
        int responseTimeoutMs = ResponseTimeoutMs;
        if (responseTimeoutMs <= 0) throw new ArgumentException("响应超时必须为正数");
        bool isCtmbs = command?.Encoding == CommandEncoding.CtmbsRequest;
        byte[]? expected = string.IsNullOrEmpty(command?.ExpectedResponse) ? null
            : command.ExpectedResponseIsHex ? CommandFrameBuilder.ParseHexBytes(command.ExpectedResponse)
            : Encoding.UTF8.GetBytes(Regex.Unescape(command.ExpectedResponse));
        var requestedConnection = _connection;
        var requestedVersion = Interlocked.Read(ref _connectionVersion);
        await _sendGate.WaitAsync();
        IsSending = true;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var connection = _connection;
            if (connection?.IsConnected != true || !ReferenceEquals(requestedConnection, connection)
                || requestedVersion != Interlocked.Read(ref _connectionVersion))
                throw new InvalidOperationException("设备未连接或连接已改变，命令未发送");
            CancellationToken token;
            long version;
            lock (_responseGate)
            {
                version = ++_responseVersion;
                _responseCts?.Cancel();
                _responseCts?.Dispose();
                _responseCts = new CancellationTokenSource();
                token = _responseCts.Token;
                _pendingConnection = connection;
                _expectedResponse = expected;
                _pendingCtmbs = isCtmbs;
                _pendingRealtimeParser = awaitCtmbsRealtime
                    ? new Ctmbs3X2000Parser(ProtocolConfig.CreateCtmbs3X2000()) : null;
                _pendingCommandName = command?.Name ?? FreeSendName;
                _pendingSentTicks = Stopwatch.GetTimestamp();
                _responseBuffer.Clear();
            }
            WriteStatus = "正在写出...";
            ResponseStatus = expected is null && !_pendingCtmbs
                ? "等待接收（未配置响应匹配，不能确认执行结果）" : "等待协议响应";
            // 先登记响应，再写出，避免本机/高速设备应答早于 SendAsync continuation。
            var writeStarted = DateTime.Now;
            await connection.SendAsync(data);
            LastSendByteCount = data.Length;
            WriteStatus = $"已写出 {data.Length} 字节；不代表设备执行成功";
            EnqueueEntry(new TrafficEntry(writeStarted, TrafficKind.Tx, data.Length, display, $"已写出 {data.Length} 字节", "",
                command?.Name ?? FreeSendName));
            // A response without a request ID cannot belong to two outstanding commands.
            // Hold the send gate asynchronously until this wait completes or is canceled.
            await WaitForResponseAsync(token, responseTimeoutMs, version);
        }
        catch
        {
            CancelResponse("写出未完成，设备执行结果未知");
            throw;
        }
        finally { IsSending = false; _sendGate.Release(); }
    }

    private async Task WaitForResponseAsync(CancellationToken token, int timeoutMs, long version)
    {
        try
        {
            await Task.Delay(timeoutMs, token).ConfigureAwait(false);
            string? name;
            int received;
            bool hasExpectation;
            lock (_responseGate)
            {
                if (token.IsCancellationRequested || _pendingConnection is null || version != _responseVersion) return;
                _pendingConnection = null;
                _pendingRealtimeParser = null;
                name = _pendingCommandName;
                received = _responseBuffer.Count;
                hasExpectation = _expectedResponse is { Length: > 0 } || _pendingCtmbs;
            }
            SetResponseStatus(version, "等待响应超时；设备执行结果未知");
            AddNote(received == 0 ? $"{timeoutMs} ms 内未收到任何数据；执行结果未知"
                : hasExpectation ? $"{timeoutMs} ms 内收到 {received} 字节，但没有匹配的应答；执行结果未知"
                : $"未配置预期应答，{timeoutMs} ms 内收到 {received} 字节；不能确认执行结果", "warn", name);
        }
        catch (OperationCanceledException) { }
    }

    private void CancelResponse(string message)
    {
        long version;
        lock (_responseGate)
        {
            version = ++_responseVersion;
            _responseCts?.Cancel();
            _pendingConnection = null;
            _pendingRealtimeParser = null;
            _responseBuffer.Clear();
        }
        SetResponseStatus(version, message);
    }

    private void SetResponseStatus(long version, string message) => OnUi(() =>
    {
        lock (_responseGate)
        {
            if (version == _responseVersion) ResponseStatus = message;
        }
    });

    private static void OnUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(action);
        else action();
    }

    // ---- 目录管理 ----

    [RelayCommand]
    private async Task AddGroupAsync()
    {
        var name = PromptInput("新建命令组", "组名:", "新命令组");
        if (string.IsNullOrWhiteSpace(name)) return;

        var group = new CommandGroup { Name = name };
        _userGroups.Add(group);
        RebuildGroups();
        SelectedGroup = group;
        await SaveCatalogAsync();
    }

    [RelayCommand]
    private async Task RenameGroupAsync(CommandGroup? group)
    {
        group ??= SelectedGroup;
        if (group == null || RejectIfBuiltIn(group)) return;

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
        if (group == null || RejectIfBuiltIn(group)) return;

        var result = MessageBox.Show(
            $"确定删除命令组 '{group.Name}' 及其 {group.Commands.Count} 条命令？",
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _userGroups.Remove(group);
        RebuildGroups();
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
        if (RejectIfBuiltIn(SelectedGroup)) return;

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
        if (cmd == null || RejectIfBuiltIn(SelectedGroup)) return;

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
        if (cmd == null || SelectedGroup == null || RejectIfBuiltIn(SelectedGroup)) return;

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

            // 导入只替换用户组；协议自带的内置组不受影响
            _userGroups.Clear();
            foreach (var g in catalog.Groups)
            {
                g.IsBuiltIn = false;
                _userGroups.Add(g);
            }
            RebuildGroups();
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
            // 导出用户组即可；内置组随协议走，导出后再导入会与协议自带的重复
            var catalog = new CommandCatalog { Groups = [.. _userGroups] };
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
    private void ClearLog()
    {
        lock (_logBufferLock) _logBuffer.Clear();
        TrafficEntries.Clear();
    }

    // ---- 连接 / 接收 ----

    private void OnConnectionChanged(IDeviceConnection? connection)
    {
        Interlocked.Increment(ref _connectionVersion);
        if (_connection != null)
        {
            _connection.DataReceived -= OnDataReceived;
            _connection.ConnectionStateChanged -= OnConnectionStateChanged;
        }

        _connection = connection;
        OnUi(() => { if (ReferenceEquals(connection, _connection)) IsConnected = connection?.IsConnected == true; });
        CancelResponse(connection is null ? "连接已结束；未确认的命令执行结果未知" : "等待发送命令");

        if (_connection != null)
        {
            _connection.DataReceived += OnDataReceived;
            _connection.ConnectionStateChanged += OnConnectionStateChanged;
        }
    }

    private void OnConnectionStateChanged(object? sender, bool connected)
    {
        if (!ReferenceEquals(sender, _connection)) return;
        Interlocked.Increment(ref _connectionVersion);
        OnUi(() => { if (ReferenceEquals(sender, _connection)) IsConnected = _connection?.IsConnected == true; });
        if (!connected) CancelResponse("连接中断；设备执行结果未知");
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        if (!ReferenceEquals(sender, _connection)) return;
        var now = DateTime.Now;
        bool isAscii = data.All(b => (b >= 0x20 && b <= 0x7E) || b == '\r' || b == '\n' || b == '\t');
        var display = isAscii
            ? Encoding.UTF8.GetString(data).Replace("\r\n", "\\r\\n").Replace("\r", "\\r").Replace("\n", "\\n")
            : BitConverter.ToString(data).Replace("-", " ");
        if (display.Length > MaxRxContentChars) display = display[..MaxRxContentChars] + " …";

        string result = "", level = "";
        string? command = null;
        lock (_responseGate)
        {
            if (ReferenceEquals(sender, _pendingConnection))
            {
                _responseBuffer.AddRange(data);
                if (_responseBuffer.Count > 65536) _responseBuffer.RemoveRange(0, _responseBuffer.Count - 65536);
                var received = _responseBuffer.ToArray().AsSpan();
                // CTMBS also pushes $err when no realtime data is available. Without
                // a request identifier it cannot be evidence that this command failed.
                var ambiguousError = _pendingCtmbs && received.IndexOf("$err\n"u8) >= 0;
                var acknowledged = _pendingCtmbs && received.IndexOf("$ack\n"u8) >= 0;
                var matched = _expectedResponse is { Length: > 0 } && received.IndexOf(_expectedResponse) >= 0;
                bool realtimeReceived = false;
                if (_pendingRealtimeParser is { } realtimeParser)
                {
                    realtimeParser.Feed(data, 0, data.Length);
                    realtimeReceived = realtimeParser.TryParse(out _);
                }
                command = _pendingCommandName;
                if (acknowledged || matched || realtimeReceived)
                {
                    _pendingConnection = null;
                    _pendingRealtimeParser = null;
                    _responseCts?.Cancel();
                    var message = realtimeReceived ? "已收到实时帧；推送已到达，不能据此归因于本次命令"
                        : acknowledged ? "收到设备 ACK；执行结果以设备协议为准"
                        : "收到匹配响应；执行结果以设备协议为准";
                    SetResponseStatus(_responseVersion, message);
                    var ms = Stopwatch.GetElapsedTime(_pendingSentTicks).TotalMilliseconds;
                    (result, level) = realtimeReceived ? ($"收到实时帧，{ms:0} ms（不能归因于本次命令）", "")
                        : acknowledged ? ($"设备返回 ACK，{ms:0} ms", "ok")
                        : ($"应答匹配，{ms:0} ms", "ok");
                }
                else if (ambiguousError)
                {
                    SetResponseStatus(_responseVersion, "收到 CTMBS ERR（也可能为无数据推送）；继续等待命令响应，执行结果未确认");
                    (result, level) = ("收到 ERR（也可能是无数据推送），继续等待", "warn");
                }
                else if (_responseBuffer.Count == data.Length)
                {
                    SetResponseStatus(_responseVersion, "已收到数据，尚未匹配命令响应；执行结果未确认");
                    result = _expectedResponse is { Length: > 0 } || _pendingCtmbs ? "尚未匹配应答" : "未配置预期应答";
                }
            }
        }
        // 直接进缓冲区（线程安全），UI 由 _logFlushTimer 节拍刷新
        EnqueueEntry(new TrafficEntry(now, TrafficKind.Rx, data.Length, display, result, level, command));
    }

    private const string FreeSendName = "自由发送";

    /// <summary>任意线程：记录进缓冲，超过上限时挤掉最旧的并计数。</summary>
    private void EnqueueEntry(TrafficEntry entry)
    {
        lock (_logBufferLock)
        {
            _logBuffer.Enqueue(entry);
            while (_logBuffer.Count > MaxTrafficEntries)
            {
                _logBuffer.Dequeue();
                _droppedPending++;
            }
        }
    }

    private long _droppedPending;

    /// <summary>没有字节的说明行：发送失败、超时等。</summary>
    private void AddNote(string result, string level, string? command = null) =>
        EnqueueEntry(new TrafficEntry(DateTime.Now, TrafficKind.Note, 0, "—", result, level, command));

    /// <summary>UI 线程：把缓冲并入 TrafficEntries 并按上限裁剪。100ms/次。</summary>
    private void FlushLogBuffer()
    {
        TrafficEntry[] pending;
        long dropped;
        lock (_logBufferLock)
        {
            if (_logBuffer.Count == 0) return;
            // 快速设备的应答可能先于发送行入队；按时间排序（发送行用写出开始的时间）。
            pending = _logBuffer.OrderBy(e => e.Time).ToArray();
            _logBuffer.Clear();
            dropped = _droppedPending;
        }
        foreach (var entry in pending) TrafficEntries.Add(entry);
        while (TrafficEntries.Count > MaxTrafficEntries) TrafficEntries.RemoveAt(0);
        DroppedTrafficEntries = dropped;
    }

    /// <summary>把当前收发记录复制为制表符分隔的文本。</summary>
    [RelayCommand]
    private void CopyTraffic()
    {
        FlushLogBuffer();
        if (TrafficEntries.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, TrafficEntries)); }
        catch (Exception ex) { AddNote($"复制失败：{ex.Message}", "err"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _connectionVersion);
        _logFlushTimer.Stop();
        _dataBus.ConnectionChanged -= OnConnectionChanged;
        if (_connection != null)
        {
            _connection.DataReceived -= OnDataReceived;
            _connection.ConnectionStateChanged -= OnConnectionStateChanged;
        }
        lock (_responseGate)
        {
            ++_responseVersion;
            _responseCts?.Cancel();
            _responseCts?.Dispose();
            _responseCts = null;
            _pendingConnection = null;
            _pendingRealtimeParser = null;
        }
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
