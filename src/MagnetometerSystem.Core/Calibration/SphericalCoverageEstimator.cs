namespace MagnetometerSystem.Core.Calibration;

/// <summary>
/// 校正第 2 步进度条的姿态覆盖度：以样本均值为中心，把每个样本的方向分到 12 经度 × 6 纬度的格子里，
/// 返回有样本的格子占比（0–100）。只看前 3 个值；与中心重合的样本不计。
/// 与 <see cref="CalibrationDataValidator"/> 和拟合结果里的覆盖度算法不同，这里沿用原算法，不做统一。
/// </summary>
public static class SphericalCoverageEstimator
{
    public static double Estimate(IReadOnlyList<double[]> samples)
    {
        const int nLon = 12;
        const int nLat = 6;
        var covered = new bool[nLon, nLat];

        double cx = 0, cy = 0, cz = 0;
        int n = samples.Count;
        if (n == 0) return 0;
        for (int i = 0; i < n; i++)
        {
            cx += samples[i][0];
            cy += samples[i][1];
            cz += samples[i][2];
        }
        cx /= n; cy /= n; cz /= n;

        for (int i = 0; i < n; i++)
        {
            double dx = samples[i][0] - cx;
            double dy = samples[i][1] - cy;
            double dz = samples[i][2] - cz;
            double r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (r < 1e-10) continue;

            double lat = Math.Asin(Math.Clamp(dz / r, -1.0, 1.0));
            double lon = Math.Atan2(dy, dx);

            int lonIdx = (int)((lon + Math.PI) / (2 * Math.PI) * nLon);
            if (lonIdx >= nLon) lonIdx = nLon - 1;
            int latIdx = (int)((lat + Math.PI / 2) / Math.PI * nLat);
            if (latIdx >= nLat) latIdx = nLat - 1;

            covered[lonIdx, latIdx] = true;
        }

        int total = nLon * nLat;
        int count = 0;
        foreach (bool c in covered)
            if (c) count++;

        return (double)count / total * 100.0;
    }
}
