namespace ColmiRingVRCBridge.Models;

internal readonly record struct BleTelemetryDiagnostics(
    DateTimeOffset? LastValidNotificationAt,
    DateTimeOffset? LastValidHeartRateAt,
    long HeartRatePollTxCount,
    long HeartRatePollWriteFailureCount,
    int ConsecutiveHeartRatePollFailures);
