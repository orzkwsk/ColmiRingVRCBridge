using ColmiRingVRCBridge.Models;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge.Tests;

public sealed class R06SessionTests
{
    [Fact]
    public async Task Polling_SurvivesTransientWriteFailureAndResetsConsecutiveCount()
    {
        await using var transport = new FakeTransport
        {
            RemainingPollFailures = 1
        };
        await using var session = new R06Session(
            transport,
            heartRatePollInterval: TimeSpan.FromMilliseconds(10),
            batteryPollInterval: TimeSpan.FromHours(1),
            handshakeDelay: TimeSpan.Zero);

        await session.StartAsync();
        await WaitUntilAsync(
            () => session.GetDiagnostics().HeartRatePollWriteFailureCount >= 1 &&
                  session.GetDiagnostics().HeartRatePollTxCount >= 1,
            TimeSpan.FromSeconds(2));

        var diagnostics = session.GetDiagnostics();
        Assert.True(diagnostics.HeartRatePollWriteFailureCount >= 1);
        Assert.True(diagnostics.HeartRatePollTxCount >= 1);
        Assert.Equal(0, diagnostics.ConsecutiveHeartRatePollFailures);
    }

    [Fact]
    public async Task Diagnostics_DistinguishRawInvalidValidAndHeartRatePackets()
    {
        await using var transport = new FakeTransport();
        await using var session = new R06Session(
            transport,
            heartRatePollInterval: TimeSpan.FromHours(1),
            batteryPollInterval: TimeSpan.FromHours(1),
            handshakeDelay: TimeSpan.Zero);

        await session.StartAsync();

        var invalid = ColmiPacket.Build(ColmiPacket.CommandBattery, 50, 0);
        invalid[15] ^= 0x01;
        transport.Inject(invalid);
        transport.Inject(ColmiPacket.Build(ColmiPacket.CommandBattery, 49, 0));
        transport.Inject(ColmiPacket.Build(ColmiPacket.CommandRealtimeHeartRate, 71));

        var diagnostics = session.GetDiagnostics();
        Assert.Equal(3L, diagnostics.RawNotificationCount);
        Assert.Equal(1L, diagnostics.InvalidPacketCount);
        Assert.Equal(2L, diagnostics.ValidPacketCount);
        Assert.Equal(1L, diagnostics.HeartRatePacketCount);
        Assert.Equal(1L, diagnostics.BatteryPacketCount);
        Assert.NotNull(diagnostics.LastRawNotificationAt);
        Assert.NotNull(diagnostics.LastValidPacketAt);
        Assert.NotNull(diagnostics.LastValidHeartRateAt);
        Assert.NotNull(diagnostics.LastBatteryPacketAt);
    }

    [Fact]
    public async Task BatteryDiagnostics_TrackPollAndResponseSeparately()
    {
        await using var transport = new FakeTransport();
        await using var session = new R06Session(
            transport,
            heartRatePollInterval: TimeSpan.FromHours(1),
            batteryPollInterval: TimeSpan.FromHours(1),
            handshakeDelay: TimeSpan.Zero);

        await session.StartAsync();

        var beforeResponse = session.GetDiagnostics();
        Assert.NotNull(beforeResponse.LastBatteryPollAt);
        Assert.Null(beforeResponse.LastBatteryPacketAt);
        Assert.Equal(1L, beforeResponse.BatteryPollTxCount);
        Assert.Equal(0L, beforeResponse.BatteryPacketCount);
        Assert.Equal(0L, beforeResponse.BatteryPollWriteFailureCount);

        transport.Inject(ColmiPacket.Build(ColmiPacket.CommandBattery, 88, 0));

        var afterResponse = session.GetDiagnostics();
        Assert.NotNull(afterResponse.LastBatteryPacketAt);
        Assert.Equal(1L, afterResponse.BatteryPacketCount);
        Assert.Equal(1L, afterResponse.BatteryPollTxCount);
    }

    [Fact]
    public async Task TelemetryState_TransitionsFromInitializingToStreamingToStale()
    {
        await using var transport = new FakeTransport();
        await using var session = new R06Session(
            transport,
            heartRatePollInterval: TimeSpan.FromHours(1),
            batteryPollInterval: TimeSpan.FromHours(1),
            handshakeDelay: TimeSpan.Zero);

        await session.StartAsync();
        var threshold = TimeSpan.FromSeconds(5);

        Assert.Equal(
            HeartRateTelemetryState.Initializing,
            session.GetTelemetryState(linkConnected: true, DateTimeOffset.UtcNow, threshold));

        transport.Inject(ColmiPacket.Build(ColmiPacket.CommandRealtimeHeartRate, 73));
        var lastHeartRate = session.GetDiagnostics().LastValidHeartRateAt;
        Assert.NotNull(lastHeartRate);

        Assert.Equal(
            HeartRateTelemetryState.Streaming,
            session.GetTelemetryState(true, lastHeartRate!.Value.AddSeconds(1), threshold));
        Assert.Equal(
            HeartRateTelemetryState.Stale,
            session.GetTelemetryState(true, lastHeartRate.Value.AddSeconds(6), threshold));
        Assert.Equal(
            HeartRateTelemetryState.Disconnected,
            session.GetTelemetryState(false, lastHeartRate.Value.AddSeconds(6), threshold));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(false, "Timed out waiting for the expected session state.");
    }

    private sealed class FakeTransport : IColmiTransport
    {
        private int _remainingPollFailures;

        public int RemainingPollFailures
        {
            get => Volatile.Read(ref _remainingPollFailures);
            init => _remainingPollFailures = value;
        }

        public event Action<byte[]>? PacketReceived;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task WriteAsync(byte[] packet, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (packet.Length > 0 &&
                packet[0] == ColmiPacket.CommandRealtimeHeartRate &&
                Interlocked.CompareExchange(ref _remainingPollFailures, 0, 0) > 0)
            {
                Interlocked.Decrement(ref _remainingPollFailures);
                throw new InvalidOperationException("Injected transient poll failure.");
            }

            return Task.CompletedTask;
        }

        public void Inject(byte[] packet) => PacketReceived?.Invoke(packet);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
