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

    // ---- 传感器配置 ----

    [ObservableProperty]
    private SensorType _selectedSensorType = SensorType.TriaxialFluxgate;

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
        LoadSavedProtocols();
        _ = LoadOrthogonalityProfilesAsync();
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
        if (IsConnected)
        {
            await DisconnectAsync();
            return;
        }

        try
        {
            var sensorConfig = new SensorConfig
            {
                Type = SelectedSensorType,
                SampleRate = SampleRate,
            };

            // 从协议配置中提取通道信息，覆盖传感器类型的默认值
            if (ProtocolConfig.UsesSegments)
            {
                var dataFields = ProtocolConfig.Segments
                    .Where(s => s.Type == SegmentType.DataField)
                    .OrderBy(s => s.ComputedOffset)
                    .ToList();
                if (dataFields.Count > 0)
                {
                    sensorConfig.ChannelCountOverride = dataFields.Count;
                    sensorConfig.ChannelNamesOverride = dataFields.Select(f => f.Name).ToArray();
                }
            }

            if (!sensorConfig.ValidateSampleRate())
            {
                StatusMessage = $"采样率超出范围: {sensorConfig.MinSampleRate}~{sensorConfig.MaxSampleRate} Hz";
                return;
            }

            var connConfig = new ConnectionConfig
            {
                Type = SelectedConnectionType,
                PortName = SelectedPort,
                BaudRate = BaudRate,
                DataBits = DataBits,
                Parity = Parity,
                StopBits = StopBits,
                IpAddress = IpAddress,
                Port = Port,
            };

            // 创建连接
            _connection = _connectionFactory.Create(connConfig);
            _connection.DataReceived += OnDataReceived;
            _connection.ErrorOccurred += OnErrorOccurred;
            _connection.ConnectionStateChanged += OnConnectionStateChanged;

            // 使用 ProtocolConfig 创建解析器
            _parser = ParserFactory.Create(ProtocolConfig);

            // 创建传感器适配器
            _sensorAdapter = SensorAdapterFactory.Create(sensorConfig);

            StatusMessage = "正在连接...";
            await _connection.ConnectAsync();
            StatusMessage = "已连接";

            // 通知数据总线采集开始
            _dataBus.PublishAcquisitionStarted(sensorConfig);
        }
        catch (Exception ex)
        {
            // 清理已创建但连接失败的资源
            if (_connection != null)
            {
                _connection.DataReceived -= OnDataReceived;
                _connection.ErrorOccurred -= OnErrorOccurred;
                _connection.ConnectionStateChanged -= OnConnectionStateChanged;
                try { await _connection.DisposeAsync(); } catch { }
                _connection = null;
            }
            _parser = null;
            _sensorAdapter = null;
            StatusMessage = $"连接失败: {ex.Message}";
        }
    }

    private async Task DisconnectAsync()
    {
        if (_connection != null)
        {
            _connection.DataReceived -= OnDataReceived;
            _connection.ErrorOccurred -= OnErrorOccurred;
            _connection.ConnectionStateChanged -= OnConnectionStateChanged;
            await _connection.DisconnectAsync();
            await _connection.DisposeAsync();
            _connection = null;
        }
        _parser?.Reset();
        _parser = null;
        _sensorAdapter = null;
        _dataBus.PublishAcquisitionStopped();
        IsConnected = false;
        StatusMessage = "已断开";
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        // 如果开启了 Hex 显示，先显示原始字节
        if (ShowHex)
        {
            var hexLine = $"[HEX] {BitConverter.ToString(data).Replace("-", " ")}";
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                RawDataLines.Add(hexLine);
                while (RawDataLines.Count > MaxRawDataLines)
                    RawDataLines.RemoveAt(0);
            });
        }

        _parser?.Feed(data, 0, data.Length);

        while (_parser?.TryParse(out var reading) == true && reading != null)
        {
            var processed = _sensorAdapter?.Process(reading) ?? reading;

            // 正交度校正（在发布之前应用）
            if (IsOrthogonalityCorrectionEnabled && ActiveOrthogonalityProfile != null)
            {
                // 保存原始值
                processed.OriginalChannelValues = processed.ChannelValues.ToArray();
                processed = _orthogonalityCorrector.ApplyToReading(
                    ActiveOrthogonalityProfile, SecondOrthogonalityProfile, processed);
            }

            // 发布到数据总线（供实时图表等消费者使用）
            _dataBus.PublishReading(processed);

            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var line = $"[{processed.Timestamp:HH:mm:ss.fff}] " +
                           string.Join(", ", processed.ChannelValues.Select(v => v.ToString("F2")));
                if (processed.TotalField.HasValue)
                    line += $" | Total: {processed.TotalField:F2}";

                RawDataLines.Add(line);
                while (RawDataLines.Count > MaxRawDataLines)
                    RawDataLines.RemoveAt(0);
            });
        }
    }

    private void OnErrorOccurred(object? sender, string message)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            StatusMessage = $"错误: {message}";
        });
    }

    private void OnConnectionStateChanged(object? sender, bool connected)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsConnected = connected;
            StatusMessage = connected ? "已连接" : "已断开";
        });
    }

    // ---- 协议配置管理 ----

    [RelayCommand]
    private void SaveProtocol()
    {
        try
        {
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
            ProtocolConfig = SelectedSavedProtocol;
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
