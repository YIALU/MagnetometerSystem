using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Helpers;
using MagnetometerSystem.Core.Protocol;

namespace MagnetometerSystem.Core.Tests;

/// <summary>
/// "磁梯度数采卡-pt" 内置协议测试。
/// 黄金数据来自《三分量梯度数采卡通信协议》表 1 下方的数据包样本（101 字节整帧）。
/// </summary>
public class ZdzC08ProtocolTests
{
    /// <summary>文档给出的完整样本包。</summary>
    private const string DocSampleHex =
        "FF 5A AD 00 5C 00 5F 42 9C 46 56 42 9C 46 7A 42 9C 46 37 42 9C 46 2E 42 9C 46 " +
        "40 42 9C 46 00 00 A0 3D 00 00 A0 3D 00 00 E8 3D 28 03 48 43 4E C8 86 3C EC 47 " +
        "38 3F 57 77 B3 C1 F2 F7 67 3D 5B CD F5 BD C2 96 1C C1 60 D2 7C 3D FB 6E 99 3E " +
        "68 CA CB BB E0 2B 1D 47 EA 2B 1D 47 DA 2B 1D 47 DA 02 48 43 BD 67 33";

    // 仅测试字段布局：文档 CRC 参数未知，使用明确的测试 CRC 重写校验字节。
    // 这些测试不构成固件 CRC 参数或文档原始帧有效性的证明。
    private static byte[] DocSample() => WithTestChecksum(ProtocolConfig.HexToBytes(DocSampleHex));

    private static byte[] WithTestChecksum(byte[] frame)
    {
        ushort crc = Crc16.Compute(frame.AsSpan(2, 96), Crc16Variant.Modbus);
        frame[98] = (byte)crc;
        frame[99] = (byte)(crc >> 8);
        return frame;
    }

    private static ProtocolConfig WithTestChecksum(ProtocolConfig config)
    {
        var checksum = config.Segments.Single(s => s.Name.StartsWith("CRC"));
        checksum.Type = SegmentType.Checksum;
        checksum.ChecksumAlgorithm = ChecksumAlgorithm.CRC16;
        checksum.Crc16Variant = Crc16Variant.Modbus;
        checksum.ChecksumStartIndex = 1;
        checksum.ChecksumBigEndian = false;
        checksum.ByteCount = 2;
        return config;
    }

    [Fact]
    public void Config_FrameLengthIs101Bytes()
    {
        Assert.Equal(101, ProtocolConfig.CreateZdzC08().TotalFrameLength);
        Assert.Equal(101, ProtocolConfig.CreateZdzC08MagneticOnly().TotalFrameLength);
    }

    [Fact]
    public void Config_FullVariantExposes21Channels()
    {
        var config = ProtocolConfig.CreateZdzC08();

        Assert.Equal(21, config.DerivedChannelCount);
        Assert.Equal(
            ["X1", "Y1", "Z1", "X2", "Y2", "Z2", "ΔX", "ΔY", "ΔZ", "GPS纬度", "GPS经度",
             "加速度X", "加速度Y", "加速度Z", "陀螺仪X", "陀螺仪Y", "陀螺仪Z",
             "磁力仪X", "磁力仪Y", "磁力仪Z", "入水深度"],
            config.DerivedChannelNames);
    }

    [Fact]
    public void Config_MagneticOnlyVariantExposesSixChannels()
    {
        var config = ProtocolConfig.CreateZdzC08MagneticOnly();

        Assert.Equal(6, config.DerivedChannelCount);
        Assert.Equal(["X1", "Y1", "Z1", "X2", "Y2", "Z2"], config.DerivedChannelNames);
    }

    [Fact]
    public void Config_NoLengthFieldSegment_SoParserTakesFixedLengthPath()
    {
        // 长度字段值恒为 92，但必须配成 Padding：若配成 LengthField，
        // 解析器会走变长路径算出 非数据区(9) + 92 = 101 之外的帧长。
        Assert.DoesNotContain(
            ProtocolConfig.CreateZdzC08().Segments,
            s => s.Type == SegmentType.LengthField);
    }

    [Fact]
    public void Parse_DocumentSample_DecodesAllTwentyOneChannels()
    {
        var parser = new ConfigurableBinaryParser(WithTestChecksum(ProtocolConfig.CreateZdzC08()));
        var frame = DocSample();
        Assert.Equal(101, frame.Length);

        parser.Feed(frame, 0, frame.Length);

        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);
        Assert.Equal(21, reading.ChannelValues.Length);

