using System.Collections.Concurrent;
using ColmiRingVRCBridge.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace ColmiRingVRCBridge.Services;

internal sealed class ColmiRingBleService : IAsyncDisposable
{
    public static readonly Guid UartServiceUuid = Guid.Parse("6E40FFF0-B5A3-F393-E0A9-E50E24DCCA9E");
    public static readonly Guid UartRxUuid = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E");
    public static readonly Guid UartTxUuid = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly TimeSpan ScanResolveTimeout = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _connectAttemptTimeout;
    private readonly Func<IBleConnection> _createConnection;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly CancellationToken _shutdownToken;
    private IBleConnection? _connection;
    private bool _disposed;
    private TaskCompletionSource? _disposeCompletion;

    public ColmiRingBleService() : this(() => new WinRtBleConnection(), TimeSpan.FromSeconds(15)) { }
    internal ColmiRingBleService(Func<IBleConnection> createConnection, TimeSpan connectAttemptTimeout)
    {
        _createConnection = createConnection;
        _connectAttemptTimeout = connectAttemptTimeout;
        _shutdownToken = _shutdownCts.Token;
    }

    public event Action<int>? HeartRateUpdated;
    public event Action<BatteryState>? BatteryUpdated;
    public event Action<bool>? ConnectionChanged;
    public event Action<string>? ProtocolWarning;
    public event Action<string>? ConnectionProgress;
    public event Action<HeartRateProtocolProbePacket>? HeartRateProtocolProbePacketObserved;

    public bool IsConnected { get { lock (_stateGate) return _connection?.IsConnected == true; } }
    public BleTelemetryDiagnostics GetTelemetryDiagnostics()
    {
        lock (_stateGate) return _connection?.GetDiagnostics() ?? BleTelemetryDiagnostics.Empty;
    }
    public HeartRateTelemetryState GetTelemetryState(DateTimeOffset now, TimeSpan staleAfter)
    {
        lock (_stateGate) return _connection?.IsConnected == true
            ? _connection.GetTelemetryState(now, staleAfter) : HeartRateTelemetryState.Disconnected;
    }

