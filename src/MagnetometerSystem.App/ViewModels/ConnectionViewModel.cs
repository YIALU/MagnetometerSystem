using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;
using MagnetometerSystem.Core.Sensors;
using MagnetometerSystem.Core.Services;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 连接配置 ViewModel
/// </summary>
public partial class ConnectionViewModel : ObservableObject
{
    private readonly IConnectionFactory _connectionFactory;
    private readonly DataBus _dataBus;
    private readonly OrthogonalityCorrector _orthogonalityCorrector;
    private readonly ICalibrationRepository _calibrationRepository;
    private IDeviceConnection? _connection;
    private IDataParser? _parser;
    private ISensorAdapter? _sensorAdapter;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly object _receiveGate = new();
    private readonly Queue<string> _rawDisplayQueue = new();
    private int _receiveUiPending;
    private long _receivedBytes, _parsedCount, _parseErrors;
    private long _lastParserRejectedCount;
    private bool _sessionPrepared;
    private string? _preparedSessionId;
    private int _rejectIncomingData;
    private int _faultStopRequested;
    private long _acquisitionGeneration;
    private string[] _activeChannelUnits = [];
    private string? _lastReportedError;

    // ---- 传感器配置 ----

    [ObservableProperty]
    private SensorType _selectedSensorType = SensorType.Generic;

    public SensorType[] SensorTypes { get; } = Enum.GetValues<SensorType>();

    [ObservableProperty]
    private double _sampleRate = 10.0;

    public double[] PresetSampleRates { get; } = SensorConfig.PresetSampleRates;

    // ---- 协议配置 ----

    [ObservableProperty]
    private ProtocolConfig _protocolConfig = ProtocolConfig.CreateDefaultAsciiTriaxial();

    [ObservableProperty]
    private ObservableCollection<ProtocolConfig> _savedProtocols = new();

    [ObservableProperty]
    private ProtocolConfig? _selectedSavedProtocol;

    public ProtocolCategory[] ProtocolCategories { get; } = Enum.GetValues<ProtocolCategory>();

    public ChecksumType[] ChecksumTypes { get; } = Enum.GetValues<ChecksumType>();

    public FieldDataType[] FieldDataTypes { get; } = Enum.GetValues<FieldDataType>();

    public SegmentType[] AvailableSegmentTypes { get; } =
        [SegmentType.Header, SegmentType.LengthField, SegmentType.DataField,
         SegmentType.Checksum, SegmentType.Tail, SegmentType.Padding];

    public ChecksumAlgorithm[] ChecksumAlgorithms { get; } = Enum.GetValues<ChecksumAlgorithm>();

    [ObservableProperty]
    private ObservableCollection<FrameSegment> _protocolSegments = new();

    // ---- 连接类型 ----

    [ObservableProperty]
    private ConnectionType _selectedConnectionType = ConnectionType.Serial;

    public ConnectionType[] ConnectionTypes { get; } = Enum.GetValues<ConnectionType>();

    // ---- 串口参数 ----

    [ObservableProperty]
    private ObservableCollection<string> _availablePorts = new();

    [ObservableProperty]
    private string _selectedPort = "COM1";

    [ObservableProperty]
    private int _baudRate = 115200;

