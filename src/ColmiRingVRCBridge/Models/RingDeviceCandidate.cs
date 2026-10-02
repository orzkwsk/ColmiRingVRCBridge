namespace ColmiRingVRCBridge.Models;

public sealed record RingDeviceCandidate(string Name, ulong BluetoothAddress, short Rssi)
{
    public string AddressText => FormatBluetoothAddress(BluetoothAddress);

    public string DisplayText => $"{(string.IsNullOrWhiteSpace(Name) ? "Unknown BLE device" : Name)}  [{AddressText}]  {Rssi} dBm";

    public static string FormatBluetoothAddress(ulong address)
    {
        Span<byte> bytes = stackalloc byte[6];
        for (var i = 0; i < 6; i++)
        {
            bytes[5 - i] = (byte)((address >> (8 * i)) & 0xFF);
        }

        return string.Join(":", bytes.ToArray().Select(b => b.ToString("X2")));
    }
}
