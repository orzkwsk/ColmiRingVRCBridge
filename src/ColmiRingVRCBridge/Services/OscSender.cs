using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace ColmiRingVRCBridge.Services;

internal sealed class OscSender : IDisposable
{
    private readonly UdpClient _udpClient;

    public OscSender(string host, int port)
    {
        _udpClient = new UdpClient();
        _udpClient.Connect(host, port);
    }

    public Task SendFloatAsync(string address, float value)
    {
        var message = BuildMessage(address, ",f", BitConverter.SingleToInt32Bits(value));
        return _udpClient.SendAsync(message, message.Length);
    }

    public Task SendIntAsync(string address, int value)
    {
        var message = BuildMessage(address, ",i", value);
        return _udpClient.SendAsync(message, message.Length);
    }

    private static byte[] BuildMessage(string address, string typeTag, int valueBits)
    {
        var addressBytes = EncodeOscString(address);
        var typeBytes = EncodeOscString(typeTag);
        var output = new byte[addressBytes.Length + typeBytes.Length + 4];

        Buffer.BlockCopy(addressBytes, 0, output, 0, addressBytes.Length);
        Buffer.BlockCopy(typeBytes, 0, output, addressBytes.Length, typeBytes.Length);
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(addressBytes.Length + typeBytes.Length, 4), valueBits);
        return output;
    }

    private static byte[] EncodeOscString(string value)
    {
        var raw = Encoding.UTF8.GetBytes(value);
        var paddedLength = ((raw.Length + 1 + 3) / 4) * 4;
        var output = new byte[paddedLength];
        Buffer.BlockCopy(raw, 0, output, 0, raw.Length);
        return output;
    }

    public void Dispose()
    {
        _udpClient.Dispose();
    }
}
