using System.Collections.Concurrent;
using ColmiRingVRCBridge.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ColmiRingVRCBridge.Services;

internal sealed class ColmiRingBleService : IAsyncDisposable
{
    public static readonly Guid UartServiceUuid = Guid.Parse("6E40FFF0-B5A3-F393-E0A9-E50E24DCCA9E");
    public static readonly Guid UartRxUuid = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
    public static readonly Guid UartTxUuid = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");

    private static readonly Guid DeviceInfoServiceUuid = Guid.Parse("0000180A-0000-1000-8000-00805F9B34FB");
    private static readonly Guid ModelNumberUuid = Guid.Parse("00002A24-0000-1000-8000-00805F9B34FB");
    private static readonly Guid SerialNumberUuid = Guid.Parse("00002A25-0000-1000-8000-00805F9B34FB");
    private static readonly Guid FirmwareRevisionUuid = Guid.Parse("00002A26-0000-1000-8000-00805F9B34FB");
    private static readonly Guid HardwareRevisionUuid = Guid.Parse("00002A27-0000-1000-8000-00805F9B34FB");

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private BluetoothLEDevice? _device;
    private GattDeviceService? _uartService;
    private GattCharacteristic? _rxCharacteristic;
    private GattCharacteristic? _txCharacteristic;
    private CancellationTokenSource? _batteryPollingCts;
    private Task? _batteryPollingTask;

    public event Action<int>? HeartRateUpdated;
    public event Action<BatteryState>? BatteryUpdated;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? ProtocolWarning;

    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public async Task<IReadOnlyList<RingDeviceCandidate>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var devices = new ConcurrentDictionary<ulong, RingDeviceCandidate>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var name = args.Advertisement.LocalName ?? string.Empty;
            var hasKnownService = args.Advertisement.ServiceUuids.Contains(UartServiceUuid);
            if (!hasKnownService && !LooksLikeColmiRing(name))
            {
                return;
            }