    public async Task<IReadOnlyList<RingDeviceCandidate>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken, cancellationToken);
        await _commands.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ReportConnectionProgress("scan_start");
            var result = await ScanCoreAsync(duration, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            ReportConnectionProgress("scan_complete");
            return result;
        }
        finally { _commands.Release(); }
    }

    private static async Task<IReadOnlyList<RingDeviceCandidate>> ScanCoreAsync(TimeSpan duration, CancellationToken cancellationToken = default)
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
        try
        {
            watcher.Start();
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
                    using var resolveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    resolveCts.CancelAfter(ScanResolveTimeout);

                    resolvedDevice = await BoundedBleOperation.AwaitAsync(
                        BluetoothLEDevice.FromBluetoothAddressAsync(observedDevice.BluetoothAddress).AsTask(resolveCts.Token),
                        resolveCts.Token, device => device?.Dispose()).ConfigureAwait(false);
                    if (resolvedDevice is not null && !string.IsNullOrWhiteSpace(resolvedDevice.Name))
                    {
                        name = resolvedDevice.Name;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Name resolution is best-effort. Do not let one Windows BLE
                    // device-open stall hold the manual scan UI indefinitely.
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
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

    public async Task<RingDeviceInfo> ConnectAsync(RingDeviceCandidate candidate, CancellationToken cancellationToken = default)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken, cancellationToken);
        await _commands.WaitAsync(attemptCts.Token).ConfigureAwait(false);
        IBleConnection? attempt = null;
        try
        {
            ThrowIfDisposed();
            await DisconnectCoreAsync().ConfigureAwait(false);
            attemptCts.CancelAfter(_connectAttemptTimeout);
            var token = attemptCts.Token;
            token.ThrowIfCancellationRequested();
            attempt = _createConnection();
            await StageAsync("device_open", () => attempt.OpenAsync(candidate.BluetoothAddress, token), token).ConfigureAwait(false);
            await StageAsync("uart_service_discovery", () => attempt.DiscoverServiceAsync(token), token).ConfigureAwait(false);
            await StageAsync("characteristic_discovery", () => attempt.DiscoverCharacteristicsAsync(token), token).ConfigureAwait(false);
            ReportConnectionProgress("device_info_read_start");
            var info = await attempt.ReadDeviceInfoAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ReportConnectionProgress("device_info_read_complete");
            await StageAsync("notification_enable", () => attempt.EnableNotificationsAsync(token), token).ConfigureAwait(false);
            await StageAsync("protocol_session", () => attempt.StartSessionAsync(token), token).ConfigureAwait(false);
            lock (_stateGate)
            {
                ThrowIfDisposed();
                token.ThrowIfCancellationRequested();
                Hook(attempt);
                _connection = attempt;
                attempt = null; // Transfer ownership exactly once, only after initialization.
            }
            ReportConnectionProgress("protocol_session_ready");
            ConnectionChanged?.Invoke(true);
            return info;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_shutdownToken.IsCancellationRequested)
        {
            ReportConnectionProgress("connect_timeout");
            throw new TimeoutException($"BLE/GATT connection attempt exceeded {_connectAttemptTimeout.TotalSeconds:0.###} seconds.");
        }
        catch (OperationCanceledException)
        {
            ReportConnectionProgress("connect_cancelled");
            throw;
        }
        finally
        {
            try
            {
                if (attempt is not null) await attempt.DisposeAsync().ConfigureAwait(false);
            }
            finally { _commands.Release(); }
        }
    }

    private async Task StageAsync(string stage, Func<Task> work, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ReportConnectionProgress($"{stage}_start");
        await work().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        ReportConnectionProgress($"{stage}_complete");
    }

    public async Task DisconnectAsync()
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        await _commands.WaitAsync(linked.Token).ConfigureAwait(false);
        try { ThrowIfDisposed(); await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _commands.Release(); }
    }

    private async Task DisconnectCoreAsync()
    {
        IBleConnection? connection;
        lock (_stateGate)
        {
            connection = _connection;
            _connection = null;
            if (connection is not null) Unhook(connection);
        }
        if (connection is not null)
        {
            ReportConnectionProgress("session_cleanup_start");
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            finally { ReportConnectionProgress("session_cleanup_complete"); }
        }
        ConnectionChanged?.Invoke(false);
    }

    public async Task RebootAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken, cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(5));
        await _commands.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var connection = _connection;
            if (connection?.IsConnected != true) throw new InvalidOperationException("Ring is not connected.");
            ReportConnectionProgress("ring_reboot_command");
            await connection.RebootAsync(linked.Token).ConfigureAwait(false);
        }
        finally { _commands.Release(); }
    }

    private void Hook(IBleConnection connection)
    {
        connection.HeartRateUpdated += ForwardHeartRate;
        connection.BatteryUpdated += ForwardBattery;
        connection.ConnectionChanged += ForwardConnection;
        connection.ProtocolWarning += ForwardWarning;
        connection.ProbeObserved += ForwardProbe;
    }
    private void Unhook(IBleConnection connection)
    {
        connection.HeartRateUpdated -= ForwardHeartRate;
        connection.BatteryUpdated -= ForwardBattery;
        connection.ConnectionChanged -= ForwardConnection;
        connection.ProtocolWarning -= ForwardWarning;
        connection.ProbeObserved -= ForwardProbe;
    }
    private bool IsCurrent(IBleConnection connection)
    {
        lock (_stateGate) return !_disposed && ReferenceEquals(_connection, connection);
    }
    private void ForwardHeartRate(IBleConnection connection, int bpm) { if (IsCurrent(connection)) HeartRateUpdated?.Invoke(bpm); }
    private void ForwardBattery(IBleConnection connection, BatteryState battery) { if (IsCurrent(connection)) BatteryUpdated?.Invoke(battery); }
    private void ForwardConnection(IBleConnection connection, bool connected) { if (IsCurrent(connection)) ConnectionChanged?.Invoke(connected); }
    private void ForwardWarning(IBleConnection connection, string warning) { if (IsCurrent(connection)) ProtocolWarning?.Invoke(warning); }
    private void ForwardProbe(IBleConnection connection, HeartRateProtocolProbePacket packet) { if (IsCurrent(connection)) HeartRateProtocolProbePacketObserved?.Invoke(packet); }
    private void ReportConnectionProgress(string stage) => ConnectionProgress?.Invoke(stage);
    private void ThrowIfDisposed()
    {
        lock (_stateGate) ObjectDisposedException.ThrowIf(_disposed, this);
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
        TaskCompletionSource completion;
        var owner = false;
        lock (_stateGate)
        {
            if (_disposeCompletion is null)
            {
                _disposed = true;
                _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                owner = true;
            }
            completion = _disposeCompletion;
        }
        if (owner)
        {
            try
            {
                try { _shutdownCts.Cancel(); }
                finally
                {
                    await _commands.WaitAsync().ConfigureAwait(false);
                    try { await DisconnectCoreAsync().ConfigureAwait(false); }
                    finally { _commands.Release(); _shutdownCts.Dispose(); }
                }
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }
        await completion.Task.ConfigureAwait(false);
    }
}
