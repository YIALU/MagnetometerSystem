using System.Text;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Protocol;
using MagnetometerSystem.Core.Sensors;

namespace MagnetometerSystem.Core.Tests;

public class AcquisitionProtocolTests
{
    [Theory]
    [InlineData(0.001)]
    [InlineData(1000)]
    [InlineData(1000000)]
    public void PositiveFiniteRate_DoesNotDependOnLegacyDeviceType(double rate)
    {
        foreach (var type in Enum.GetValues<SensorType>())
            Assert.True(new SensorConfig { Type = type, SampleRate = rate }.ValidateSampleRate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidRate_IsRejected(double rate) => Assert.False(new SensorConfig { SampleRate = rate }.ValidateSampleRate());

    [Fact]
    public void GenericAcquisition_PreservesTwentyOneChannelsAndUnits()
    {
        var config = new SensorConfig { ChannelCountOverride = 21, ChannelNamesOverride = Enumerable.Range(0, 21).Select(i => $"CH{i}").ToArray(), ChannelUnitsOverride = Enumerable.Repeat("V", 21).ToArray() };
        var reading = new MagnetometerReading { ChannelValues = Enumerable.Range(0, 21).Select(i => (double)i).ToArray() };
        var adapter = SensorAdapterFactory.Create(config);
        Assert.Same(reading, adapter.Process(reading));
        Assert.Equal(21, reading.ChannelValues.Length);
        Assert.Equal(21, adapter.GetChannelCount());
        Assert.Equal(SensorType.Generic, reading.SensorType);
        Assert.All(config.ChannelUnits, unit => Assert.Equal("V", unit));
    }

    [Fact]
    public void HeaderCommentsAndInvalidFields_DoNotBlockFollowingFrameOrCreateZeroValues()
    {
        var config = ProtocolConfig.CreateDefaultAsciiTriaxial();
        config.AsciiHasHeader = true;
        var parser = new ConfigurableAsciiParser(config);
        byte[] bytes = Encoding.ASCII.GetBytes("X,Y,Z\n# sample\n1,,3\n4,NaN,6\n7,8,9\n");
        parser.Feed(bytes, 0, bytes.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(new[] { 7d, 8d, 9d }, reading!.ChannelValues);
        Assert.Equal(2, parser.RejectedFrameCount);
        Assert.False(parser.TryParse(out _));
    }

    [Fact]
    public void ConfiguredCarriageReturnAndMultiCharacterDelimiter_AreApplied()
    {
        var parser = new ConfigurableAsciiParser(new ProtocolConfig { AsciiDelimiter = "::", AsciiLineEnding = "\r", AsciiSkipLines = 2 });
        byte[] bytes = Encoding.ASCII.GetBytes("title\runits\r1::2::3\r");
        parser.Feed(bytes, 0, bytes.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(new[] { 1d, 2d, 3d }, reading!.ChannelValues);
    }

    [Fact]
    public void ActiveParser_UsesSnapshotAfterEditorMutation()
    {
        var config = ProtocolConfig.CreateDefaultAsciiTriaxial();
        var parser = new ConfigurableAsciiParser(config);
        config.FieldMappings[0].Scale = 50;
        var bytes = Encoding.ASCII.GetBytes("1,2,3\n");
        parser.Feed(bytes, 0, bytes.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(1, reading!.ChannelValues[0]);
    }

    [Fact]
    public void DuplicateOrGappedChannelIndices_FailBeforeConnection()
    {
        var config = ProtocolConfig.CreateDefaultAsciiTriaxial();
        config.FieldMappings[2].ChannelIndex = 8;
        Assert.Throws<ArgumentException>(config.Validate);
        config.FieldMappings[2].ChannelIndex = 0;
        Assert.Throws<ArgumentException>(config.Validate);
    }

    [Fact]
    public void SwitchingToAscii_IgnoresRetainedBinarySegments()
    {
        var config = ProtocolConfig.CreateDefaultBinaryTriaxialSegments();
        config.Category = ProtocolCategory.Ascii;
        config.FieldMappings = [new() { Name = "Voltage", Unit = "V", ChannelIndex = 0 }];
        config.Validate();
        Assert.Equal(1, config.DerivedChannelCount);
        Assert.Equal(new[] { "Voltage" }, config.DerivedChannelNames);
        Assert.Equal(new[] { "V" }, config.DerivedChannelUnits);
        var parser = new ConfigurableAsciiParser(config);
        var bytes = Encoding.ASCII.GetBytes("3.3\n");
        parser.Feed(bytes, 0, bytes.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(new[] { 3.3 }, reading!.ChannelValues);
    }

    [Fact]
    public void ReadingClone_DoesNotShareRawOrProcessedArrays()
    {
        var source = new MagnetometerReading { ChannelValues = [2, 3], OriginalChannelValues = [1, 2] };
        var clone = source.DeepClone();
        clone.ChannelValues[0] = 99;
        clone.OriginalChannelValues![0] = 99;
        Assert.Equal(2, source.ChannelValues[0]);
        Assert.Equal(1, source.OriginalChannelValues![0]);
    }

    [Theory]
    [InlineData(2, 0x26)]
    [InlineData(3, 0x12)]
    public void ReloadingLegacyBinary_PreservesChecksumByteStart(int start, byte checksum)
    {
        var oldConfig = new ProtocolConfig
        {
            Category = ProtocolCategory.Binary, FrameHeader = "AA55", HasLengthByte = false,
            FixedDataLength = 2, Checksum = ChecksumType.Xor, ChecksumStartOffset = start,
            FieldMappings = [new() { Name = "Value", DataType = FieldDataType.UInt16, ChannelIndex = 0 }],
        };
        var config = ProtocolConfig.FromJson(oldConfig.ToJson())!;
        config.Validate();
        var parser = ParserFactory.Create(config);
        byte[] frame = [0xAA, 0x55, 0x34, 0x12, checksum];
        parser.Feed(frame, 0, frame.Length);
        Assert.True(parser.TryParse(out var reading));
        Assert.Equal(new[] { 4660d }, reading!.ChannelValues);
    }
}
