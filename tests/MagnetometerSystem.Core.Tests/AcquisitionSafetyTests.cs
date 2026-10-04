using MagnetometerSystem.Core.Calibration;
using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;

namespace MagnetometerSystem.Core.Tests;

public class AcquisitionSafetyTests
{
    [Fact]
    public async Task DataBus_SeparatesRawAndProcessed_AndAwaitsFinalStorage()
    {
        var bus = new DataBus();
        var received = new List<double>();
        bus.ReadingReceived += r => r.ChannelValues[0] = 900;
        bus.ReadingReceived += r => received.Add(r.ChannelValues[0]);
        bus.ProcessedReadingReceived += r => r.ChannelValues[0] = 800;
        var raw = new MagnetometerReading { ChannelValues = [1, 2, 3, 24] };
        bus.PublishReading(raw);
        bus.PublishProcessedReading(raw);
        Assert.Equal([1d], received);
        Assert.Equal(1, raw.ChannelValues[0]);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.AcquisitionStopping += () => gate.Task;
        var stop = bus.PublishAcquisitionStoppingAsync();
        Assert.False(stop.IsCompleted);
        gate.SetResult();
        await stop;
    }

    [Fact]
    public void ExplicitMappedCorrection_PreservesOriginalSnapshotAndTemperature()
    {
        var raw = new MagnetometerReading { SensorType = SensorType.Generic, ChannelValues = [24.5, 11, 22, 33] };
        var corrected = new OrthogonalityCorrector().ApplyToReading(new OrthogonalityParams { Offset = [1, 2, 3] }, null, raw, [1, 2, 3]);
        Assert.Equal([24.5, 10, 20, 30], corrected.ChannelValues);
        Assert.Equal([24.5, 11, 22, 33], corrected.OriginalChannelValues);
        corrected.ChannelValues[0] = 100;
        corrected.OriginalChannelValues![1] = 100;
        Assert.Equal([24.5, 11, 22, 33], raw.ChannelValues);
    }

    [Fact]
    public void UnsafeParametersAndMappings_AreRejectedWithoutChangingRaw()
    {
        var corrector = new OrthogonalityCorrector();
        var raw = new MagnetometerReading { ChannelValues = [1, 2, 3, 24] };
        var invalid = new[]
        {
            new OrthogonalityParams { Offset = [1, 2] },
            new OrthogonalityParams { Offset = [double.NaN, 0, 0] },
            new OrthogonalityParams { CompensationMatrix = new double[9] },
            new OrthogonalityParams { CompensationMatrix = [1, 0, 0, 0, double.PositiveInfinity, 0, 0, 0, 1] }
        };
        foreach (var profile in invalid)
            Assert.Throws<ArgumentException>(() => corrector.ApplyToReading(profile, null, raw, [0, 1, 2]));
        Assert.Throws<ArgumentException>(() => corrector.ApplyToReading(new(), null, raw, [0, 0, 2]));
        Assert.Throws<ArgumentException>(() => corrector.ApplyToReading(new(), null, raw, [0, 1, 9]));
        Assert.Throws<ArgumentException>(() => corrector.ApplyToReading(new(), new(), raw, [0, 1, 2], [1, 2, 3]));
        Assert.Equal([1d, 2d, 3d, 24d], raw.ChannelValues);
    }

    [Fact]
    public async Task CanceledBatch_DoesNotAlterInputReadings()
    {
        var readings = new[] { new MagnetometerReading { ChannelValues = [1, 2, 3, 24] } };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OrthogonalityCorrector()
            .ApplyBatchAsync(new(), null, readings, [0, 1, 2], cancellationToken: cts.Token));
        Assert.Equal([1d, 2d, 3d, 24d], readings[0].ChannelValues);
    }
}
