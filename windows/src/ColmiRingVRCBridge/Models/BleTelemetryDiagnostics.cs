namespace ColmiRingVRCBridge.Models;

internal readonly record struct BleTelemetryDiagnostics(
    DateTimeOffset? LastRawNotificationAt,
    DateTimeOffset? LastValidPacketAt,
    DateTimeOffset? LastValidHeartRateAt,
    DateTimeOffset? LastBatteryPollAt,
    DateTimeOffset? LastBatteryPacketAt,
    long RawNotificationCount,
    long InvalidPacketCount,
    long ValidPacketCount,
    long HeartRatePacketCount,
    long BatteryPacketCount,
    long HeartRatePollTxCount,
    long HeartRatePollWriteFailureCount,
    int ConsecutiveHeartRatePollFailures,
    long BatteryPollTxCount,
    long BatteryPollWriteFailureCount,
    int ConsecutiveBatteryPollFailures,
    bool HeartRateSessionStarted)
{
    public static BleTelemetryDiagnostics Empty { get; } = new(
        null,
        null,
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
        0,
        0,
        0,
        0,
        false);
}
