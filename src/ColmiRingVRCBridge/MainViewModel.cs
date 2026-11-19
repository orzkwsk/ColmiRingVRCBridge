using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Windows.Input;
using ColmiRingVRCBridge.Models;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge;

internal sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private static readonly TimeSpan HeartRateFreshness = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BatteryFreshness = TimeSpan.FromMinutes(3);
    private readonly RingConnectionController _connection;
    private readonly OscOutputService _osc;
    private readonly OscSettingsStore _oscSettingsStore;
    private readonly OscSettings _loadedOscSettings;
    private readonly Action<Action> _post;
    private readonly List<AsyncCommand> _commands = [];
    private readonly HeartRateHistoryBuffer _heartRateHistory = new(TimeSpan.FromMinutes(5));
    private readonly Dictionary<ulong, BatteryHistoryBuffer> _batteryHistories = [];
    private RingConnectionSnapshot _display = RingConnectionSnapshot.Disconnected;
    private IReadOnlyList<RingDeviceCandidate> _devices = [];
    private RingDeviceCandidate? _selectedDevice;
    private ulong? _observedAddress;
    private int? _minimum;
    private int? _maximum;
    private BatteryHistoryBuffer? _batteryHistory;
    private bool _closing;
    private string? _message;
    private string? _bleWarning;

    private OscSettings _oscSettings;
    private VrcOutputSettings _vrcOutput;
    private long _oscRun;
    private bool _oscTransition;
    private string? _oscError;
    private string _lastOutput = "最終送信 ―";

    public MainViewModel(RingConnectionController connection, OscOutputService osc, Action<Action> post,
        OscSettingsStore? oscSettingsStore = null)
    {
        _connection = connection;
        _osc = osc;
        _post = post;
        _oscSettingsStore = oscSettingsStore ?? OscSettingsStore.Local;
        _loadedOscSettings = _oscSettings = _oscSettingsStore.Load();
        _vrcOutput = _oscSettings.ToVrcOutputSettings();
        ScanCommand = Command(ScanAsync, () => _connection.IsReady && !IsConnected && !ConnectionBusy);
        ConnectionCommand = Command(ToggleConnectionAsync,
            () => _connection.IsReady && !ConnectionBusy && (IsConnected || SelectedDevice is not null));
        OutputCommand = Command(ToggleOutputAsync, () => !_oscTransition);
        ResetOscCommand = Command(() =>
        {
            _message = "OSCの接続先を初期値に戻しました。";
            UpdateOscSettings(_oscSettings with { Host = OscSettings.DefaultHost, Port = OscSettings.DefaultPort });
            return Task.CompletedTask;
        }, () => CanEditOsc);
        ResetHeartRateCommand = Command(() =>
        {
            ResetHeartRate();
            _message = "Heart-rate statistics and graph history reset.";
            Refresh();
            return Task.CompletedTask;
        });

        _connection.Changed += HandleConnectionIntent;
        _connection.Ble.ConnectionChanged += HandleLink;
        _connection.Ble.HeartRateUpdated += HandleHeartRate;
        _connection.Ble.BatteryUpdated += HandleBattery;
        _connection.Ble.DeviceInfoChanged += PostRefresh;
        _connection.Ble.ProtocolWarning += HandleWarning;
        _osc.OutputSent += HandleOutput;
        _osc.OutputError += HandleOutputError;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ICommand ScanCommand { get; }
    public ICommand ConnectionCommand { get; }
    public ICommand OutputCommand { get; }
    public ICommand ResetOscCommand { get; }
    public ICommand ResetHeartRateCommand { get; }
    public IReadOnlyList<RingDeviceCandidate> Devices => _devices;
    public RingDeviceCandidate? SelectedDevice
    {
        get => _selectedDevice;
        set { if (_selectedDevice != value) { _selectedDevice = value; Refresh(); } }
    }

    private bool IsConnected => _display.IsConnected;
    private bool ConnectionBusy => _connection.IsManualBusy;
    public bool CanSelectDevice => _connection.IsReady && !IsConnected && !ConnectionBusy && !_closing;
    public bool ShowDevicePicker => !IsConnected;
    public string ConnectedDeviceName => _display.Device?.Name ?? "—";
    public bool CanChangeAutoReconnect => _connection.IsReady && !_closing;
    public bool AutoReconnect { get => _connection.AutoReconnect; set => _connection.SetAutoReconnect(value); }
    public string ConnectionButtonText => IsConnected ? "切断" : "接続";
    public string Status => _message ?? _connection.Status;
    public string Version => "v" + (typeof(MainViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "");
    public string ConnectionText => _display.GetTelemetryState(HeartRateFreshness) switch
    {
        HeartRateTelemetryState.Streaming => "接続済み",
        HeartRateTelemetryState.Stale => "接続済み・心拍更新停止",
        HeartRateTelemetryState.Initializing => "接続済み・心拍待ち",
        _ when _connection.Operation == ConnectionOperation.AutoReconnect => "再接続中",
        _ when _connection.Operation == ConnectionOperation.ManualConnect => "接続中",
        _ => "未接続"
    };
    public string DeviceAddress => _display.Device?.AddressText ?? "—";
    public string DeviceModel => _display.Info is { } info ? $"Serial: {info.Serial ?? "—"}    Model: {info.Model ?? "—"}" : "—";
    public string DeviceFirmware => _display.Info is { } info ? $"HW: {info.HardwareVersion ?? "—"}    FW: {info.FirmwareVersion ?? "—"}" : "—";
    public string HeartRate => BpmText(_display.HeartRate?.Value);
    public string LastHeartRate => _display.HeartRate?.ReceivedAt.LocalDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "—";
    public string MinimumHeartRate => BpmText(_minimum);
    public string MaximumHeartRate => BpmText(_maximum);
    public string Battery => _display.Battery is { } battery ? $"{battery.Value.Percent}%{BatterySuffix}" : "—%";
    public string Charging => _display.Battery is { } battery ? (battery.Value.Charging ? "Yes" : "No") + BatterySuffix : "—";
    private string BatterySuffix => _display.Battery?.IsFresh(BatteryFreshness) == false ? " (stale)" : "";

    public string ConnectionDetails
    {
        get
        {
            var counts = _connection.Ble.GetTelemetryDiagnostics();
            return $"Telemetry: {_display.GetTelemetryState(HeartRateFreshness)}\n" +
                   $"HR packets: {counts.HeartRatePacketCount}    Battery packets: {counts.BatteryPacketCount}\n" +
                   $"HR polls: {counts.HeartRatePollTxCount}    Failed: {counts.HeartRatePollWriteFailureCount}\n" +
                   $"Battery polls: {counts.BatteryPollTxCount}    Failed: {counts.BatteryPollWriteFailureCount}\n" +
                   $"Valid / invalid packets: {counts.ValidPacketCount} / {counts.InvalidPacketCount}" +
                   (_bleWarning is not null ? $"\nLast BLE warning: {_bleWarning}" : "");
        }
    }

    public string ParameterName { get => _oscSettings.ParameterName; set => UpdateOscSettings(_oscSettings with { ParameterName = value }); }
    public string OscAddress => OscOutputOptions.FormatAddress(ParameterName);
    public string OscHost { get => _oscSettings.Host; set => UpdateOscSettings(_oscSettings with { Host = value }); }
    public string OscPort { get => _oscSettings.Port; set => UpdateOscSettings(_oscSettings with { Port = value }); }
    public string OscEndpoint => $"{OscHost}:{OscPort}";
    public string OscInterval { get => _oscSettings.Interval; set => UpdateOscSettings(_oscSettings with { Interval = value }); }
    public OscValueType ValueType
    {
        get => _oscSettings.ValueType;
        set => UpdateOscSettings(_oscSettings with
        {
            ValueType = value,
            Scaling = value == OscValueType.Int ? ScalingMode.RawBpm : Scaling
        });
    }
    public ScalingMode Scaling { get => _oscSettings.Scaling; set => UpdateOscSettings(_oscSettings with { Scaling = value }); }
    public bool CanEditOsc => !_osc.IsRunning && !_oscTransition && !_closing;
    public bool CanEditScaling => CanEditOsc && ValueType == OscValueType.Float;
    public bool UseFixedBpm
    {
        get => _oscSettings.UseFixedBpm;
        set => UpdateOscSettings(_oscSettings with { UseFixedBpm = value });
    }
    public string FixedBpm
    {
        get => _oscSettings.FixedBpm;
        set => UpdateOscSettings(_oscSettings with { FixedBpm = value });
    }
    public HeartRateLossMode LossMode
    {
        get => _oscSettings.LossMode;
        set => UpdateOscSettings(_oscSettings with { LossMode = value });
    }
    public bool ShowSpecifiedValue => LossMode == HeartRateLossMode.UseSpecifiedValue;
    public string SpecifiedValue
    {
        get => _oscSettings.SpecifiedValue;
        set => UpdateOscSettings(_oscSettings with { SpecifiedValue = value });
    }
    public string OutputButtonText => _osc.IsRunning ? "送信停止" : "送信開始";
    public string OutputState
    {
        get
        {
            if (_oscError is not null) return _oscError;
            if (_oscTransition) return "送信停止処理中";
            if (!_osc.IsRunning) return "送信停止中";
            if (UseFixedBpm) return _vrcOutput.FixedBpm is null ? "一時停止・固定BPMを入力" : "送信中・固定BPM";
            if (GetFreshBpm() is not null) return "送信中・実測";
            return LossMode switch
            {
                HeartRateLossMode.HoldLast => _osc.LastSent is null ? "一時停止・心拍待ち" : "心拍途絶・最終値を保持",
                HeartRateLossMode.UseFixedBpm => _vrcOutput.FixedBpm is null
                    ? "一時停止・固定BPMを入力" : "心拍途絶・固定BPMを送信",
                HeartRateLossMode.UseSpecifiedValue => _vrcOutput.SpecifiedValue is null
                    ? "一時停止・途絶時の指定値を入力" : "心拍途絶・指定値を送信",
                _ => "一時停止・心拍待ち"
            };
        }
    }
    public string LastOutput => _lastOutput;

    public async Task InitializeAsync()
    {
        await _connection.InitializeAsync();
        if (_closing) return;
        if (_connection.RememberedDevice is { } device)
            SetDevices([device]);
        else Refresh();
    }

    public IReadOnlyList<HeartRateSample> HeartRateSamples(DateTimeOffset since) => _heartRateHistory.Snapshot(since);
    public IReadOnlyList<BatteryHistorySample> BatterySamples(DateTimeOffset now) => _batteryHistory?.Snapshot(now) ?? [];

    public void Refresh()
    {
        if (_closing) return;
        var snapshot = _connection.Ble.Snapshot;
        if (snapshot.Id != _display.Id) _bleWarning = null;
        if (snapshot.Device is { } device && _observedAddress != device.BluetoothAddress)
        {
            ResetHeartRate();
            _observedAddress = device.BluetoothAddress;
            if (!_batteryHistories.TryGetValue(device.BluetoothAddress, out _batteryHistory))
            {
                _batteryHistory = new BatteryHistoryBuffer();
                _batteryHistories.Add(device.BluetoothAddress, _batteryHistory);
            }
        }
        if (snapshot.HeartRate is { } hr && !ReferenceEquals(hr, _display.HeartRate))
        {
            _heartRateHistory.Add(hr.Value, hr.ReceivedAt);
            _minimum = Math.Min(_minimum ?? hr.Value, hr.Value);
            _maximum = Math.Max(_maximum ?? hr.Value, hr.Value);
        }
        if (snapshot.Battery is { } battery && !ReferenceEquals(battery, _display.Battery))
            _batteryHistory?.Add(battery.Value, battery.ReceivedAt);
        _display = snapshot;
        Notify();
    }

    private async Task ScanAsync()
    {
        if (await _connection.ScanAsync() is not { } devices || _closing) return;
        SetDevices(devices);
    }

    private void SetDevices(IReadOnlyList<RingDeviceCandidate> devices)
    {
        _devices = devices;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Devices)));
        _selectedDevice = devices.FirstOrDefault();
        Refresh();
    }

    private async Task ToggleConnectionAsync()
    {
        if (_connection.Ble.IsConnected)
            await _connection.DisconnectAsync();
        else if (SelectedDevice is { } device)
        {
            ResetHeartRate();
            await _connection.ConnectAsync(device);
        }
        Refresh();
    }

    private async Task ToggleOutputAsync()
    {
        if (_osc.IsRunning) { await StopOutputAsync(); return; }
        var options = OscOutputOptions.FromInput(OscHost, OscPort, ParameterName, ValueType, Scaling, OscInterval);
        if ((UseFixedBpm || LossMode == HeartRateLossMode.UseFixedBpm) && _vrcOutput.FixedBpm is null)
            throw new ArgumentException("固定BPMに整数を入力してください。");
        if (!UseFixedBpm && ShowSpecifiedValue && _vrcOutput.SpecifiedValue is null)
            throw new ArgumentException("途絶時の指定値を、選択したOSC型に合う数値で入力してください。");
        Interlocked.Increment(ref _oscRun);
        _osc.Start(options,
            lastSent => Volatile.Read(ref _vrcOutput).Select(options.Scaling, GetFreshBpm(), lastSent),
            lastSent => Volatile.Read(ref _vrcOutput).SelectForLoss(options.Scaling, lastSent));
        _oscError = null;
        _message = null;
        _lastOutput = "最終送信 ―";
        Refresh();
    }

    private async Task StopOutputAsync(string? error = null)
    {
        if (_oscTransition) return;
        _oscTransition = true;
        _oscError = error;
        Refresh();
        try { await _osc.StopAsync(); }
        finally
        {
            Interlocked.Increment(ref _oscRun);
            _oscTransition = false;
            Refresh();
        }
    }

    private int? GetFreshBpm()
    {
        var heartRate = _connection.Ble.Snapshot.HeartRate;
        return heartRate?.IsFresh(HeartRateFreshness) == true ? heartRate.Value : null;
    }

    private void UpdateOscSettings(OscSettings settings)
    {
        if (_oscSettings == settings) return;
        _oscSettings = settings;
        Volatile.Write(ref _vrcOutput, settings.ToVrcOutputSettings());
        Refresh();
    }

    private void HandleOutput(OscOutputValue output)
    {
        var run = Interlocked.Read(ref _oscRun);
        _post(() =>
        {
            if (_closing || run != _oscRun) return;
            var value = output.Value.ToString("G7", CultureInfo.InvariantCulture);
            var bpm = output.Bpm is int source ? $"{source} BPM → " : "";
            _lastOutput = $"最終送信 {bpm}{value} · {DateTime.Now:HH:mm:ss}";
            Refresh();
        });
    }

    private void HandleOutputError(Exception error)
    {
        var run = Interlocked.Read(ref _oscRun);
        _post(async () =>
        {
            if (_closing || run != _oscRun) return;
            var message = $"OSC output error: {error.Message}";
            if (_oscTransition)
            {
                _oscError = message;
                Refresh();
                return;
            }
            try { await StopOutputAsync(message); }
            catch (Exception ex) { _oscError = $"OSC output error: {ex.Message}"; Refresh(); }
        });
    }

    private void PostRefresh() => _post(Refresh);
    private void HandleLink(bool _) => PostRefresh();
    private void HandleHeartRate(int _) => PostRefresh();
    private void HandleBattery(BatteryState _) => PostRefresh();
    private void HandleConnectionIntent() { _message = null; Refresh(); }
    private void HandleWarning(string warning)
    {
        var session = _connection.Ble.Snapshot.Id;
        _post(() =>
        {
            if (_closing || session == 0 || session != _connection.Ble.Snapshot.Id) return;
            _bleWarning = warning;
            Refresh();
        });
    }

    private void ResetHeartRate()
    {
        _heartRateHistory.Clear();
        _minimum = null;
        _maximum = null;
    }

    private static string BpmText(int? bpm) => bpm.HasValue ? $"{bpm} BPM" : "— BPM";
    private AsyncCommand Command(Func<Task> execute, Func<bool>? enabled = null)
    {
        var command = new AsyncCommand(execute, () => !_closing && (enabled?.Invoke() ?? true),
            ex => { _message = ex.Message; Refresh(); });
        _commands.Add(command);
        return command;
    }

    private void Notify()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        foreach (var command in _commands) command.Refresh();
    }

    public async ValueTask DisposeAsync()
    {
        _closing = true;
        _connection.Changed -= HandleConnectionIntent;
        _connection.Ble.ConnectionChanged -= HandleLink;
        _connection.Ble.HeartRateUpdated -= HandleHeartRate;
        _connection.Ble.BatteryUpdated -= HandleBattery;
        _connection.Ble.DeviceInfoChanged -= PostRefresh;
        _connection.Ble.ProtocolWarning -= HandleWarning;
        _osc.OutputSent -= HandleOutput;
        _osc.OutputError -= HandleOutputError;
        var settings = _oscSettings;
        var saveSettings = settings == _loadedOscSettings ? Task.CompletedTask
            : Task.Run(() => _oscSettingsStore.Save(settings));
        try { await _osc.DisposeAsync(); }
        finally
        {
            try { await _connection.DisposeAsync(); }
            finally
            {
                await saveSettings;
            }
        }
    }

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> enabled, Action<Exception> failed) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => enabled();
        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter)) return;
            try { await execute(); }
            catch (Exception ex) { failed(ex); }
        }
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
