using ColmiRingVRCBridge.Models;

namespace ColmiRingVRCBridge.Services;

// User intent and retries are serialized on the application's UI context. The BLE
// service owns native handles, link loss and telemetry independently of that context.
internal sealed class RingConnectionController(ColmiRingBleService ble) : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private readonly ConnectionOperationCoordinator _operations = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task _initialization = Task.CompletedTask;
    private Task _retryWorker = Task.CompletedTask;
    private Task _lastSave = Task.CompletedTask;
    private RingDeviceCandidate? _rememberedDevice;
    private bool _paused;
    private bool _closing;

    public event Action? Changed;
    public ColmiRingBleService Ble { get; } = ble;
    public bool AutoReconnect { get; private set; } = true;
    public bool IsReady { get; private set; }
    public ConnectionOperation Operation => _operations.CurrentOperation;
    public bool IsManualBusy => Operation is ConnectionOperation.ManualScan
        or ConnectionOperation.ManualConnect or ConnectionOperation.ManualDisconnect;
    public string Status { get; private set; } = "Loading connection preferences…";
    public RingDeviceCandidate? RememberedDevice => _rememberedDevice;

    public Task InitializeAsync() => _initialization = LoadPreferencesAsync();

    private async Task LoadPreferencesAsync()
    {
        var settings = await Task.Run(ReconnectSettingsStore.Load);
        if (_closing) return;
        AutoReconnect = settings.AutoReconnect;
        if (settings.BluetoothAddress is { } address)
        {
            _rememberedDevice = new RingDeviceCandidate(
                string.IsNullOrWhiteSpace(settings.DeviceName) ? "Last ring" : settings.DeviceName, address, 0);
        }
        IsReady = true;
        SetStatus("Ready");
        _retryWorker = RetryAsync(_shutdown.Token);
    }

    public void SetAutoReconnect(bool enabled)
    {
        if (!IsReady || _closing || AutoReconnect == enabled) return;
        AutoReconnect = enabled;
        if (enabled) _paused = false;
        else _operations.Cancel(ConnectionOperation.AutoReconnect);
        SavePreferences();
        SetStatus(enabled ? "Auto reconnect enabled." : "Auto reconnect disabled.");
    }

    public async Task<IReadOnlyList<RingDeviceCandidate>?> ScanAsync()
    {
        IReadOnlyList<RingDeviceCandidate>? result = null;
        await RunManualAsync(ConnectionOperation.ManualScan, async token =>
        {
            _paused = true;
            try
            {
                SetStatus("Scanning for COLMI / QRing-compatible BLE devices…");
                result = await Ble.ScanAsync(TimeSpan.FromSeconds(6), token);
                token.ThrowIfCancellationRequested();
                SetStatus(result.Count == 0
                    ? "Scan complete: no compatible candidate found."
                    : $"Scan complete: {result.Count} candidate(s) found. Auto reconnect is paused while selecting a device.");
            }
            finally { _paused = result?.Count > 0; }
        });
        return result;
    }

    public Task ConnectAsync(RingDeviceCandidate candidate) =>
        RunManualAsync(ConnectionOperation.ManualConnect, async token =>
        {
            _paused = false;
            await ConnectCoreAsync(candidate, reconnecting: false, token);
        });

    public Task DisconnectAsync() =>
        RunManualAsync(ConnectionOperation.ManualDisconnect, async _ =>
        {
            _paused = true;
            await Ble.DisconnectAsync();
            SetStatus("Ring disconnected. Auto reconnect is paused after a manual disconnect.");
        });

    private async Task RunManualAsync(ConnectionOperation operation, Func<CancellationToken, Task> body)
    {
        if (_closing || !IsReady) return;
        try
        {
            var work = _operations.RunAsync(operation, body, _shutdown.Token);
            Changed?.Invoke();
            await work;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closing) SetStatus(ex.Message); }
        finally { if (!_closing) Changed?.Invoke(); }
    }

    private async Task RetryAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RetryDelay, token);
                if (!AutoReconnect || _paused || Ble.IsConnected || Operation != ConnectionOperation.None ||
                    _rememberedDevice is not { } candidate)
                    continue;

                await _operations.RunAsync(ConnectionOperation.AutoReconnect,
                    attemptToken => ConnectCoreAsync(candidate, reconnecting: true, attemptToken), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_closing && !IsManualBusy) SetStatus($"Auto reconnect: {ex.Message} Retrying in 1 s.");
            }
        }
    }

    private async Task ConnectCoreAsync(RingDeviceCandidate candidate, bool reconnecting, CancellationToken token)
    {
        SetStatus(reconnecting ? "Reconnecting to the remembered ring…" : "Connecting to the selected ring…");
        await Ble.ConnectAsync(candidate, token);
        token.ThrowIfCancellationRequested();
        if (Ble.Snapshot.Device?.BluetoothAddress != candidate.BluetoothAddress)
            throw new InvalidOperationException("The ring disconnected before connection completed.");

        _rememberedDevice = candidate;
        SavePreferences();
        SetStatus(reconnecting ? "Ring reconnected." : "Ring connected. Waiting for heart rate.");
    }

    private void SavePreferences()
    {
        var settings = new ReconnectSettings(_rememberedDevice?.BluetoothAddress, _rememberedDevice?.Name, AutoReconnect);
        _lastSave = SaveAfterAsync(_lastSave, settings);
    }

    private static async Task SaveAfterAsync(Task previous, ReconnectSettings settings)
    {
        await previous.ConfigureAwait(false);
        await Task.Run(() => ReconnectSettingsStore.Save(settings)).ConfigureAwait(false);
    }

    private void SetStatus(string message)
    {
        Status = message;
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _closing = true;
        _shutdown.Cancel();
        try
        {
            await _operations.ShutdownAsync();
            await _retryWorker;
        }
        finally
        {
            try { await Ble.DisposeAsync(); }
            finally
            {
                await _initialization;
                await _lastSave;
                _shutdown.Dispose();
            }
        }
    }
}
