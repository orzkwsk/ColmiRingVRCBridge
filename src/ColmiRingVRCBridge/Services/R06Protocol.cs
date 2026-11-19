using ColmiRingVRCBridge.Models;

namespace ColmiRingVRCBridge.Services;

internal enum R06PacketKind
{
    Unknown,
    Battery,
    HeartRate,
    RealtimeStatus
}

internal readonly record struct R06Packet(
    R06PacketKind Kind,
    int? HeartRate,
    BatteryState? Battery,
    byte? ErrorCode);

internal static class R06Protocol
{
    public static byte[] BuildStartHeartRatePacket() => ColmiPacket.Build(
        ColmiPacket.CommandStartRealtime,
        ColmiPacket.RealtimeHeartRate,
        ColmiPacket.ActionStart);

    public static byte[] BuildContinueHeartRatePacket() => ColmiPacket.Build(
        ColmiPacket.CommandStartRealtime,
        ColmiPacket.RealtimeHeartRate,
        ColmiPacket.ActionContinue);

    public static byte[] BuildStopHeartRatePacket() => ColmiPacket.Build(
        ColmiPacket.CommandStopRealtime,
        ColmiPacket.RealtimeHeartRate,
        0x00,
        0x00);

    public static byte[] BuildPollHeartRatePacket() => ColmiPacket.Build(
        ColmiPacket.CommandRealtimeHeartRate,
        ColmiPacket.RealtimeHeartRatePollType);

    public static byte[] BuildBatteryPacket() => ColmiPacket.Build(ColmiPacket.CommandBattery);

    public static bool TryParse(ReadOnlySpan<byte> packet, out R06Packet parsed)
    {
        parsed = default;
        if (!ColmiPacket.IsValid(packet))
        {
            return false;
        }

        switch (packet[0])
        {
            case ColmiPacket.CommandBattery:
                parsed = new R06Packet(
                    R06PacketKind.Battery,
                    null,
                    new BatteryState(Math.Clamp((int)packet[1], 0, 100), packet[2] != 0),
                    null);
                return true;

            case ColmiPacket.CommandRealtimeHeartRate:
                parsed = new R06Packet(
                    R06PacketKind.HeartRate,
                    packet[1] > 0 ? (int)packet[1] : null,
                    null,
                    null);
                return true;

            case ColmiPacket.CommandStartRealtime:
                if (packet[1] != ColmiPacket.RealtimeHeartRate)
                {
                    parsed = new R06Packet(R06PacketKind.Unknown, null, null, null);
                    return true;
                }

                byte? errorCode = packet[2] == 0 ? null : packet[2];
                parsed = new R06Packet(
                    R06PacketKind.RealtimeStatus,
                    errorCode is null && packet[3] > 0 ? (int)packet[3] : null,
                    null,
                    errorCode);
                return true;

            default:
                parsed = new R06Packet(R06PacketKind.Unknown, null, null, null);
                return true;
        }
    }
}
