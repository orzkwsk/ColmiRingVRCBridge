namespace ColmiRingVRCBridge.Services;

internal static class ColmiPacket
{
    public const byte CommandBattery = 0x03;
    public const byte CommandReboot = 0x08;
    public const byte CommandRealtimeHeartRate = 0x1E;
    public const byte CommandStartRealtime = 0x69;
    public const byte CommandStopRealtime = 0x6A;

    // QRing protocol exposes both the ordinary HR measurement (0x01) and a
    // dedicated real-time HR measurement type (0x06). R06 testing shows the
    // ordinary type behaves like a finite measurement and stops after a few
    // readings, so the bridge uses the dedicated continuous type.
    public const byte RealtimeHeartRate = 0x06;

    // Older QRing/R02-family implementations send ASCII '3' (0x33) with
    // command 0x1E when requesting the current computed heart rate.
    public const byte RealtimeHeartRatePollType = 0x33;

    public const byte ActionStart = 0x01;
    public const byte ActionContinue = 0x03;

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
