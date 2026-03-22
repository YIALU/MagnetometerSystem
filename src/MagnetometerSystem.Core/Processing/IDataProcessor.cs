namespace MagnetometerSystem.Core.Processing;

/// <summary>
/// 数据处理器接口，提供基础滤波算法。
/// </summary>
public interface IDataProcessor
{
    /// <summary>
    /// 移动平均滤波。
    /// </summary>
    /// <param name="data">原始数据数组。</param>
    /// <param name="windowSize">窗口大小，必须为正整数。</param>
    /// <returns>滤波后的数据数组，长度与输入相同。</returns>
    double[] MovingAverage(double[] data, int windowSize);

    /// <summary>
    /// 中值滤波。
    /// </summary>
    /// <param name="data">原始数据数组。</param>
    /// <param name="windowSize">窗口大小，必须为正奇数。若传入偶数则自动 +1。</param>
    /// <returns>滤波后的数据数组，长度与输入相同。</returns>
    double[] MedianFilter(double[] data, int windowSize);
}
