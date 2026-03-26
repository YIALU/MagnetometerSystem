namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 应用全局设置数据模型。
/// </summary>
public class AppSettings
{
    public string? DefaultPortName { get; set; }
    public int DefaultBaudRate { get; set; } = 115200;
    public string? DefaultIpAddress { get; set; }
    public int DefaultPort { get; set; } = 5000;
    public string DataStoragePath { get; set; } = "";
    public bool AutoSaveEnabled { get; set; } = true;
    public int ChartRefreshRate { get; set; } = 30;
    public string ThemeName { get; set; } = "Default";
    public string? DefaultOrthogonalityProfileId { get; set; }
    public string? DefaultOrthogonalitySecondProfileId { get; set; }
}