            devices.AddOrUpdate(
                args.BluetoothAddress,
                _ => new RingDeviceCandidate(name, args.BluetoothAddress, args.RawSignalStrengthInDBm),
                (_, existing) => new RingDeviceCandidate(
                    string.IsNullOrWhiteSpace(name) ? existing.Name : name,
                    args.BluetoothAddress,
                    args.RawSignalStrengthInDBm));
        }

        watcher.Received += OnReceived;
        watcher.Start();
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            watcher.Stop();
            watcher.Received -= OnReceived;
        }

        return devices.Values
            .OrderByDescending(x => x.Rssi)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<RingDeviceInfo> ConnectAsync(RingDeviceCandidate candidate)
    {
        await DisconnectAsync().ConfigureAwait(false);

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(candidate.BluetoothAddress);
        if (_device is null)
        {
            throw new InvalidOperationException("Windows could not open the selected BLE device.");
        }

        _device.ConnectionStatusChanged += Device_ConnectionStatusChanged;

        var services = await _device.GetGattServicesForUuidAsync(UartServiceUuid, BluetoothCacheMode.Uncached);
        if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
        {
            await DisconnectAsync().ConfigureAwait(false);
            throw new InvalidOperationException("COLMI UART GATT service was not found on the selected device.");
        }

        _uartService = services.Services[0];

        var rxResult = await _uartService.GetCharacteristicsForUuidAsync(UartRxUuid, BluetoothCacheMode.Uncached);
        var txResult = await _uartService.GetCharacteristicsForUuidAsync(UartTxUuid, BluetoothCacheMode.Uncached);
        if (rxResult.Status != GattCommunicationStatus.Success || rxResult.Characteristics.Count == 0 ||
            txResult.Status != GattCommunicationStatus.Success || txResult.Characteristics.Count == 0)
        {
            await DisconnectAsync().ConfigureAwait(false);
            throw new InvalidOperationException("COLMI UART RX/TX characteristics were not found.");
        }

        _rxCharacteristic = rxResult.Characteristics[0];
        _txCharacteristic = txResult.Characteristics[0];
        _txCharacteristic.ValueChanged += TxCharacteristic_ValueChanged;

        var notifyStatus = await _txCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (notifyStatus != GattCommunicationStatus.Success)
        {
            await DisconnectAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Failed to enable BLE notifications: {notifyStatus}");
        }

        ConnectionChanged?.Invoke(true);

        var deviceInfo = await ReadDeviceInfoAsync().ConfigureAwait(false);
        await StartRealtimeHeartRateAsync().ConfigureAwait(false);
        await RequestBatteryAsync().ConfigureAwait(false);
        StartBatteryPolling();

        return deviceInfo;
    }

    public async Task DisconnectAsync()
    {
        await StopBatteryPollingAsync().ConfigureAwait(false);

        if (_rxCharacteristic is not null && IsConnected)
        {
            try
            {
                await StopRealtimeHeartRateAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (_txCharacteristic is not null)
        {
            _txCharacteristic.ValueChanged -= TxCharacteristic_ValueChanged;
            try
            {
                await _txCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch
            {
            }
        }

        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
        }

        _txCharacteristic = null;
        _rxCharacteristic = null;
        _uartService?.Dispose();
        _uartService = null;
        _device?.Dispose();
        _device = null;
        ConnectionChanged?.Invoke(false);
    }

    public Task StartRealtimeHeartRateAsync()
    {
        return WritePacketAsync(ColmiPacket.Build(
            ColmiPacket.CommandStartRealtime,
            ColmiPacket.RealtimeHeartRate,
            ColmiPacket.ActionStart));
    }

    public Task StopRealtimeHeartRateAsync()
    {
        return WritePacketAsync(ColmiPacket.Build(
            ColmiPacket.CommandStopRealtime,
            ColmiPacket.RealtimeHeartRate,
            0x00,
            0x00));
    }

    public Task RequestBatteryAsync()
    {
        return WritePacketAsync(ColmiPacket.Build(ColmiPacket.CommandBattery));
    }

    private async Task WritePacketAsync(byte[] packet)
    {
        var characteristic = _rxCharacteristic ?? throw new InvalidOperationException("Ring is not connected.");

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            using var writer = new DataWriter();
            writer.WriteBytes(packet);
            var status = await characteristic.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
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

        if (data.Length != 16)
        {
            ProtocolWarning?.Invoke($"Unexpected packet length: {data.Length}");
            return;
        }

        if (!ColmiPacket.IsValid(data))
        {
            ProtocolWarning?.Invoke("Received packet with invalid checksum.");
            return;
        }

        switch (data[0])
        {
            case ColmiPacket.CommandStartRealtime:
                if (data[1] == ColmiPacket.RealtimeHeartRate && data[2] == 0x00 && data[3] > 0)
                {
                    HeartRateUpdated?.Invoke(data[3]);
                }
                else if (data[2] != 0x00)
                {
                    ProtocolWarning?.Invoke($"Realtime HR error code: {data[2]}");
                }
                break;

            case ColmiPacket.CommandBattery:
                BatteryUpdated?.Invoke(new BatteryState(data[1], data[2] != 0));
                break;
        }
    }

    private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        ConnectionChanged?.Invoke(sender.ConnectionStatus == BluetoothConnectionStatus.Connected);
    }

    private void StartBatteryPolling()
    {
        _batteryPollingCts?.Cancel();
        _batteryPollingCts?.Dispose();
        _batteryPollingCts = new CancellationTokenSource();
        _batteryPollingTask = Task.Run(() => BatteryPollingLoopAsync(_batteryPollingCts.Token));
    }

    private async Task BatteryPollingLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                if (IsConnected)
                {
                    await RequestBatteryAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ProtocolWarning?.Invoke($"Battery polling stopped: {ex.Message}");
        }
    }

    private async Task StopBatteryPollingAsync()
    {
        if (_batteryPollingCts is null)
        {
            return;
        }

        var cts = _batteryPollingCts;
        var task = _batteryPollingTask;
        _batteryPollingCts = null;
        _batteryPollingTask = null;
        cts.Cancel();

        if (task is not null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
    }

    private async Task<RingDeviceInfo> ReadDeviceInfoAsync()
    {
        if (_device is null)
        {
            return new RingDeviceInfo(null, null, null, null);
        }

        var result = await _device.GetGattServicesForUuidAsync(DeviceInfoServiceUuid, BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
        {
            return new RingDeviceInfo(null, null, null, null);
        }

        using var service = result.Services[0];
        var model = await ReadStringCharacteristicAsync(service, ModelNumberUuid).ConfigureAwait(false);
        var serial = await ReadStringCharacteristicAsync(service, SerialNumberUuid).ConfigureAwait(false);
        var hardware = await ReadStringCharacteristicAsync(service, HardwareRevisionUuid).ConfigureAwait(false);
        var firmware = await ReadStringCharacteristicAsync(service, FirmwareRevisionUuid).ConfigureAwait(false);
        return new RingDeviceInfo(model, serial, hardware, firmware);
    }

    private static async Task<string?> ReadStringCharacteristicAsync(GattDeviceService service, Guid characteristicUuid)
    {
        var result = await service.GetCharacteristicsForUuidAsync(characteristicUuid, BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0)
        {
            return null;
        }

        var read = await result.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached);
        if (read.Status != GattCommunicationStatus.Success)
        {
            return null;
        }

        using var reader = DataReader.FromBuffer(read.Value);
        var data = new byte[(int)reader.UnconsumedBufferLength];
        reader.ReadBytes(data);
        return System.Text.Encoding.UTF8.GetString(data).TrimEnd('\0').Trim();
    }

    private static bool LooksLikeColmiRing(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var upper = name.ToUpperInvariant();
        return upper.Contains("COLMI", StringComparison.Ordinal) ||
               upper.Contains("RING", StringComparison.Ordinal) ||
               upper.StartsWith("R02", StringComparison.Ordinal) ||
               upper.StartsWith("R03", StringComparison.Ordinal) ||
               upper.StartsWith("R06", StringComparison.Ordinal) ||
               upper.StartsWith("R09", StringComparison.Ordinal) ||
               upper.StartsWith("R10", StringComparison.Ordinal) ||
               upper.StartsWith("R12", StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