        // 六个磁分量，单位 nT
        Assert.Equal(20001.185547, reading.ChannelValues[0], 4);   // X1
        Assert.Equal(20001.167969, reading.ChannelValues[1], 4);   // Y1
        Assert.Equal(20001.238281, reading.ChannelValues[2], 4);   // Z1
        Assert.Equal(20001.107422, reading.ChannelValues[3], 4);   // X2
        Assert.Equal(20001.089844, reading.ChannelValues[4], 4);   // Y2
        Assert.Equal(20001.125000, reading.ChannelValues[5], 4);   // Z2

        // 设备端计算的梯度值
        Assert.Equal(0.078125, reading.ChannelValues[6], 6);       // ΔX
        Assert.Equal(0.078125, reading.ChannelValues[7], 6);       // ΔY
        Assert.Equal(0.113281, reading.ChannelValues[8], 6);       // ΔZ

        // 加速度 / 陀螺 / 磁力仪 / 深度
        Assert.Equal(0.05663294, reading.ChannelValues[11], 6);    // 加速度X
        Assert.Equal(-9.78680611, reading.ChannelValues[13], 5);   // 加速度Z
        Assert.Equal(0.06172407, reading.ChannelValues[14], 6);    // 陀螺仪X
        Assert.Equal(40235.875, reading.ChannelValues[17], 3);     // 磁力仪X
        Assert.Equal(200.011139, reading.ChannelValues[20], 4);    // 入水深度
    }

    [Fact]
    public void Parse_DocumentSample_GradientChannelsMatchComponentDifference()
    {
        // 文档注 2：ΔX/ΔY/ΔZ 分别由 X1-X2 / Y1-Y2 / Z1-Z2 得出。
        // 这条不变量是"字段布局解释正确"的最强证据 —— 布局若错位，三个差值不可能同时对上。
        var parser = new ConfigurableBinaryParser(WithTestChecksum(ProtocolConfig.CreateZdzC08()));
        var frame = DocSample();
        parser.Feed(frame, 0, frame.Length);

        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);

        var v = reading.ChannelValues;
        Assert.Equal(v[0] - v[3], v[6], 5);
        Assert.Equal(v[1] - v[4], v[7], 5);
        Assert.Equal(v[2] - v[5], v[8], 5);
    }

    [Fact]
    public void Parse_MagneticOnlyVariant_DecodesSameSixValues()
    {
        var parser = new ConfigurableBinaryParser(WithTestChecksum(ProtocolConfig.CreateZdzC08MagneticOnly()));
        var frame = DocSample();
        parser.Feed(frame, 0, frame.Length);

        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);
        Assert.Equal(6, reading.ChannelValues.Length);
        Assert.Equal(20001.185547, reading.ChannelValues[0], 4);
        Assert.Equal(20001.125000, reading.ChannelValues[5], 4);
    }

    [Fact]
    public void Parse_GpsDoubleFields_UseLittleEndian()
    {
        // GPS 经纬度是本协议里唯一的 8 字节字段，也是全项目第一次真正用到 Double 段。
        // 样本包采集时无定位，值本身无意义，此处锁定的是字节序解释而非数值合理性。
        var parser = new ConfigurableBinaryParser(WithTestChecksum(ProtocolConfig.CreateZdzC08()));
        var frame = DocSample();
        parser.Feed(frame, 0, frame.Length);

        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);

        double expectedLat = BitConverter.ToDouble(frame, 42);
        double expectedLon = BitConverter.ToDouble(frame, 50);
        Assert.Equal(expectedLat, reading.ChannelValues[9]);
        Assert.Equal(expectedLon, reading.ChannelValues[10]);
    }

    [Fact]
    public void Parse_LeadingGarbage_ResyncsToFrameHeader()
    {
        var parser = new ConfigurableBinaryParser(WithTestChecksum(ProtocolConfig.CreateZdzC08()));
        var frame = DocSample();
        var stream = new byte[] { 0x11, 0x22, 0x33, 0xFF, 0x00 }.Concat(frame).ToArray();

        parser.Feed(stream, 0, stream.Length);

        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);
        Assert.Equal(20001.185547, reading.ChannelValues[0], 4);
    }

    [Fact]
    public void Parse_TwoBackToBackFrames_BothDecode()
    {
        var parser = new ConfigurableBinaryParser(WithTestChecksum(ProtocolConfig.CreateZdzC08()));
        var frame = DocSample();
        var stream = frame.Concat(frame).ToArray();

        parser.Feed(stream, 0, stream.Length);

        Assert.True(parser.TryParse(out var first));
        Assert.True(parser.TryParse(out var second));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.ChannelValues[0], second.ChannelValues[0]);
    }

    [Fact]
    public void Parse_IncompleteFrame_ReturnsFalseWithoutConsuming()
    {
        var parser = new ConfigurableBinaryParser(WithTestChecksum(ProtocolConfig.CreateZdzC08()));
        var frame = DocSample();

        // 先喂 100 字节（差一个字节），再补上剩余部分
        parser.Feed(frame, 0, 100);
        Assert.False(parser.TryParse(out _));

        parser.Feed(frame, 100, 1);
        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);
        Assert.Equal(20001.185547, reading.ChannelValues[0], 4);
    }

    [Fact]
    public void Parse_WrongInfoId_RejectsFrame()
    {
        // 信息 ID 打开了 ValidateFixedValue，篡改后该帧必须被拒。
        var parser = new ConfigurableBinaryParser(WithTestChecksum(ProtocolConfig.CreateZdzC08()));
        var frame = DocSample();
        frame[2] = 0xAE;

        parser.Feed(frame, 0, frame.Length);

        Assert.False(parser.TryParse(out _));
    }

    [Fact]
    public void Parse_WrongDataLengthField_RejectsFrame()
    {
        var parser = new ConfigurableBinaryParser(WithTestChecksum(ProtocolConfig.CreateZdzC08()));
        var frame = DocSample();
        frame[4] = 0x5D;

        parser.Feed(frame, 0, frame.Length);

        Assert.False(parser.TryParse(out _));
    }

    [Fact]
    public void Parse_FalseHeaderInPayload_DoesNotDerailNextRealFrame()
    {
        // 92 字节浮点载荷里出现 FF 5A 并不罕见。构造一个"假帧头 + 真整帧"的流，
        // 验证信息 ID / 长度锚点能让解析器跳过诱饵并最终锁上真正的帧。
        //
        var parser = new ConfigurableBinaryParser(WithTestChecksum(ProtocolConfig.CreateZdzC08()));
        var frame = DocSample();
        var decoy = new byte[] { 0xFF, 0x5A, 0x00, 0x11, 0x22, 0x33 };
        var stream = decoy.Concat(frame).ToArray();

        parser.Feed(stream, 0, stream.Length);

        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);
        Assert.Equal(20001.185547, reading.ChannelValues[0], 4);
        Assert.Equal(200.011139, reading.ChannelValues[20], 4);
    }

    [Fact]
    public void Parse_WrongTail_RejectsFrame()
    {
        var parser = new ConfigurableBinaryParser(WithTestChecksum(ProtocolConfig.CreateZdzC08()));
        var frame = DocSample();
        frame[100] = 0x34;

        parser.Feed(frame, 0, frame.Length);

        Assert.False(parser.TryParse(out _));
    }

    [Fact]
    public void Parse_RealDeviceRecord_GradientInvariantHolds()
    {
        // 第二个独立黄金样本：取自 ZDZ_C08 设备实测导出的 512B 存储包中的一条 101B 记录
        // （包内偏移 7）。与文档样本包不同源、数值量级也完全不同（个位数 nT vs 20000 nT），
        // 但同样满足 Δ = 分量差。两个独立来源同时自洽，才能排除"布局碰巧对上一个样本"。
        const string realRecordHex =
            "FF 5A AD 00 5C 00 52 0E 2D 3F 03 D7 A2 C0 E7 4D 93 C0 D6 62 35 C0 00 99 54 C0 " +
            "39 EF BF C0 6A A6 60 40 0C 2A E2 BF 48 85 B2 3F 00 00 00 00 00 00 00 00 00 00 " +
            "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 " +
            "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 C6 48 33";

        var parser = new ConfigurableBinaryParser(WithTestChecksum(ProtocolConfig.CreateZdzC08()));
        var frame = WithTestChecksum(ProtocolConfig.HexToBytes(realRecordHex));
        Assert.Equal(101, frame.Length);

        parser.Feed(frame, 0, frame.Length);

        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);

        var v = reading.ChannelValues;
        Assert.Equal(0.676000, v[0], 4);      // X1
        Assert.Equal(-5.997952, v[5], 4);     // Z2
        Assert.Equal(v[0] - v[3], v[6], 5);   // ΔX
        Assert.Equal(v[1] - v[4], v[7], 5);   // ΔY
        Assert.Equal(v[2] - v[5], v[8], 5);   // ΔZ
    }

    [Fact]
    public void Config_RoundTripsThroughJson()
    {
        var original = ProtocolConfig.CreateZdzC08();

        var restored = ProtocolConfig.FromJson(original.ToJson());

        Assert.NotNull(restored);
        Assert.Equal(101, restored.TotalFrameLength);
        Assert.Equal(21, restored.DerivedChannelCount);
        Assert.Equal(original.DerivedChannelNames, restored.DerivedChannelNames);

        // ValidateFixedValue 必须能穿过序列化，否则重新加载后锚点静默失效
        var infoId = restored.Segments.Single(s => s.Name == "信息ID");
        Assert.True(infoId.ValidateFixedValue);
        Assert.Equal("AD00", infoId.FixedHexValue);

        // 反序列化后的配置解析同一样本包，结果应完全一致
        Assert.True(restored.RequireChecksum);
        Assert.Equal(original.DerivedChannelUnits, restored.DerivedChannelUnits);
        Assert.Throws<ArgumentException>(() => new ConfigurableBinaryParser(restored));
        var parser = new ConfigurableBinaryParser(WithTestChecksum(restored));
        var frame = DocSample();
        parser.Feed(frame, 0, frame.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.NotNull(reading);
        Assert.Equal(20001.185547, reading.ChannelValues[0], 4);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Config_UnconfirmedChecksumBlocksDefaultAndPersistedPresets(bool magneticOnly)
    {
        var config = magneticOnly ? ProtocolConfig.CreateZdzC08MagneticOnly() : ProtocolConfig.CreateZdzC08();
        Assert.True(config.RequireChecksum);
        Assert.Contains("CRC 参数待确认", Assert.Throws<ArgumentException>(() => new ConfigurableBinaryParser(config)).Message);
        var restored = ProtocolConfig.FromJson(config.ToJson())!;
        Assert.Throws<ArgumentException>(() => new ConfigurableBinaryParser(restored));
    }

    [Theory]
    [InlineData(2)] // fixed information ID
    [InlineData(4)] // fixed data length
    [InlineData(20)] // payload checksum
    [InlineData(98)] // checksum byte
    [InlineData(100)] // tail
    public void Parse_CorruptThenValidFrameInOneReceive_DeliversValidFrameImmediately(int corruptedOffset)
    {
        var parser = new ConfigurableBinaryParser(WithTestChecksum(ProtocolConfig.CreateZdzC08()));
        var invalid = DocSample();
        invalid[corruptedOffset] ^= 1;
        var stream = invalid.Concat(DocSample()).ToArray();
        parser.Feed(stream, 0, stream.Length);
        var readings = new List<MagnetometerReading>();
        while (parser.TryParse(out var reading)) readings.Add(reading!);
        Assert.Single(readings);
        Assert.Equal(20001.185547, readings[0].ChannelValues[0], 4);
    }

    [Fact]
    public void Config_FullVariantUnitsRemainAlignedThroughReorderingAndSerialization()
    {
        var config = ProtocolConfig.CreateZdzC08();
        string[] units = ["nT", "nT", "nT", "nT", "nT", "nT", "nT", "nT", "nT", "°", "°",
            "m/s²", "m/s²", "m/s²", "°/s", "°/s", "°/s", "设备单位", "设备单位", "设备单位", "m"];
        Assert.Equal(units, config.DerivedChannelUnits);
        config.Segments.Reverse();
        Assert.Equal(units, config.DerivedChannelUnits);
        Assert.Equal(units, ProtocolConfig.FromJson(config.ToJson())!.DerivedChannelUnits);
        Assert.All(ProtocolConfig.CreateZdzC08MagneticOnly().DerivedChannelUnits, unit => Assert.Equal("nT", unit));
    }

    [Fact]
    public void Config_RequiredChecksumRejectsInvalidRangeOrWidth()
    {
        var config = WithTestChecksum(ProtocolConfig.CreateZdzC08());
        var checksum = config.Segments.Single(s => s.Type == SegmentType.Checksum);
        checksum.ChecksumStartIndex = config.Segments.IndexOf(checksum);
        Assert.Throws<ArgumentException>(() => new ConfigurableBinaryParser(config));
        checksum.ChecksumStartIndex = 1;
        checksum.ByteCount = 1;
        Assert.Throws<ArgumentException>(() => new ConfigurableBinaryParser(config));
    }

}
