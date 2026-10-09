using ColmiRingVRCBridge.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ColmiRingVRCBridge.Services;

internal sealed class WinRtBleConnection : IBleConnection
{
    private static readonly Guid DeviceInfoUuid = Guid.Parse("0000180A-0000-1000-8000-00805F9B34FB");
    private BluetoothLEDevice? _device;
    private GattDeviceService? _uartService;
    private GattColmiTransport? _transport;
    private R06Session? _session;
    private volatile bool _disposed;

    public event Action<IBleConnection, int>? HeartRateUpdated;
    public event Action<IBleConnection, BatteryState>? BatteryUpdated;
    public event Action<IBleConnection, bool>? ConnectionChanged;
    public event Action<IBleConnection, string>? ProtocolWarning;
    public event Action<IBleConnection, HeartRateProtocolProbePacket>? ProbeObserved;
    public bool IsConnected => !_disposed && _device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
    public BleTelemetryDiagnostics GetDiagnostics() => _session?.GetDiagnostics() ?? BleTelemetryDiagnostics.Empty;
    public HeartRateTelemetryState GetTelemetryState(DateTimeOffset now, TimeSpan staleAfter) =>
        _session?.GetTelemetryState(IsConnected, now, staleAfter) ?? HeartRateTelemetryState.Initializing;

    public async Task OpenAsync(ulong address, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _device = await BoundedBleOperation.AwaitAsync(
            BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(token), token,
            device => device?.Dispose()).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Windows could not open the selected BLE device.");
        _device.ConnectionStatusChanged += DeviceConnectionChanged;
    }

    public async Task DiscoverServiceAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await BoundedBleOperation.AwaitAsync(
            _device!.GetGattServicesForUuidAsync(ColmiRingBleService.UartServiceUuid, BluetoothCacheMode.Uncached)
                .AsTask(token), token, DisposeServices).ConfigureAwait(false);
        if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
        {
            DisposeServices(result);
            throw new InvalidOperationException("COLMI UART GATT service was not found on the selected device.");
        }
        _uartService = result.Services[0];
        foreach (var unused in result.Services.Skip(1)) unused.Dispose();
    }

    public async Task DiscoverCharacteristicsAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var rx = await BoundedBleOperation.AwaitAsync(
            _uartService!.GetCharacteristicsForUuidAsync(ColmiRingBleService.UartRxUuid, BluetoothCacheMode.Uncached)
                .AsTask(token), token).ConfigureAwait(false);
        var tx = await BoundedBleOperation.AwaitAsync(
            _uartService.GetCharacteristicsForUuidAsync(ColmiRingBleService.UartTxUuid, BluetoothCacheMode.Uncached)
                .AsTask(token), token).ConfigureAwait(false);
        if (rx.Status != GattCommunicationStatus.Success || rx.Characteristics.Count == 0 ||
            tx.Status != GattCommunicationStatus.Success || tx.Characteristics.Count == 0)
            throw new InvalidOperationException("COLMI UART RX/TX characteristics were not found.");
        _transport = new GattColmiTransport(rx.Characteristics[0], tx.Characteristics[0]);
    }

    public async Task<RingDeviceInfo> ReadDeviceInfoAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await BoundedBleOperation.AwaitAsync(
            _device!.GetGattServicesForUuidAsync(DeviceInfoUuid, BluetoothCacheMode.Uncached).AsTask(token),
            token, DisposeServices).ConfigureAwait(false);
        if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
        {
            DisposeServices(result);
            return new RingDeviceInfo(null, null, null, null);
        }
        using var service = result.Services[0];
        foreach (var unused in result.Services.Skip(1)) unused.Dispose();
        var model = await ReadStringAsync(service, "00002A24", token).ConfigureAwait(false);
        var serial = await ReadStringAsync(service, "00002A25", token).ConfigureAwait(false);
        var hardware = await ReadStringAsync(service, "00002A27", token).ConfigureAwait(false);
        var firmware = await ReadStringAsync(service, "00002A26", token).ConfigureAwait(false);
        return new RingDeviceInfo(model, serial, hardware, firmware);
    }

    private static async Task<string?> ReadStringAsync(GattDeviceService service, string id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await BoundedBleOperation.AwaitAsync(service.GetCharacteristicsForUuidAsync(
            Guid.Parse($"{id}-0000-1000-8000-00805F9B34FB"), BluetoothCacheMode.Uncached).AsTask(token), token)
            .ConfigureAwait(false);
        if (result.Status != GattCommunicationStatus.Success || result.Characteristics.Count == 0) return null;
        var read = await BoundedBleOperation.AwaitAsync(
            result.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(token), token)
            .ConfigureAwait(false);
        if (read.Status != GattCommunicationStatus.Success) return null;
        using var reader = DataReader.FromBuffer(read.Value);
        var data = new byte[(int)reader.UnconsumedBufferLength];
        reader.ReadBytes(data);
        return System.Text.Encoding.UTF8.GetString(data).TrimEnd('\0').Trim();
    }

    public Task EnableNotificationsAsync(CancellationToken token) => _transport!.StartAsync(token);

    public async Task StartSessionAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var session = _session = new R06Session(_transport!);
        session.HeartRateUpdated += SessionHeartRate;
        session.BatteryUpdated += SessionBattery;
        session.ProtocolWarning += SessionWarning;
        session.HeartRateProtocolProbePacketObserved += SessionProbe;
        await session.StartAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    public Task RebootAsync(CancellationToken token) => _session!.RebootAsync(token);
    private void SessionHeartRate(int bpm) => HeartRateUpdated?.Invoke(this, bpm);
    private void SessionBattery(BatteryState battery) => BatteryUpdated?.Invoke(this, battery);
    private void SessionWarning(string warning) => ProtocolWarning?.Invoke(this, warning);
    private void SessionProbe(HeartRateProtocolProbePacket packet) => ProbeObserved?.Invoke(this, packet);
    private void DeviceConnectionChanged(BluetoothLEDevice sender, object args)
    {
        if (!_disposed && ReferenceEquals(sender, _device) && sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            ConnectionChanged?.Invoke(this, false);
    }

    private static void DisposeServices(GattDeviceServicesResult result)
    {
        foreach (var service in result.Services) service.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_device is not null) _device.ConnectionStatusChanged -= DeviceConnectionChanged;
        try
        {
            if (_session is not null)
            {
                _session.HeartRateUpdated -= SessionHeartRate;
                _session.BatteryUpdated -= SessionBattery;
                _session.ProtocolWarning -= SessionWarning;
                _session.HeartRateProtocolProbePacketObserved -= SessionProbe;
                await _session.DisposeAsync().ConfigureAwait(false);
            }
            else if (_transport is not null) await _transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try { _uartService?.Dispose(); }
            finally
            {
                try { _device?.Dispose(); }
                finally
                {
                    _session = null;
                    _transport = null;
                    _uartService = null;
                    _device = null;
                }
            }
        }
    }
}
