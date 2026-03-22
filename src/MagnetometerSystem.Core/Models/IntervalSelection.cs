namespace MagnetometerSystem.Core.Models;

/// <summary>
/// Represents a user-selected time interval on the chart (in elapsed seconds)
/// </summary>
public class IntervalSelection
{
    public double StartTime { get; }
    public double EndTime { get; }
    public double Duration => EndTime - StartTime;
    public bool IsValid => EndTime > StartTime;

    public IntervalSelection(double startTime, double endTime)
    {
        StartTime = startTime;
        EndTime = endTime;
    }

    /// <summary>
    /// Find the start index and count of data points within this interval
    /// </summary>
    public (int startIdx, int count) GetIndices(double[] times)
    {
        if (times.Length == 0 || !IsValid) return (0, 0);

        int start = -1;
        int end = -1;

        for (int i = 0; i < times.Length; i++)
        {
            if (start == -1 && times[i] >= StartTime)
                start = i;
            if (times[i] <= EndTime)
                end = i;
        }

        if (start == -1 || end == -1 || end < start) return (0, 0);
        return (start, end - start + 1);
    }
}
