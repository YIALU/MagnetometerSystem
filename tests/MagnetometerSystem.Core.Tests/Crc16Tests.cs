using System.Text;
using MagnetometerSystem.Core.Protocol;

namespace MagnetometerSystem.Core.Tests;

/// <summary>
/// 用各 CRC-16 变体的标准校验值（输入 ASCII "123456789"）验证算法实现正确。
/// 这些 check 值来自 CRC 标准目录，是锁定算法正确性的金标准。
/// </summary>
public class Crc16Tests
{
    private static readonly byte[] Check = Encoding.ASCII.GetBytes("123456789");

    [Theory]
    [InlineData(Crc16Variant.Modbus, 0x4B37)]
    [InlineData(Crc16Variant.CcittFalse, 0x29B1)]
    [InlineData(Crc16Variant.XModem, 0x31C3)]
    [InlineData(Crc16Variant.Ibm, 0xBB3D)]
    public void Compute_StandardCheckValue(Crc16Variant variant, int expected)
    {
        ushort actual = Crc16.Compute(Check, variant);
        Assert.Equal((ushort)expected, actual);
    }

    [Fact]
    public void Compute_EmptyInput_ReturnsInit()
    {
        // 空输入：结果应为各变体的初值（未经任何字节处理）
        Assert.Equal((ushort)0xFFFF, Crc16.Compute(ReadOnlySpan<byte>.Empty, Crc16Variant.Modbus));
        Assert.Equal((ushort)0x0000, Crc16.Compute(ReadOnlySpan<byte>.Empty, Crc16Variant.XModem));
    }
}
