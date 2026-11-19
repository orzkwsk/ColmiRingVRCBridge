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
    private static readonly TimeSpan ScanResolveBudget = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DeviceInfoTimeout = TimeSpan.FromSeconds(3);
    private readonly TimeSpan _connectAttemptTimeout;
    private readonly Func<IBleConnection> _createConnection;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly object _stateGate = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly CancellationToken _shutdownToken;
    private ConnectionLifetime? _connection;
    private ConnectionLifetime? _initializing;
    private Task _lostConnectionCleanup = Task.CompletedTask;
    private long _nextConnectionId;
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
    public event Action? DeviceInfoChanged;
    public event Action<string>? ProtocolWarning;

    public RingConnectionSnapshot Snapshot
    {
        get
        {
            lock (_stateGate)
                return _connection?.Snapshot ?? RingConnectionSnapshot.Disconnected;
        }
    }

    public bool IsConnected => Snapshot.IsConnected;
    public BleTelemetryDiagnostics GetTelemetryDiagnostics()
    {
        lock (_stateGate) return _connection?.Connection.GetDiagnostics() ?? BleTelemetryDiagnostics.Empty;
    }
    public async Task<IReadOnlyList<RingDeviceCandidate>> ScanAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken, cancellationToken);
        await _commands.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            Task cleanup;
            lock (_stateGate) cleanup = _lostConnectionCleanup;
            await cleanup.WaitAsync(linked.Token).ConfigureAwait(false);
            var result = await ScanCoreAsync(duration, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally { _commands.Release(); }
    }

    private static async Task<IReadOnlyList<RingDeviceCandidate>> ScanCoreAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var observed = new ConcurrentDictionary<ulong, (RingDeviceCandidate Device, bool HasUart)>();
        var stopped = new TaskCompletionSource<BluetoothError>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };

        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (args.RawSignalStrengthInDBm == -127)
                return;
            var name = args.Advertisement.LocalName ?? string.Empty;
            var hasKnownService = args.Advertisement.ServiceUuids.Contains(UartServiceUuid);
            var received = new RingDeviceCandidate(name, args.BluetoothAddress, args.RawSignalStrengthInDBm);
            observed.AddOrUpdate(
                args.BluetoothAddress,
                _ => (received, hasKnownService),
                (_, existing) => (string.IsNullOrWhiteSpace(name) ? received with { Name = existing.Device.Name } : received,
                    existing.HasUart || hasKnownService));
        }

        void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args) =>
            stopped.TrySetResult(args.Error);

        watcher.Received += OnReceived;
        watcher.Stopped += OnStopped;
        try
        {
            watcher.Start();
            var delay = Task.Delay(duration, cancellationToken);
            if (await Task.WhenAny(delay, stopped.Task).ConfigureAwait(false) == stopped.Task)
                throw new InvalidOperationException($"Bluetooth scan stopped before completion: {await stopped.Task.ConfigureAwait(false)}.");
            await delay.ConfigureAwait(false);
            if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted)
                throw new InvalidOperationException("Windows aborted the Bluetooth scan.");
        }
        finally
        {
            watcher.Received -= OnReceived;
            watcher.Stopped -= OnStopped;
            watcher.Stop();
        }

        var candidates = new List<RingDeviceCandidate>();
        using var resolveBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        resolveBudget.CancelAfter(ScanResolveBudget);

        foreach (var (observedDevice, hasKnownService) in observed.Values.OrderByDescending(x => x.Device.Rssi))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = observedDevice.Name;
            if (!resolveBudget.IsCancellationRequested && !hasKnownService && !LooksLikeColmiRing(name))
            {
                BluetoothLEDevice? resolvedDevice = null;
                try
                {
                    using var resolveCts = CancellationTokenSource.CreateLinkedTokenSource(resolveBudget.Token);
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

    public async Task ConnectAsync(RingDeviceCandidate candidate, CancellationToken cancellationToken = default)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken, cancellationToken);
        await _commands.WaitAsync(attemptCts.Token).ConfigureAwait(false);
        ConnectionLifetime? attempt = null;
        try
        {
            ThrowIfDisposed();
            await DisconnectCoreAsync().ConfigureAwait(false);
            attemptCts.CancelAfter(_connectAttemptTimeout);
            var token = attemptCts.Token;
            token.ThrowIfCancellationRequested();
            var connection = _createConnection();
            var lifetime = attempt = new ConnectionLifetime(connection, new RingConnectionSnapshot(++_nextConnectionId, candidate));
            lock (_stateGate)
            {
                _initializing = lifetime;
                Hook(connection);
            }
            await StageAsync(() => connection.OpenAsync(candidate.BluetoothAddress, token), token).ConfigureAwait(false);
            await StageAsync(() => connection.DiscoverServiceAsync(token), token).ConfigureAwait(false);
            await StageAsync(() => connection.DiscoverCharacteristicsAsync(token), token).ConfigureAwait(false);
            await StageAsync(() => connection.EnableNotificationsAsync(token), token).ConfigureAwait(false);
            await StageAsync(() => connection.StartSessionAsync(token), token).ConfigureAwait(false);
            lock (_stateGate)
            {
                ThrowIfDisposed();
                token.ThrowIfCancellationRequested();
                if (lifetime.LinkLost || !connection.IsConnected)
                    throw new InvalidOperationException("The BLE link was lost before the session was ready.");
                _initializing = null;
                _connection = lifetime;
                lifetime.DeviceInfoTask = Task.Run(() => ReadDeviceInfoAsync(lifetime));
                attempt = null; // Transfer ownership exactly once, only after initialization.
            }
            var published = Snapshot;
            if (published.Id != lifetime.Snapshot.Id)
                throw new InvalidOperationException("The BLE link was lost while the session was being published.");
            ConnectionChanged?.Invoke(true);
            if (published.Battery is { } battery) BatteryUpdated?.Invoke(battery.Value);
            if (published.HeartRate is { } heartRate) HeartRateUpdated?.Invoke(heartRate.Value);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_shutdownToken.IsCancellationRequested)
        {
            throw new TimeoutException($"BLE/GATT connection attempt exceeded {_connectAttemptTimeout.TotalSeconds:0.###} seconds.");
        }
        finally
        {
            try
            {
                if (attempt is not null)
                {
                    lock (_stateGate)
                    {
                        _initializing = null;
                        Unhook(attempt.Connection);
                    }
                    await DisposeLifetimeAsync(attempt).ConfigureAwait(false);
                }
            }
            finally { _commands.Release(); }
        }
    }

    private static async Task StageAsync(Func<Task> work, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await work().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
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
        ConnectionLifetime? connection;
        Task previousCleanup;
        lock (_stateGate)
        {
            connection = _connection;
            previousCleanup = _lostConnectionCleanup;
            _connection = null;
            if (connection is not null) Unhook(connection.Connection);
        }
        if (connection is not null)
        {
            ConnectionChanged?.Invoke(false);
            await DisposeLifetimeAsync(connection).ConfigureAwait(false);
        }
        await previousCleanup.ConfigureAwait(false);
    }

    private async Task ReadDeviceInfoAsync(ConnectionLifetime lifetime)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Ending.Token);
        deadline.CancelAfter(DeviceInfoTimeout);
        try
        {
            var info = await lifetime.Connection.ReadDeviceInfoAsync(deadline.Token).ConfigureAwait(false);
            lock (_stateGate)
            {
                if (!ReferenceEquals(_connection, lifetime))
                    return;
                lifetime.Snapshot = lifetime.Snapshot with { Info = info };
            }
            DeviceInfoChanged?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Optional BLE device information unavailable: {ex.GetType().Name}");
        }
    }

    private async Task DisposeLifetimeAsync(ConnectionLifetime lifetime)
    {
        try
        {
            try { await lifetime.Ending.CancelAsync().ConfigureAwait(false); }
            finally
            {
                await lifetime.DeviceInfoTask.ConfigureAwait(false);
            }
        }
        finally
        {
            lifetime.Ending.Dispose();
            await lifetime.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Hook(IBleConnection connection)
    {
        connection.HeartRateUpdated += ForwardHeartRate;
        connection.BatteryUpdated += ForwardBattery;
        connection.ConnectionChanged += ForwardConnection;
        connection.ProtocolWarning += ForwardWarning;
    }
    private void Unhook(IBleConnection connection)
    {
        connection.HeartRateUpdated -= ForwardHeartRate;
        connection.BatteryUpdated -= ForwardBattery;
        connection.ConnectionChanged -= ForwardConnection;
        connection.ProtocolWarning -= ForwardWarning;
    }
    private bool IsCurrent(IBleConnection connection)
    {
        lock (_stateGate) return !_disposed && ReferenceEquals(_connection?.Connection, connection);
    }

    private ConnectionLifetime? FindLifetime(IBleConnection connection) =>
        ReferenceEquals(_connection?.Connection, connection) ? _connection :
        ReferenceEquals(_initializing?.Connection, connection) ? _initializing : null;

    private void ForwardHeartRate(IBleConnection connection, int bpm)
    {
        bool published;
        lock (_stateGate)
        {
            if (_disposed || FindLifetime(connection) is not { LinkLost: false } lifetime)
                return;
            lifetime.Snapshot = lifetime.Snapshot with { HeartRate = RingReading<int>.Capture(bpm) };
            published = ReferenceEquals(lifetime, _connection);
        }
        if (published) HeartRateUpdated?.Invoke(bpm);
    }

    private void ForwardBattery(IBleConnection connection, BatteryState battery)
    {
        bool published;
        lock (_stateGate)
        {
            if (_disposed || FindLifetime(connection) is not { LinkLost: false } lifetime)
                return;
            lifetime.Snapshot = lifetime.Snapshot with { Battery = RingReading<BatteryState>.Capture(battery) };
            published = ReferenceEquals(lifetime, _connection);
        }
        if (published) BatteryUpdated?.Invoke(battery);
    }

    private void ForwardConnection(IBleConnection connection, bool connected)
    {
        if (connected) return;
        lock (_stateGate)
        {
            if (_disposed || FindLifetime(connection) is not { } lifetime)
                return;
            lifetime.LinkLost = true;
            if (!ReferenceEquals(lifetime, _connection))
                return;
            _connection = null;
            Unhook(connection);
            _lostConnectionCleanup = Task.Run(async () =>
            {
                try { await DisposeLifetimeAsync(lifetime).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Disconnected BLE cleanup failed: {ex.GetType().Name}");
                }
            });
        }
        ConnectionChanged?.Invoke(false);
    }
    private void ForwardWarning(IBleConnection connection, string warning) { if (IsCurrent(connection)) ProtocolWarning?.Invoke(warning); }
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

    private sealed class ConnectionLifetime(IBleConnection connection, RingConnectionSnapshot snapshot)
    {
        public IBleConnection Connection { get; } = connection;
        public CancellationTokenSource Ending { get; } = new();
        public RingConnectionSnapshot Snapshot { get; set; } = snapshot;
        public Task DeviceInfoTask { get; set; } = Task.CompletedTask;
        public bool LinkLost { get; set; }
    }
}
