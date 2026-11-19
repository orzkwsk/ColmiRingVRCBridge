using System.Diagnostics;

namespace ColmiRingVRCBridge.Models;

internal sealed record RingReading<T>(T Value, DateTimeOffset ReceivedAt, long Timestamp)
{
    public static RingReading<T> Capture(T value) => new(value, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp());
    public bool IsFresh(TimeSpan threshold) => Stopwatch.GetElapsedTime(Timestamp) <= threshold;
}

internal sealed record RingConnectionSnapshot(
    long Id, RingDeviceCandidate? Device, RingDeviceInfo? Info = null,
    RingReading<int>? HeartRate = null, RingReading<BatteryState>? Battery = null)
{
    public static RingConnectionSnapshot Disconnected { get; } = new(0, null);
    public bool IsConnected => Device is not null;

    public HeartRateTelemetryState GetTelemetryState(TimeSpan staleAfter) =>
        !IsConnected ? HeartRateTelemetryState.Disconnected :
        HeartRate is null ? HeartRateTelemetryState.Initializing :
        HeartRate.IsFresh(staleAfter) ? HeartRateTelemetryState.Streaming : HeartRateTelemetryState.Stale;
}
