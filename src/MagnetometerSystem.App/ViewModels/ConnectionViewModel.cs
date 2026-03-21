using System.Collections.ObjectModel;
using System.IO.Ports;
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

    [ObservableProperty]
    private string _protocolType = "ASCII_CSV";

    public string[] ProtocolTypes { get; } = ["ASCII_CSV", "ASCII_SPACE", "ASCII_AUTO", "BINARY_FLOAT", "BINARY_DOUBLE"];

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

    [ObservableProperty]
    private string _parity = "None";

    public string[] Parities { get; } = ["None", "Odd", "Even"];

    [ObservableProperty]
    private double _stopBits = 1.0;

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

    private const int MaxRawDataLines = 100;

    public ConnectionViewModel(IConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
        RefreshPorts();
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
            // 构造配置
            var sensorConfig = new SensorConfig
            {
                Type = SelectedSensorType,
                SampleRate = SampleRate,
                ProtocolType = ProtocolType,
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

            // 创建协议解析器
            _parser = ParserFactory.Create(ProtocolType, sensorConfig.ChannelCount);

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
        StatusMessage = "已断开";
    }

    private void OnDataReceived(object? sender, byte[] data)
    {
        _parser?.Feed(data, 0, data.Length);

        while (_parser?.TryParse(out var reading) == true && reading != null)
        {
            // 通过传感器适配器处理
            var processed = _sensorAdapter?.Process(reading) ?? reading;

            // 在 UI 线程上更新原始数据显示
            Application.Current?.Dispatcher.Invoke(() =>
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
        Application.Current?.Dispatcher.Invoke(() =>
        {
            StatusMessage = $"错误: {message}";
        });
    }

    private void OnConnectionStateChanged(object? sender, bool connected)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            IsConnected = connected;
            StatusMessage = connected ? "已连接" : "已断开";
        });
    }
}
