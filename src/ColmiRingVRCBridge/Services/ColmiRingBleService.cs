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

    private BluetoothLEDevice? _device;
    private GattDeviceService? _uartService;
    private R06Session? _session;

    public event Action<int>? HeartRateUpdated;
    public event Action<BatteryState>? BatteryUpdated;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? ProtocolWarning;
    public event Action<HeartRateProtocolProbePacket>? HeartRateProtocolProbePacketObserved;

    public bool IsConnected => _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public BleTelemetryDiagnostics GetTelemetryDiagnostics()
    {
        return _session?.GetDiagnostics() ?? BleTelemetryDiagnostics.Empty;
    }

    public HeartRateTelemetryState GetTelemetryState(DateTimeOffset now, TimeSpan staleAfter)
    {
        if (!IsConnected)
        {
            return HeartRateTelemetryState.Disconnected;
        }

        return _session?.GetTelemetryState(true, now, staleAfter)
               ?? HeartRateTelemetryState.Initializing;
    }

    public async Task<IReadOnlyList<RingDeviceCandidate>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var observed = new ConcurrentDictionary<ulong, RingDeviceCandidate>();
        var knownService = new ConcurrentDictionary<ulong, bool>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            var name = args.Advertisement.LocalName ?? string.Empty;
            var hasKnownService = args.Advertisement.ServiceUuids.Contains(UartServiceUuid);

            observed.AddOrUpdate(
                args.BluetoothAddress,
                _ => new RingDeviceCandidate(name, args.BluetoothAddress, args.RawSignalStrengthInDBm),
                (_, existing) => new RingDeviceCandidate(
                    string.IsNullOrWhiteSpace(name) ? existing.Name : name,
                    args.BluetoothAddress,
                    args.RawSignalStrengthInDBm));

            knownService.AddOrUpdate(
                args.BluetoothAddress,
                hasKnownService,
                (_, existing) => existing || hasKnownService);
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

        var candidates = new List<RingDeviceCandidate>();

        foreach (var observedDevice in observed.Values.OrderByDescending(x => x.Rssi))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = observedDevice.Name;
            var hasKnownService = knownService.TryGetValue(observedDevice.BluetoothAddress, out var serviceSeen) && serviceSeen;

            if (!hasKnownService && !LooksLikeColmiRing(name))
            {
                BluetoothLEDevice? resolvedDevice = null;
                try
                {
                    resolvedDevice = await BluetoothLEDevice.FromBluetoothAddressAsync(observedDevice.BluetoothAddress);
                    if (resolvedDevice is not null && !string.IsNullOrWhiteSpace(resolvedDevice.Name))
                    {
                        name = resolvedDevice.Name;
                    }
                }
                catch
                {
                }
                finally
                {
                    resolvedDevice?.Dispose();
                }
            }

            if (hasKnownService || LooksLikeColmiRing(name))
            {
                candidates.Add(new RingDeviceCandidate(
                    name,
                    observedDevice.BluetoothAddress,
                    observedDevice.Rssi));
            }
        }

        return candidates
            .OrderByDescending(x => x.Rssi)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<RingDeviceInfo> ConnectAsync(
        RingDeviceCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(candidate.BluetoothAddress);
        cancellationToken.ThrowIfCancellationRequested();

        if (_device is null)
        {
            throw new InvalidOperationException("Windows could not open the selected BLE device.");
        }

        _device.ConnectionStatusChanged += Device_ConnectionStatusChanged;

        try
        {
            var services = await _device.GetGattServicesForUuidAsync(UartServiceUuid, BluetoothCacheMode.Uncached);
            cancellationToken.ThrowIfCancellationRequested();
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                throw new InvalidOperationException("COLMI UART GATT service was not found on the selected device.");
            }

            _uartService = services.Services[0];

            var rxResult = await _uartService.GetCharacteristicsForUuidAsync(UartRxUuid, BluetoothCacheMode.Uncached);
            var txResult = await _uartService.GetCharacteristicsForUuidAsync(UartTxUuid, BluetoothCacheMode.Uncached);
            cancellationToken.ThrowIfCancellationRequested();

            if (rxResult.Status != GattCommunicationStatus.Success || rxResult.Characteristics.Count == 0 ||
                txResult.Status != GattCommunicationStatus.Success || txResult.Characteristics.Count == 0)
            {
                throw new InvalidOperationException("COLMI UART RX/TX characteristics were not found.");
            }

            var transport = new GattColmiTransport(rxResult.Characteristics[0], txResult.Characteristics[0]);
            var session = new R06Session(transport);
            HookSession(session);
            _session = session;

            var deviceInfo = await ReadDeviceInfoAsync().ConfigureAwait(false);
            await session.StartAsync(cancellationToken).ConfigureAwait(false);

            ConnectionChanged?.Invoke(true);
            return deviceInfo;
        }
        catch
        {
            await DisconnectAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            UnhookSession(session);
            await session.DisposeAsync().ConfigureAwait(false);
        }

        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
        }

        _uartService?.Dispose();
        _uartService = null;
        _device?.Dispose();
        _device = null;

        ConnectionChanged?.Invoke(false);
    }

    private void HookSession(R06Session session)
    {
        session.HeartRateUpdated += Session_HeartRateUpdated;
        session.BatteryUpdated += Session_BatteryUpdated;
        session.ProtocolWarning += Session_ProtocolWarning;
        session.HeartRateProtocolProbePacketObserved += Session_HeartRateProtocolProbePacketObserved;
    }

    private void UnhookSession(R06Session session)
    {
        session.HeartRateUpdated -= Session_HeartRateUpdated;
        session.BatteryUpdated -= Session_BatteryUpdated;
        session.ProtocolWarning -= Session_ProtocolWarning;
        session.HeartRateProtocolProbePacketObserved -= Session_HeartRateProtocolProbePacketObserved;
    }

    private void Session_HeartRateUpdated(int bpm) => HeartRateUpdated?.Invoke(bpm);

    private void Session_BatteryUpdated(BatteryState battery) => BatteryUpdated?.Invoke(battery);

    private void Session_ProtocolWarning(string message) => ProtocolWarning?.Invoke(message);

    private void Session_HeartRateProtocolProbePacketObserved(HeartRateProtocolProbePacket packet) =>
        HeartRateProtocolProbePacketObserved?.Invoke(packet);

    private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            ConnectionChanged?.Invoke(false);
        }
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
    }
}
