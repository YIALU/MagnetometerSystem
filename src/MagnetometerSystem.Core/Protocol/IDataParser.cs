using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Protocol;

/// <summary>
/// 协议解析器接口：将原始字节流解析为 MagnetometerReading
/// </summary>
public interface IDataParser
{
    /// <summary>
    /// 向解析器输入原始字节数据
    /// </summary>
    void Feed(byte[] data, int offset, int count);

    /// <summary>
    /// 尝试从缓冲区解析出一条完整读数。
    /// 返回 true 表示成功解析出一条，reading 为结果；
    /// 返回 false 表示缓冲区数据不足，需要继续 Feed。
    /// 可循环调用直到返回 false，以一次性取出所有已缓冲的完整帧。
    /// </summary>
    bool TryParse(out MagnetometerReading? reading);

    /// <summary>
    /// 重置解析器内部状态（清空缓冲区）
    /// </summary>
    void Reset();
}
