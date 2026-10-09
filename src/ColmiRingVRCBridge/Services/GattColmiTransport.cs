using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ColmiRingVRCBridge.Services;

internal sealed class GattColmiTransport : IColmiTransport
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);

    private readonly GattCharacteristic _rxCharacteristic;
    private readonly GattCharacteristic _txCharacteristic;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _started;
    private bool _notificationRequested;
    private volatile bool _disposed;

    public GattColmiTransport(GattCharacteristic rxCharacteristic, GattCharacteristic txCharacteristic)
    {
        _rxCharacteristic = rxCharacteristic;
        _txCharacteristic = txCharacteristic;
    }

    public event Action<byte[]>? PacketReceived;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_started)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _txCharacteristic.ValueChanged += TxCharacteristic_ValueChanged;

        try
        {
            _notificationRequested = true;
            var status = await BoundedBleOperation.AwaitAsync(
                _txCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (status != GattCommunicationStatus.Success)
            {
                throw new InvalidOperationException($"Failed to enable BLE notifications: {status}");
            }

            _started = true;
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            _txCharacteristic.ValueChanged -= TxCharacteristic_ValueChanged;

            if (_notificationRequested)
            {
                try
                {
                    using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
                    await DisableNotificationsAsync(cleanupCts.Token).ConfigureAwait(false);
                }
                catch
                {
                }

                _started = false;
                _notificationRequested = false;
            }

            throw;
        }
    }

    public async Task WriteAsync(byte[] packet, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_started)
        {
            throw new InvalidOperationException("BLE transport is not started.");
        }

        using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        writeCts.CancelAfter(WriteTimeout);
        var token = writeCts.Token;
        await _writeLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var writer = new DataWriter();
            writer.WriteBytes(packet);
            var status = await BoundedBleOperation.AwaitAsync(
                _rxCharacteristic.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse)
                    .AsTask(token), token).ConfigureAwait(false);

            if (status != GattCommunicationStatus.Success)
            {
                throw new InvalidOperationException($"BLE write failed: {status}");
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void TxCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (_disposed) return;
        using var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var data = new byte[(int)reader.UnconsumedBufferLength];
        reader.ReadBytes(data);
        PacketReceived?.Invoke(data);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GattColmiTransport));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _txCharacteristic.ValueChanged -= TxCharacteristic_ValueChanged;

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_notificationRequested)
            {
                try
                {
                    using var cleanupCts = new CancellationTokenSource(CleanupTimeout);
                    await DisableNotificationsAsync(cleanupCts.Token).ConfigureAwait(false);
                }
                catch { }
            }
        }
        finally { _writeLock.Release(); }

        _started = false;
        _notificationRequested = false;
        // Do not dispose a semaphore while previously accepted writers may still
        // be unwinding. It owns no native WaitHandle; waiting writers recheck disposed.
    }

    private Task DisableNotificationsAsync(CancellationToken token) => BoundedBleOperation.AwaitAsync(
        _txCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(token), token);
}
