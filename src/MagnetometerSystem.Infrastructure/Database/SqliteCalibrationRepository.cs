using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Infrastructure.Database;

/// <summary>
/// 基于 SQLite + Dapper 的校正/校准配置持久化仓库实现
/// </summary>
public class SqliteCalibrationRepository : ICalibrationRepository
{
    private readonly DatabaseInitializer _dbInit;

    public SqliteCalibrationRepository(DatabaseInitializer dbInit)
    {
        _dbInit = dbInit;
    }

    // === 正交度配置 ===

    public async Task SaveOrthogonalityProfileAsync(OrthogonalityParams profile)
    {
        const string sql = """
            INSERT OR REPLACE INTO orthogonality_profiles
            (id, name, sensor_serial, created_at,
             offset_x, offset_y, offset_z,
             m00, m01, m02, m10, m11, m12, m20, m21, m22,
             residual_mean, residual_std, sample_count, notes)
            VALUES
            (@Id, @Name, @SensorSerial, @CreatedAt,
             @OffsetX, @OffsetY, @OffsetZ,
             @M00, @M01, @M02, @M10, @M11, @M12, @M20, @M21, @M22,
             @ResidualMean, @ResidualStd, @SampleCount, @Notes)
            """;

        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync(sql, new
        {
            profile.Id,
            profile.Name,
            profile.SensorSerial,
            CreatedAt = profile.CreatedAt.ToString("O"),
            OffsetX = profile.Offset[0],
            OffsetY = profile.Offset[1],
            OffsetZ = profile.Offset[2],
            M00 = profile.CompensationMatrix[0],
            M01 = profile.CompensationMatrix[1],
            M02 = profile.CompensationMatrix[2],
            M10 = profile.CompensationMatrix[3],
            M11 = profile.CompensationMatrix[4],
            M12 = profile.CompensationMatrix[5],
            M20 = profile.CompensationMatrix[6],
            M21 = profile.CompensationMatrix[7],
            M22 = profile.CompensationMatrix[8],
            profile.ResidualMean,
            profile.ResidualStd,
            profile.SampleCount,
            profile.Notes
        });
    }

    public async Task<IReadOnlyList<OrthogonalityParams>> GetOrthogonalityProfilesAsync(
        string? sensorSerial = null)
    {
        var sql = "SELECT * FROM orthogonality_profiles";
        if (sensorSerial != null)
            sql += " WHERE sensor_serial = @SensorSerial";
        sql += " ORDER BY created_at DESC";

        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        var rows = await conn.QueryAsync(sql, new { SensorSerial = sensorSerial });
        return rows.Select(MapToOrthogonalityParams).ToList().AsReadOnly();
    }

    public async Task<OrthogonalityParams?> GetOrthogonalityProfileAsync(string id)
    {
        const string sql = "SELECT * FROM orthogonality_profiles WHERE id = @Id";

        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        var row = await conn.QueryFirstOrDefaultAsync(sql, new { Id = id });
        return row != null ? MapToOrthogonalityParams(row) : null;
    }

    public async Task DeleteOrthogonalityProfileAsync(string id)
    {
        const string sql = "DELETE FROM orthogonality_profiles WHERE id = @Id";

        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync(sql, new { Id = id });
    }

    // === 通用校准配置 ===

    public async Task SaveCalibrationProfileAsync(CalibrationParams profile)
    {
        const string sql = """
            INSERT OR REPLACE INTO calibration_profiles
            (id, name, sensor_type, sensor_serial, created_at,
             offset_values, gain_values, notes)
            VALUES
            (@Id, @Name, @SensorType, @SensorSerial, @CreatedAt,
             @OffsetValues, @GainValues, @Notes)
            """;

        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync(sql, new
        {
            profile.Id,
            profile.Name,
            SensorType = profile.SensorType.ToString(),
            profile.SensorSerial,
            CreatedAt = profile.CreatedAt.ToString("O"),
            OffsetValues = JsonSerializer.Serialize(profile.OffsetValues),
            GainValues = JsonSerializer.Serialize(profile.GainValues),
            profile.Notes
        });
    }

    public async Task<IReadOnlyList<CalibrationParams>> GetCalibrationProfilesAsync(
        SensorType? sensorType = null)
    {
        var sql = "SELECT * FROM calibration_profiles";
        if (sensorType != null)
            sql += " WHERE sensor_type = @SensorType";
        sql += " ORDER BY created_at DESC";

        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        var rows = await conn.QueryAsync(sql,
            new { SensorType = sensorType?.ToString() });
        return rows.Select(MapToCalibrationParams).ToList().AsReadOnly();
    }

    public async Task DeleteCalibrationProfileAsync(string id)
    {
        const string sql = "DELETE FROM calibration_profiles WHERE id = @Id";

        using var conn = new SqliteConnection(_dbInit.ConnectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync(sql, new { Id = id });
    }

    // === 私有映射方法 ===

    private static OrthogonalityParams MapToOrthogonalityParams(dynamic row)
    {
        return new OrthogonalityParams
        {
            Id = (string)row.id,
            Name = (string)row.name,
            SensorSerial = row.sensor_serial as string,
            CreatedAt = DateTime.Parse((string)row.created_at, null, DateTimeStyles.RoundtripKind),
            Offset = [(double)row.offset_x, (double)row.offset_y, (double)row.offset_z],
            CompensationMatrix =
            [
                (double)row.m00, (double)row.m01, (double)row.m02,
                (double)row.m10, (double)row.m11, (double)row.m12,
                (double)row.m20, (double)row.m21, (double)row.m22
            ],
            ResidualMean = row.residual_mean as double?,
            ResidualStd = row.residual_std as double?,
            SampleCount = (int?)(row.sample_count as long?),
            Notes = row.notes as string
        };
    }

    private static CalibrationParams MapToCalibrationParams(dynamic row)
    {
        return new CalibrationParams
        {
            Id = (string)row.id,
            Name = (string)row.name,
            SensorType = Enum.Parse<SensorType>((string)row.sensor_type),
            SensorSerial = row.sensor_serial as string,
            CreatedAt = DateTime.Parse((string)row.created_at, null, DateTimeStyles.RoundtripKind),
            OffsetValues = JsonSerializer.Deserialize<double[]>((string)row.offset_values) ?? [],
            GainValues = JsonSerializer.Deserialize<double[]>((string)row.gain_values) ?? [],
            Notes = row.notes as string
        };
    }
}
