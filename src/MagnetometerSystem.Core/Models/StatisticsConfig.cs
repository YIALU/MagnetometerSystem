using System.ComponentModel;

namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 自定义滚动统计配置
/// </summary>
public class StatisticsConfig : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private double _windowSeconds = 60;
    /// <summary>统计时间窗口（秒），0 表示使用图表时间窗口</summary>
    public double WindowSeconds
    {
        get => _windowSeconds;
        set { if (_windowSeconds != value) { _windowSeconds = value; OnPropertyChanged(nameof(WindowSeconds)); } }
    }

    private bool _showMean = true;
    /// <summary>是否显示均值</summary>
    public bool ShowMean
    {
        get => _showMean;
        set { if (_showMean != value) { _showMean = value; OnPropertyChanged(nameof(ShowMean)); } }
    }

    private bool _showStdDev = true;
    /// <summary>是否显示标准差</summary>
    public bool ShowStdDev
    {
        get => _showStdDev;
        set { if (_showStdDev != value) { _showStdDev = value; OnPropertyChanged(nameof(ShowStdDev)); } }
    }

    private bool _showPeakToPeak = true;
    /// <summary>是否显示峰峰值 (max - min)</summary>
    public bool ShowPeakToPeak
    {
        get => _showPeakToPeak;
        set { if (_showPeakToPeak != value) { _showPeakToPeak = value; OnPropertyChanged(nameof(ShowPeakToPeak)); } }
    }

    private bool _showRms;
    /// <summary>是否显示 RMS（均方根）</summary>
    public bool ShowRms
    {
        get => _showRms;
        set { if (_showRms != value) { _showRms = value; OnPropertyChanged(nameof(ShowRms)); } }
    }

    private bool _showMin;
    /// <summary>是否显示最小值</summary>
    public bool ShowMin
    {
        get => _showMin;
        set { if (_showMin != value) { _showMin = value; OnPropertyChanged(nameof(ShowMin)); } }
    }

    private bool _showMax;
    /// <summary>是否显示最大值</summary>
    public bool ShowMax
    {
        get => _showMax;
        set { if (_showMax != value) { _showMax = value; OnPropertyChanged(nameof(ShowMax)); } }
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
