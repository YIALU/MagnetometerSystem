using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Sensors;

namespace MagnetometerSystem.Core.Tests;

public class SensorAdapterFactoryTests
{
    [Fact]
    public void Create_TriaxialFluxgate_ReturnsCorrectAdapter()
    {
        // Arrange
        var config = new SensorConfig { Type = SensorType.TriaxialFluxgate };

        // Act
        var adapter = SensorAdapterFactory.Create(config);

        // Assert
        Assert.IsType<TriaxialFluxgateAdapter>(adapter);
        Assert.Equal(SensorType.TriaxialFluxgate, adapter.SensorType);
        Assert.Equal(3, adapter.GetChannelCount());
        Assert.Equal(new[] { "X", "Y", "Z" }, adapter.GetChannelNames());
    }

    [Fact]
    public void Create_SingleAxisFluxgate_ReturnsCorrectAdapter()
    {
        // Arrange
        var config = new SensorConfig { Type = SensorType.SingleAxisFluxgate };

        // Act
        var adapter = SensorAdapterFactory.Create(config);

        // Assert
        Assert.IsType<SingleAxisFluxgateAdapter>(adapter);
        Assert.Equal(SensorType.SingleAxisFluxgate, adapter.SensorType);
        Assert.Equal(1, adapter.GetChannelCount());
        Assert.Equal(new[] { "B" }, adapter.GetChannelNames());
    }

    [Fact]
    public void Create_DualTriaxialFluxgate_ReturnsCorrectAdapter()
    {
        // Arrange
        var config = new SensorConfig { Type = SensorType.DualTriaxialFluxgate };

        // Act
        var adapter = SensorAdapterFactory.Create(config);

        // Assert
        Assert.IsType<DualTriaxialFluxgateAdapter>(adapter);
        Assert.Equal(SensorType.DualTriaxialFluxgate, adapter.SensorType);
        Assert.Equal(6, adapter.GetChannelCount());
        Assert.Equal(new[] { "X1", "Y1", "Z1", "X2", "Y2", "Z2" }, adapter.GetChannelNames());
    }

    [Fact]
    public void Create_ProtonMagnetometer_ReturnsCorrectAdapter()
    {
        // Arrange
        var config = new SensorConfig { Type = SensorType.ProtonMagnetometer };

        // Act
        var adapter = SensorAdapterFactory.Create(config);

        // Assert
        Assert.IsType<ProtonMagnetometerAdapter>(adapter);
        Assert.Equal(SensorType.ProtonMagnetometer, adapter.SensorType);
        Assert.Equal(1, adapter.GetChannelCount());
        Assert.Equal(new[] { "Total" }, adapter.GetChannelNames());
    }

    [Fact]
    public void Create_InvalidType_ThrowsArgumentException()
    {
        // Arrange
        var config = new SensorConfig { Type = (SensorType)999 };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => SensorAdapterFactory.Create(config));
    }

    [Fact]
    public void Create_AdapterPreservesConfig()
    {
        // Arrange
        var config = new SensorConfig
        {
            Type = SensorType.TriaxialFluxgate,
            SampleRate = 250.0,
            SerialNumber = "SN-12345"
        };

        // Act
        var adapter = SensorAdapterFactory.Create(config);

        // Assert
        Assert.Same(config, adapter.Config);
        Assert.Equal(250.0, adapter.Config.SampleRate);
    }

    [Theory]
    [InlineData(SensorType.TriaxialFluxgate, typeof(TriaxialFluxgateAdapter))]
    [InlineData(SensorType.SingleAxisFluxgate, typeof(SingleAxisFluxgateAdapter))]
    [InlineData(SensorType.DualTriaxialFluxgate, typeof(DualTriaxialFluxgateAdapter))]
    [InlineData(SensorType.ProtonMagnetometer, typeof(ProtonMagnetometerAdapter))]
    public void Create_AllValidTypes_ReturnsExpectedAdapterType(SensorType sensorType, Type expectedType)
    {
        // Arrange
        var config = new SensorConfig { Type = sensorType };

        // Act
        var adapter = SensorAdapterFactory.Create(config);

        // Assert
        Assert.IsType(expectedType, adapter);
    }
}
