namespace ColmiRingVRCBridge.Models;

internal readonly record struct BleTelemetryDiagnostics(
    DateTimeOffset? LastRawNotificationAt,
    DateTimeOffset? LastValidPacketAt,
    DateTimeOffset? LastValidHeartRateAt,
    long RawNotificationCount,
    long InvalidPacketCount,
    long ValidPacketCount,
    long HeartRatePacketCount,
    long HeartRatePollTxCount,
    long HeartRatePollWriteFailureCount,
    int ConsecutiveHeartRatePollFailures,
    bool HeartRateSessionStarted)
{
    public static BleTelemetryDiagnostics Empty { get; } = new(
        null,
        null,
        null,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        false);
}
