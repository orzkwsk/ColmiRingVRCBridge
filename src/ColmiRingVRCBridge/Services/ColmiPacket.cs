namespace ColmiRingVRCBridge.Services;

internal static class ColmiPacket
{
    public const byte CommandBattery = 0x03;
    public const byte CommandStartRealtime = 0x69;
    public const byte CommandStopRealtime = 0x6A;
    public const byte RealtimeHeartRate = 0x01;
    public const byte ActionStart = 0x01;

    public static byte[] Build(byte command, params byte[] payload)
    {
        if (payload.Length > 14)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "COLMI payload must be 14 bytes or fewer.");
        }

        var packet = new byte[16];
        packet[0] = command;
        Array.Copy(payload, 0, packet, 1, payload.Length);
        packet[15] = CalculateChecksum(packet);
        return packet;
    }

    public static bool IsValid(ReadOnlySpan<byte> packet)
    {
        return packet.Length == 16 && CalculateChecksum(packet) == packet[15];
    }

    private static byte CalculateChecksum(ReadOnlySpan<byte> packet)
    {
        var sum = 0;
        var length = Math.Min(15, packet.Length);
        for (var i = 0; i < length; i++)
        {
            sum += packet[i];
        }

        return (byte)(sum & 0xFF);
    }
}
