using MagnetometerSystem.Core.Models;
using MagnetometerSystem.Core.Services;

namespace MagnetometerSystem.Core.Tests;

public class AcquisitionAdmissionTests
{
    [Fact]
    public async Task FaultClosesAdmissionEvenWithoutCriticalConsumer_UntilNextPreparation()
    {
        var bus = new DataBus();
        var observed = new List<MagnetometerReading>();
        bus.ReadingReceived += observed.Add;
        Assert.True(bus.TryPublishAcquisitionReading(new MagnetometerReading { ChannelValues = [1] }));
        bus.PublishAcquisitionFault(new IOException("storage failed"));
        Assert.False(bus.TryPublishAcquisitionReading(new MagnetometerReading { ChannelValues = [2] }));
        bus.PublishReading(new MagnetometerReading { ChannelValues = [3] });
        Assert.Equal(1, Assert.Single(observed).ChannelValues[0]);

        await bus.PublishAcquisitionStartingAsync(new SensorConfig());
        Assert.True(bus.TryPublishAcquisitionReading(new MagnetometerReading { ChannelValues = [4] }));
        Assert.Equal(new[] { 1d, 4d }, observed.Select(r => r.ChannelValues[0]));
    }

    [Fact]
    public void RefusedReadingIsNotPublished_AndAConsumerCanLaterAccept()
    {
        var bus = new DataBus();
        bool ready = false;
        int published = 0;
        int faults = 0;
        bus.RegisterAcquisitionReadingAcceptor(_ => ready);
        bus.ReadingReceived += _ => published++;
        bus.AcquisitionFaulted += _ => faults++;

        Assert.False(bus.TryPublishAcquisitionReading(new MagnetometerReading()));
        Assert.Equal(0, published);
        Assert.Equal(0, faults);
        ready = true;
        Assert.True(bus.TryPublishAcquisitionReading(new MagnetometerReading()));
        Assert.Equal(1, published);
    }

    [Fact]
    public void CriticalConsumerExceptionClosesAdmissionNotifiesFaultAndSkipsObservers()
    {
        var bus = new DataBus();
        var failure = new IOException("cannot retain reading");
        int attempts = 0;
        int published = 0;
        Exception? fault = null;
        bus.RegisterAcquisitionReadingAcceptor(_ => { attempts++; throw failure; });
        bus.ReadingReceived += _ => published++;
        bus.AcquisitionFaulted += error => fault = error;

        Assert.False(bus.TryPublishAcquisitionReading(new MagnetometerReading()));
        Assert.False(bus.TryPublishAcquisitionReading(new MagnetometerReading()));

        Assert.Same(failure, fault);
        Assert.Equal(1, attempts);
        Assert.Equal(0, published);
    }

    [Fact]
    public async Task FaultDuringAsyncPreparationIsNotOverriddenWhenPreparationCompletes()
    {
        var bus = new DataBus();
        var releasePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.AcquisitionStarting += _ => releasePreparation.Task;
        var preparing = bus.PublishAcquisitionStartingAsync(new SensorConfig());
        bus.PublishAcquisitionFault(new IOException("session preparation failed"));
        releasePreparation.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => preparing);

        Assert.False(bus.TryPublishAcquisitionReading(new MagnetometerReading()));
    }
}
