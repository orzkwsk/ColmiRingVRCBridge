namespace ColmiRingVRCBridge.Services;

internal interface IColmiTransport : IAsyncDisposable
{
    event Action<byte[]>? PacketReceived;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(byte[] packet, CancellationToken cancellationToken = default);
}
