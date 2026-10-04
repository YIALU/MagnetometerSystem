using System.Reflection;
using MagnetometerSystem.Core.Communication;
using MagnetometerSystem.Core.Models;

namespace MagnetometerSystem.Core.Tests;

/// <summary>
/// Exercises the production read/notification boundary without opening a serial port.
/// This verifies managed lock ordering, not driver I/O or hardware close/drain behavior.
/// </summary>
public class SerialReceiveLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedReceiveSubscriberDoesNotPreventDisconnectOrDispose(bool dispose)
    {
        var connection = new SerialDeviceConnection(new ConnectionConfig());
        object ioGate = GetIoGate(connection);
        using var releaseSubscriber = new ManualResetEventSlim();
        var subscriberEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriberCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool readHeldIoGate = false;
        bool subscriberHeldIoGate = true;
        byte[]? received = null;
        int callbackCount = 0;
        connection.DataReceived += (_, bytes) =>
        {
            received = bytes;
            Interlocked.Increment(ref callbackCount);
            subscriberHeldIoGate = Monitor.IsEntered(ioGate);
            subscriberEntered.TrySetResult();
            try
            {
                // Model a subscriber waiting for UI work while that UI closes the connection.
                releaseSubscriber.Wait();
            }
            finally { subscriberCompleted.TrySetResult(); }
        };

        Task receive = Task.Run(() => ReadAndPublish(connection, () =>
        {
            readHeldIoGate = Monitor.IsEntered(ioGate);
            return [0x12, 0x34, 0x56];
        }));
        Task stop = Task.CompletedTask;
        try
        {
            await subscriberEntered.Task.WaitAsync(Timeout);
            stop = Task.Run(() => dispose ? connection.DisposeAsync().AsTask() : connection.DisconnectAsync());

            // If the callback still owns _ioGate, this cannot complete until the subscriber
            // is released. WaitAsync only bounds a regression failure; it does not order the race.
            await stop.WaitAsync(Timeout);
            Assert.False(subscriberCompleted.Task.IsCompleted);
            Assert.False(connection.IsConnected);
            Assert.True(readHeldIoGate);
            Assert.False(subscriberHeldIoGate);
            Assert.Equal(1, callbackCount);
            Assert.Equal(new byte[] { 0x12, 0x34, 0x56 }, received);
        }
        finally
        {
            // Release even when the assertion times out so no worker or lifecycle lock leaks.
            releaseSubscriber.Set();
            await receive.WaitAsync(Timeout);
            await stop.WaitAsync(Timeout);
            await connection.DisposeAsync();
        }
        Assert.True(subscriberCompleted.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ReadFailuresAndThrowingSubscribersRemainIsolatedOutsideTheIoGate()
    {
        await using var connection = new SerialDeviceConnection(new ConnectionConfig());
        object ioGate = GetIoGate(connection);
        var errors = new List<string>();
        var states = new List<bool>();
        var packets = new List<byte[]>();
        var callbackLockStates = new List<bool>();
        connection.ErrorOccurred += (_, _) => throw new InvalidOperationException("faulty error observer");
        connection.ErrorOccurred += (_, error) =>
        {
            callbackLockStates.Add(Monitor.IsEntered(ioGate));
            errors.Add(error);
        };
        connection.ConnectionStateChanged += (_, connected) =>
        {
            callbackLockStates.Add(Monitor.IsEntered(ioGate));
            states.Add(connected);
        };
        connection.DataReceived += (_, _) => throw new InvalidOperationException("faulty data observer");
        connection.DataReceived += (_, data) =>
        {
            callbackLockStates.Add(Monitor.IsEntered(ioGate));
            packets.Add(data);
        };

        ReadAndPublish(connection, () => throw new IOException("simulated read failure"));
        Assert.Contains("simulated read failure", Assert.Single(errors));
        Assert.False(Assert.Single(states));
        Assert.Empty(packets);

        ReadAndPublish(connection, () => null);
        Assert.Empty(packets);
        ReadAndPublish(connection, () => [0x41, 0x42]);
        Assert.Equal(new byte[] { 0x41, 0x42 }, Assert.Single(packets));
        Assert.Single(errors);
        Assert.Single(states);
        Assert.All(callbackLockStates, held => Assert.False(held));
    }

    private static object GetIoGate(SerialDeviceConnection connection) =>
        typeof(SerialDeviceConnection).GetField("_ioGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(connection)!;

    private static void ReadAndPublish(SerialDeviceConnection connection, Func<byte[]?> readAvailable) =>
        typeof(SerialDeviceConnection).GetMethod("ReadAndPublishData", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(connection, [readAvailable]);
}
