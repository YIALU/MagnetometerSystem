using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;
using MagnetometerSystem.Core.Sensors;

namespace MagnetometerSystem.App.ViewModels;

/// <summary>
/// 连接配置 ViewModel
/// </summary>
public partial class ConnectionViewModel : ObservableObject
{
    private readonly IConnectionFactory _connectionFactory;
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

    private const int MaxRawDataLines = 200;

    private static readonly string ProtocolConfigDir = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Protocols");

    public ConnectionViewModel(IConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        RefreshPorts();
        LoadSavedProtocols();
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
        }
        catch (Exception ex)
        {
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

    [RelayCommand]
    private void ClearRawData()
    {
        RawDataLines.Clear();
    }

    private void LoadSavedProtocols()
    {
        SavedProtocols.Clear();

        // 添加内置预设
        SavedProtocols.Add(ProtocolConfig.CreateDefaultAsciiTriaxial());
        SavedProtocols.Add(ProtocolConfig.CreateDefaultBinaryTriaxial());

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
                catch { /* 跳过损坏的配置文件 */ }
            }
        }
    }
}
