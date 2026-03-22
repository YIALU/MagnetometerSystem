namespace MagnetometerSystem.Infrastructure.Configuration;

/// <summary>
/// 应用配置持久化服务接口。
/// </summary>
public interface IAppConfigService
{
    /// <summary>
    /// 获取指定键的配置值。键不存在时返回 default(T)。
    /// </summary>
    Task<T?> GetAsync<T>(string key);

    /// <summary>
    /// 设置指定键的配置值。键存在则更新，不存在则插入。
    /// </summary>
    Task SetAsync<T>(string key, T value);

    /// <summary>
    /// 加载完整的应用设置。各字段从对应键读取，缺失时使用默认值。
    /// </summary>
    Task<AppSettings> LoadSettingsAsync();

    /// <summary>
    /// 保存完整的应用设置。各字段写入对应键。
    /// </summary>
    Task SaveSettingsAsync(AppSettings settings);
}

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
}
