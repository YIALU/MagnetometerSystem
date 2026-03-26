namespace MagnetometerSystem.Core.Models;

/// <summary>
/// 正交度校准记录
/// </summary>
public class OrthogonalityCalibrationRecord
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string MatrixJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string? Operator { get; set; }
    public string? Notes { get; set; }
}
