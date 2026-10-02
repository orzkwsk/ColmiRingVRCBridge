namespace ColmiRingVRCBridge.Services;

internal sealed record HeartRateProtocolProbePacket(
    DateTimeOffset TimestampUtc,
    byte Command,
    int? CandidateBpm,
    string Reason,
    string RawHex);
