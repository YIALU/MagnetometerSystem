using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Calibration;

/// <summary>
/// 校正/校准配置持久化仓库接口
/// </summary>
public interface ICalibrationRepository
{
    // === 正交度配置 ===

    /// <summary>保存正交度配置（插入或更新）</summary>
    Task SaveOrthogonalityProfileAsync(OrthogonalityParams profile);

    /// <summary>查询正交度配置列表，可按传感器序列号筛选</summary>
    Task<IReadOnlyList<OrthogonalityParams>> GetOrthogonalityProfilesAsync(
        string? sensorSerial = null);

    /// <summary>按 Id 查询单个正交度配置</summary>
    Task<OrthogonalityParams?> GetOrthogonalityProfileAsync(string id);

    /// <summary>按 Id 删除正交度配置</summary>
    Task DeleteOrthogonalityProfileAsync(string id);

    // === 通用校准配置 ===

    /// <summary>保存校准配置</summary>
    Task SaveCalibrationProfileAsync(CalibrationParams profile);

    /// <summary>查询校准配置列表，可按传感器类型筛选</summary>
    Task<IReadOnlyList<CalibrationParams>> GetCalibrationProfilesAsync(
        SensorType? sensorType = null);

    /// <summary>按 Id 删除校准配置</summary>
    Task DeleteCalibrationProfileAsync(string id);
}
