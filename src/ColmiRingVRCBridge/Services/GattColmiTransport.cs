using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ColmiRingVRCBridge.Services;

internal sealed class GattColmiTransport : IColmiTransport
{
    private readonly GattCharacteristic _rxCharacteristic;
    private readonly GattCharacteristic _txCharacteristic;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _started;
    private bool _disposed;

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
            var status = await _txCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

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

            if (_started)
            {
                try
                {
                    await _txCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
                }
                catch
                {
                }

                _started = false;
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

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var writer = new DataWriter();
            writer.WriteBytes(packet);
            var status = await _rxCharacteristic.WriteValueAsync(
                writer.DetachBuffer(),
                GattWriteOption.WriteWithoutResponse);
            cancellationToken.ThrowIfCancellationRequested();

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

        if (_started)
        {
            try
            {
                await _txCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch
            {
            }
        }

        _started = false;
        _writeLock.Dispose();
    }
}