    public int[] BaudRates { get; } = [9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    [ObservableProperty]
    private int _dataBits = 8;

    public int[] DataBitsOptions { get; } = [7, 8];

    [ObservableProperty]
    private string _parity = "None";

    public string[] Parities { get; } = ["None", "Odd", "Even"];

    [ObservableProperty]
    private double _stopBits = 1.0;

    public double[] StopBitsOptions { get; } = [1.0, 1.5, 2.0];

    // ---- TCP 参数 ----

    [ObservableProperty]
    private string _ipAddress = "192.168.1.100";

    [ObservableProperty]
    private int _port = 5000;

    // ---- 状态 ----

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private bool _isAcquiring;
    [ObservableProperty] private long _receivedByteCount;
    [ObservableProperty] private long _parsedReadingCount;
    [ObservableProperty] private long _parseErrorCount;
    [ObservableProperty] private string _lastError = "";

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private ObservableCollection<string> _rawDataLines = new();

    [ObservableProperty]
    private bool _showHex;

    // ---- 正交度校正 ----

    [ObservableProperty]
    private bool _isOrthogonalityCorrectionEnabled;

    [ObservableProperty]
    private ObservableCollection<OrthogonalityParams> _availableOrthogonalityProfiles = new();

    /// <summary>当前活动的正交度校正配置（第一组三轴）</summary>
    [ObservableProperty]
    private OrthogonalityParams? _activeOrthogonalityProfile;

    /// <summary>第二组三轴的正交度校正配置（仅双三轴传感器使用）</summary>
    [ObservableProperty]
    private OrthogonalityParams? _secondOrthogonalityProfile;

    [ObservableProperty] private string _firstOrthogonalityChannelsText = "";
    [ObservableProperty] private string _secondOrthogonalityChannelsText = "";

    private const int MaxRawDataLines = 200;

    private static readonly string ProtocolConfigDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Protocols");

    public ConnectionViewModel(IConnectionFactory connectionFactory, DataBus dataBus,
        OrthogonalityCorrector orthogonalityCorrector, ICalibrationRepository calibrationRepository)
    {
        _connectionFactory = connectionFactory;
        _dataBus = dataBus;
        _orthogonalityCorrector = orthogonalityCorrector;
        _calibrationRepository = calibrationRepository;
        _dataBus.AcquisitionFaulted += OnAcquisitionFaulted;
        _dataBus.SessionStarted += sessionId =>
        {
            if (_sessionPrepared && _connection != null) _preparedSessionId = sessionId;
        };
        _dataBus.AcquisitionRecoveryCompleted += OnAcquisitionRecoveryCompletedAsync;

        // 监听段列表变化，订阅每个段的 PropertyChanged
        ProtocolSegments.CollectionChanged += (s, e) =>
        {
            if (e.NewItems != null)
                foreach (FrameSegment seg in e.NewItems)
                    seg.PropertyChanged += OnSegmentPropertyChanged;
            if (e.OldItems != null)
                foreach (FrameSegment seg in e.OldItems)
                    seg.PropertyChanged -= OnSegmentPropertyChanged;
        };

        RefreshPorts();
        // 协议配置和正交度配置延迟加载（构造函数只做最小化初始化）
    }

    private bool _isLoaded;
    public async Task EnsureLoadedAsync()
    {
        if (_isLoaded) return;
        _isLoaded = true;
        LoadSavedProtocols();
        await LoadOrthogonalityProfilesAsync();
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        AvailablePorts.Clear();
        foreach (var port in SerialPort.GetPortNames())
        {
            AvailablePorts.Add(port);
        }
        if (AvailablePorts.Count > 0 && !AvailablePorts.Contains(SelectedPort))
        {
            SelectedPort = AvailablePorts[0];
        }
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        await _connectionGate.WaitAsync();
        IsConnecting = true;
        try
        {
            // 包含 TCP 自动重连期间，避免把“当前断线”误判成可以创建第二个采集会话。
            if (_connection != null || _sessionPrepared)
            {
                await DisconnectCoreAsync();
                return;
            }
            var protocol = ProtocolConfig.FromJson(ProtocolConfig.ToJson())
                ?? throw new InvalidOperationException("协议配置无效");
            protocol.Validate();
            var sensorConfig = new SensorConfig
            {
                Type = SensorType.Generic,
                SampleRate = SampleRate,
                ChannelCountOverride = protocol.DerivedChannelCount,
                ChannelNamesOverride = protocol.DerivedChannelNames.ToArray(),
                ChannelUnitsOverride = protocol.DerivedChannelUnits.ToArray(),
                ProtocolType = protocol.Name,
            };
            if (!sensorConfig.ValidateSampleRate())
                throw new ArgumentException("标称采样率必须是有限正数；实际节奏由设备决定");
            var connConfig = new ConnectionConfig
            {
                Type = SelectedConnectionType, PortName = SelectedPort, BaudRate = BaudRate,
                DataBits = DataBits, Parity = Parity, StopBits = StopBits,
                IpAddress = IpAddress, Port = Port,
            };
            var connection = _connectionFactory.Create(connConfig);
            lock (_receiveGate)
            {
                _parser = ParserFactory.Create(protocol);
                _sensorAdapter = SensorAdapterFactory.Create(sensorConfig);
                _activeChannelUnits = sensorConfig.ChannelUnits.ToArray();
                Interlocked.Increment(ref _acquisitionGeneration);
                _connection = connection;
                Volatile.Write(ref _rejectIncomingData, 0);
                Interlocked.Exchange(ref _faultStopRequested, 0);
                _receivedBytes = _parsedCount = _parseErrors = _lastParserRejectedCount = 0;
            }
            ReceivedByteCount = ParsedReadingCount = ParseErrorCount = 0;
            LastError = "";
            _lastReportedError = null;
            ShowHex = protocol.Category == ProtocolCategory.Binary;
            connection.DataReceived += OnDataReceived;
            connection.ErrorOccurred += OnErrorOccurred;
            connection.ConnectionStateChanged += OnConnectionStateChanged;
            StatusMessage = "正在准备采集会话...";
            _sessionPrepared = true;
            await _dataBus.PublishAcquisitionStartingAsync(sensorConfig, connConfig);
            IsAcquiring = true;
            // 图表和保存消费者均在首帧前初始化。
            _dataBus.PublishAcquisitionStarted(sensorConfig);
            _dataBus.PublishConnectionChanged(connection);
            await connection.ConnectAsync();
            IsConnected = connection.IsConnected;
            StatusMessage = "已连接 · 原始数据自动保存";
        }
        catch (Exception ex)
        {
            var message = ex.Message;
            try { await DisconnectCoreAsync(); }
            catch (Exception cleanup) { message += $"；结束会话失败: {cleanup.Message}"; }
            ReportError("连接失败: " + message);
        }
        finally { IsConnecting = false; _connectionGate.Release(); }
    }

    public async Task StopAcquisitionAsync()
    {
        await _connectionGate.WaitAsync();
        IsConnecting = true;
        try { await DisconnectCoreAsync(); }
        finally { IsConnecting = false; _connectionGate.Release(); }
    }

    private void OnAcquisitionFaulted(Exception error)
    {
        var failedConnection = _connection;
        long generation = Volatile.Read(ref _acquisitionGeneration);
        if (failedConnection == null && !_sessionPrepared) return;
        // Do not acquire _receiveGate here: a receive callback may currently be
        // publishing to storage. Every next frame checks this gate without UI work.
        Volatile.Write(ref _rejectIncomingData, 1);
        if (Interlocked.Exchange(ref _faultStopRequested, 1) != 0) return;
        ReportError("保存失败，采集已停止接收；已接收数据保留待重试: " + error.Message, generation);
        _ = Task.Run(() => StopFaultedAcquisitionAsync(failedConnection, generation));
    }

    private async Task StopFaultedAcquisitionAsync(IDeviceConnection? failedConnection, long generation)
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A delayed fault task must never stop a newer connection/session.
            if (generation != Volatile.Read(ref _acquisitionGeneration)
                || !ReferenceEquals(failedConnection, _connection)) return;
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _acquisitionGeneration))
                ReportError("采集已停止，未保存数据仍待重试: " + ex.Message, generation);
        }
        finally { _connectionGate.Release(); }
    }

    private async Task OnAcquisitionRecoveryCompletedAsync(string sessionId)
    {
        long generation = Volatile.Read(ref _acquisitionGeneration);
        await _connectionGate.WaitAsync();
        try
        {
            if (generation != Volatile.Read(ref _acquisitionGeneration)
                || !_sessionPrepared || sessionId != _preparedSessionId) return;
            // Recovery releases the session lifecycle gate before awaiting this gate.
            // A fault cleanup may still be waiting to disconnect the transport.
            if (_connection == null) CompleteAcquisitionStop();
            else if (Volatile.Read(ref _rejectIncomingData) != 0) await DisconnectCoreAsync();
        }
        finally { _connectionGate.Release(); }
    }

    private void CompleteAcquisitionStop()
    {
        bool notifyStopped = _sessionPrepared;
        _sessionPrepared = false;
        _preparedSessionId = null;
        IsAcquiring = false;
        if (notifyStopped && Volatile.Read(ref _faultStopRequested) != 0)
        {
            LastError = "";
            _lastReportedError = null;
        }
        StatusMessage = "已断开 · 会话已保存";
        if (notifyStopped) _dataBus.PublishAcquisitionStopped();
    }

    private async Task DisconnectCoreAsync()
    {
        var connection = _connection;
        if (connection != null)
        {
            // 先停止物理接收并等待其当前回调，最后才结束存储会话。
            await connection.DisconnectAsync();
            lock (_receiveGate)
            {
                connection.DataReceived -= OnDataReceived;
                connection.ErrorOccurred -= OnErrorOccurred;
                connection.ConnectionStateChanged -= OnConnectionStateChanged;
                _connection = null;
                _parser?.Reset();
                _parser = null;
                _sensorAdapter = null;
            }
            _dataBus.PublishConnectionChanged(null);
            await connection.DisposeAsync();
        }
        IsConnected = false;
        if (_sessionPrepared)
        {
            StatusMessage = "正在保存尾批数据...";
            await _dataBus.PublishAcquisitionStoppingAsync();
        }
        CompleteAcquisitionStop();
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        lock (_receiveGate)
        {
            if (Volatile.Read(ref _rejectIncomingData) != 0 || !ReferenceEquals(sender, _connection) || _parser == null) return;
            _receivedBytes += data.Length;
            _rawDisplayQueue.Enqueue(ShowHex ? BitConverter.ToString(data).Replace("-", " ") : System.Text.Encoding.ASCII.GetString(data));
            while (_rawDisplayQueue.Count > MaxRawDataLines) _rawDisplayQueue.Dequeue();
            try
            {
                _parser.Feed(data, 0, data.Length);
                while (Volatile.Read(ref _rejectIncomingData) == 0 && _parser.TryParse(out var reading))
                {
                    if (reading is null) continue;
                    var raw = _sensorAdapter?.Process(reading) ?? reading;
                    // Count and display only after the storage consumer has accepted ownership.
                    // The fault can close acceptance after TryParse succeeds but before this call.
                    if (!_dataBus.TryPublishAcquisitionReading(raw)) break;
                    _parsedCount++;
                    // 原始值已被独立保存消费者接纳；任何可选改正失败均不能中断保存。
                    var display = raw.DeepClone();
                    if (IsOrthogonalityCorrectionEnabled)
                    {
                        try
                        {
                            if (ActiveOrthogonalityProfile is null)
                                throw new ArgumentException("请选择第一组改正参数");
                            var first = ParseChannelSelection(FirstOrthogonalityChannelsText, raw.ChannelValues.Length);
                            var second = SecondOrthogonalityProfile is null ? null
                                : ParseChannelSelection(SecondOrthogonalityChannelsText, raw.ChannelValues.Length);
                            ValidateCorrectionUnits(first, ActiveOrthogonalityProfile);
                            if (second != null) ValidateCorrectionUnits(second, SecondOrthogonalityProfile!);
                            display = _orthogonalityCorrector.ApplyToReading(
                                ActiveOrthogonalityProfile, SecondOrthogonalityProfile, raw.DeepClone(), first, second);
                        }
                        catch (Exception ex) { ReportError("改正未应用，原始数据已保留: " + ex.Message); }
                    }
                    // 默认采集和改正失败也必须绘图；显示只收到与原始流独立的快照。
                    _dataBus.PublishProcessedReading(display);
                }
                if (_parser is IParserDiagnostics diagnostics)
                {
                    var rejected = diagnostics.RejectedFrameCount - _lastParserRejectedCount;
                    _parseErrors += rejected;
                    _lastParserRejectedCount = diagnostics.RejectedFrameCount;
                    if (rejected > 0 && diagnostics.LastError is { } error) ReportError(error);
                }
            }
            catch (Exception ex)
            {
                _parseErrors++;
                _parser.Reset();
                ReportError("接收解析失败，已重置解析器: " + ex.Message);
            }
            if (Interlocked.Exchange(ref _receiveUiPending, 1) == 0) OnUi(FlushReceiveStatus);
        }
    }

    private void FlushReceiveStatus()
    {
        lock (_receiveGate)
        {
            while (_rawDisplayQueue.Count > 0) RawDataLines.Add(_rawDisplayQueue.Dequeue());
            while (RawDataLines.Count > MaxRawDataLines) RawDataLines.RemoveAt(0);
            ReceivedByteCount = _receivedBytes;
            ParsedReadingCount = _parsedCount;
            ParseErrorCount = _parseErrors;
            Interlocked.Exchange(ref _receiveUiPending, 0);
        }
    }

    private static int[] ParseChannelSelection(string text, int count)
    {
        var parts = text.Split([',', '，', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts.Any(p => !int.TryParse(p, out _)))
            throw new ArgumentException("请明确填写三个通道索引，例如 0,1,2");
        var indices = parts.Select(int.Parse).ToArray();
        if (indices.Distinct().Count() != 3 || indices.Any(i => i < 0 || i >= count))
            throw new ArgumentException("改正通道索引必须唯一且在当前通道范围内");
        return indices;
    }

    private void ValidateCorrectionUnits(int[] channels, OrthogonalityParams profile)
    {
        var units = channels.Select(i => OrthogonalityParams.CanonicalUnit(
            i < _activeChannelUnits.Length ? _activeChannelUnits[i] : "")).ToArray();
        if (units.Distinct().Count() != 1 || string.IsNullOrEmpty(units[0]))
            throw new ArgumentException("改正的三个通道必须具有相同磁场单位，不能包含温度或其他辅助通道");
        profile.ValidateUnit(units[0]);
    }

    private void OnErrorOccurred(object? sender, string message)
    {
        if (ReferenceEquals(sender, _connection)) ReportError(message);
    }

    private void OnConnectionStateChanged(object? sender, bool connected)
    {
        if (!connected && ReferenceEquals(sender, _connection))
            lock (_receiveGate) _parser?.Reset();
        OnUi(() =>
        {
            if (!ReferenceEquals(sender, _connection)) return;
            IsConnected = connected;
            StatusMessage = connected ? "已连接 · 原始数据自动保存" : "连接中断 · 停止后可结束当前会话";
        });
    }

    private void ReportError(string message, long? faultGeneration = null)
    {
        long generation = faultGeneration ?? Volatile.Read(ref _acquisitionGeneration);
        if (generation != Volatile.Read(ref _acquisitionGeneration)
            || (faultGeneration.HasValue && !_sessionPrepared)) return;
        if (Interlocked.Exchange(ref _lastReportedError, message) == message) return;
        System.Diagnostics.Trace.TraceError(message);
        OnUi(() =>
        {
            if (generation != Volatile.Read(ref _acquisitionGeneration)
                || (faultGeneration.HasValue && !_sessionPrepared)) return;
            LastError = message;
            StatusMessage = message;
        });
    }

    private static void OnUi(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(action);
        else action();
    }

    // ---- 协议配置管理 ----

    [RelayCommand]
    private void SaveProtocol()
    {
        try
        {
            ProtocolConfig.Validate();
            Directory.CreateDirectory(ProtocolConfigDir);

            // 过滤非法文件名字符，防止路径穿越
            var invalidChars = Path.GetInvalidFileNameChars();
            var safeName = new string(
                ProtocolConfig.Name.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray());
            var shortId = ProtocolConfig.Id?.Length >= 8 ? ProtocolConfig.Id[..8] : "00000000";
            var fileName = $"{safeName}_{shortId}.json";
            var filePath = Path.GetFullPath(Path.Combine(ProtocolConfigDir, fileName));

            // 验证路径在目标目录内
            var configDirFull = Path.GetFullPath(ProtocolConfigDir);
            if (!filePath.StartsWith(configDirFull + Path.DirectorySeparatorChar)
                && !filePath.StartsWith(configDirFull + Path.AltDirectorySeparatorChar))
            {
                StatusMessage = "保存失败: 协议名称包含非法字符";
                return;
            }

            File.WriteAllText(filePath, ProtocolConfig.ToJson());
            StatusMessage = $"协议配置已保存: {ProtocolConfig.Name}";
            LoadSavedProtocols();
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存协议配置失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void LoadSelectedProtocol()
    {
        if (SelectedSavedProtocol != null)
        {
            ProtocolConfig = ProtocolConfig.FromJson(SelectedSavedProtocol.ToJson())!;
            SyncSegmentsFromConfig();
            StatusMessage = $"已加载协议: {ProtocolConfig.Name}";
        }
    }

    [RelayCommand]
    private void AddFieldMapping()
    {
        var nextIndex = ProtocolConfig.FieldMappings.Count;
        ProtocolConfig.FieldMappings.Add(new FieldMapping
        {
            Name = $"CH{nextIndex}",
            ChannelIndex = nextIndex,
            ByteOffset = ProtocolConfig.Category == ProtocolCategory.Ascii
                ? nextIndex
                : nextIndex * 8,
            DataType = FieldDataType.Double,
        });
        OnPropertyChanged(nameof(ProtocolConfig));
    }

    [RelayCommand]
    private void RemoveFieldMapping(FieldMapping? field)
    {
        if (field != null)
        {
            ProtocolConfig.FieldMappings.Remove(field);
            int index = 0;
            foreach (var mapping in ProtocolConfig.FieldMappings.OrderBy(f => f.ChannelIndex)) mapping.ChannelIndex = index++;
            OnPropertyChanged(nameof(ProtocolConfig));
        }
    }

    // ---- 帧段操作 ----

    [RelayCommand]
    private void AddSegment(SegmentType type)
    {
        var seg = type switch
        {
            SegmentType.Header => new FrameSegment { Type = type, Name = "帧头", ByteCount = 2, FixedHexValue = "AA55" },
            SegmentType.Tail => new FrameSegment { Type = type, Name = "帧尾", ByteCount = 1, FixedHexValue = "0D" },
            SegmentType.LengthField => new FrameSegment { Type = type, Name = "长度", ByteCount = 1 },
            SegmentType.Checksum => new FrameSegment { Type = type, Name = "校验", ByteCount = 1 },
            SegmentType.Padding => new FrameSegment { Type = type, Name = "填充", ByteCount = 1, FixedHexValue = "00" },
            SegmentType.DataField => new FrameSegment
            {
                Type = type,
                Name = $"CH{ProtocolSegments.Count(s => s.Type == SegmentType.DataField)}",
                ByteCount = 4,
                DataType = FieldDataType.Float,
                ChannelIndex = ProtocolSegments.Count(s => s.Type == SegmentType.DataField),
            },
            _ => new FrameSegment { Type = type, Name = "未知", ByteCount = 1 },
        };

        ProtocolSegments.Add(seg);
        SyncSegmentsToConfig();
    }

    [RelayCommand]
    private void RemoveSegment(FrameSegment? seg)
    {
        if (seg != null)
        {
            ProtocolSegments.Remove(seg);
            int index = 0;
            foreach (var field in ProtocolSegments.Where(s => s.Type == SegmentType.DataField).OrderBy(s => s.ChannelIndex)) field.ChannelIndex = index++;
            SyncSegmentsToConfig();
        }
    }

    [RelayCommand]
    private void MoveSegmentUp(FrameSegment? seg)
    {
        if (seg == null) return;
        int idx = ProtocolSegments.IndexOf(seg);
        if (idx > 0)
        {
            ProtocolSegments.Move(idx, idx - 1);
            SyncSegmentsToConfig();
        }
    }

    [RelayCommand]
    private void MoveSegmentDown(FrameSegment? seg)
    {
        if (seg == null) return;
        int idx = ProtocolSegments.IndexOf(seg);
        if (idx >= 0 && idx < ProtocolSegments.Count - 1)
        {
            ProtocolSegments.Move(idx, idx + 1);
            SyncSegmentsToConfig();
        }
    }

    /// <summary>将 ObservableCollection 同步回 ProtocolConfig.Segments 并重算偏移</summary>
    public void SyncSegmentsToConfig()
    {
        ProtocolConfig.Segments = [.. ProtocolSegments];
        ProtocolConfig.ComputeSegmentOffsets();
        // 同步回来以更新 ComputedOffset
        for (int i = 0; i < ProtocolSegments.Count && i < ProtocolConfig.Segments.Count; i++)
        {
            ProtocolSegments[i].ComputedOffset = ProtocolConfig.Segments[i].ComputedOffset;
        }
        OnPropertyChanged(nameof(ProtocolConfig));
        OnPropertyChanged(nameof(TotalFrameLength));
    }

    /// <summary>总帧长度（供 UI 显示）</summary>
    public int TotalFrameLength => ProtocolSegments.Sum(s => s.ByteCount);

    /// <summary>从 ProtocolConfig.Segments 同步到 ObservableCollection</summary>
    private void SyncSegmentsFromConfig()
    {
        ProtocolSegments.Clear();
        foreach (var seg in ProtocolConfig.Segments)
        {
            ProtocolSegments.Add(seg);
        }
        OnPropertyChanged(nameof(TotalFrameLength));
    }

    private void OnSegmentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FrameSegment.ByteCount))
        {
            SyncSegmentsToConfig();
        }
    }

    [RelayCommand]
    private void ClearRawData()
    {
        RawDataLines.Clear();
    }

    [RelayCommand]
    private void ExportProtocol()
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出协议配置",
                Filter = "JSON 文件 (*.json)|*.json",
                FileName = $"{ProtocolConfig.Name}.json",
            };

            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, ProtocolConfig.ToJson());
                StatusMessage = $"协议已导出: {dialog.FileName}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ImportProtocol()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "导入协议配置",
                Filter = "JSON 文件 (*.json)|*.json",
            };

            if (dialog.ShowDialog() == true)
            {
                var json = File.ReadAllText(dialog.FileName);
                if (json.Length > 1_000_000)
                {
                    StatusMessage = "导入失败: 文件过大";
                    return;
                }

                var config = ProtocolConfig.FromJson(json);
                if (config != null)
                {
                    ProtocolConfig = config;
                    SyncSegmentsFromConfig();
                    StatusMessage = $"已导入协议: {config.Name}";
                }
                else
                {
                    StatusMessage = "导入失败: 无效的协议配置文件";
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadOrthogonalityProfilesAsync()
    {
        try
        {
            var profiles = await _calibrationRepository.GetOrthogonalityProfilesAsync();
            AvailableOrthogonalityProfiles.Clear();
            foreach (var profile in profiles)
            {
                AvailableOrthogonalityProfiles.Add(profile);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载正交度配置失败: {ex.Message}";
        }
    }

    private void LoadSavedProtocols()
    {
        SavedProtocols.Clear();

        // 添加内置预设
        SavedProtocols.Add(ProtocolConfig.CreateDefaultAsciiTriaxial());
        SavedProtocols.Add(ProtocolConfig.CreateDefaultAsciiDualTriaxial());
        SavedProtocols.Add(ProtocolConfig.CreateDefaultBinaryTriaxial());
        SavedProtocols.Add(ProtocolConfig.CreateDefaultBinaryTriaxialSegments());
        SavedProtocols.Add(ProtocolConfig.CreateCct5Gradiometer());
        SavedProtocols.Add(ProtocolConfig.CreateZdzC08());
        SavedProtocols.Add(ProtocolConfig.CreateZdzC08MagneticOnly());
        SavedProtocols.Add(ProtocolConfig.CreateCtmbs3X2000());

        // 从文件加载
        if (Directory.Exists(ProtocolConfigDir))
        {
            foreach (var file in Directory.GetFiles(ProtocolConfigDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var config = ProtocolConfig.FromJson(json);
                    if (config != null)
                        SavedProtocols.Add(config);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning($"跳过损坏的协议配置文件 {file}: {ex.Message}");
                }
            }
        }
    }
}
