using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ColmiRingVRCBridge.Models;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge;

public partial class MainWindow : Window
{
    private static readonly Regex IntegerRegex = new("^[0-9]+$");
    private static readonly TimeSpan HeartRateGraphWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HeartRateHistoryRetention = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HeartRateGraphGapThreshold = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan HeartRateFreshnessThreshold = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BatteryHistoryRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan BatteryGraphGapThreshold = TimeSpan.FromMinutes(30);

    private readonly ColmiRingBleService _ringService = new();
    private readonly OscOutputService _oscOutputService = new();
    private readonly HeartRateHistoryBuffer _heartRateHistory = new(HeartRateHistoryRetention);
    private readonly DispatcherTimer _heartRateGraphTimer;
    private readonly object _heartRateStatsGate = new();

    private BatteryHistoryStore? _batteryHistory;
    private ToolTip? _batteryHistoryToolTip;
    private Canvas? _batteryChargingIntervalsCanvas;
    private Canvas? _batteryHistoryGraphCanvas;
    private System.Windows.Shapes.Path? _batteryHistoryGraphPath;
    private TextBlock? _batteryHistoryEmptyText;
    private TextBlock? _batteryHistorySummaryText;

    private HeartRateSnapshot _heartRateSnapshot = HeartRateSnapshot.Empty;
    private int? _minimumHeartRate;
    private int? _maximumHeartRate;
    private bool _dummyEnabled;
    private int _dummyBpm = 72;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        InitializeBatteryHistoryToolTip();

        _ringService.HeartRateUpdated += RingService_HeartRateUpdated;
        _ringService.BatteryUpdated += RingService_BatteryUpdated;
        _ringService.ConnectionChanged += RingService_ConnectionChanged;
        _ringService.ProtocolWarning += RingService_ProtocolWarning;

        _oscOutputService.OutputSent += OscOutputService_OutputSent;
        _oscOutputService.OutputError += OscOutputService_OutputError;

        _heartRateGraphTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _heartRateGraphTimer.Tick += (_, _) => RedrawHeartRateGraph();
        _heartRateGraphTimer.Tick += HeartRateGraphTimer_DiagnosticsTick;
        _heartRateGraphTimer.Start();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = false;
        RingComboBox.IsEnabled = false;
        SetStatus("Scanning for COLMI / QRing-compatible BLE devices...");

        try
        {
            var devices = await _ringService.ScanAsync(TimeSpan.FromSeconds(6));
            RingComboBox.ItemsSource = devices;
            if (devices.Count > 0)
            {
                RingComboBox.SelectedIndex = 0;
                SetStatus($"Scan complete: {devices.Count} candidate(s) found.");
            }
            else
            {
                SetStatus("Scan complete: no compatible candidate found.");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"BLE scan failed: {ex.Message}");
        }
        finally
        {
            RefreshConnectionControls();
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var operation = _ringService.IsConnected
            ? ConnectionOperation.ManualDisconnect
            : ConnectionOperation.ManualConnect;
        SetConnectionOperation(operation);

        try
        {
            if (operation == ConnectionOperation.ManualDisconnect)
            {
                SetStatus("Disconnecting ring...");
                await _ringService.DisconnectAsync();
                ClearDeviceDetails();
                return;
            }

            if (RingComboBox.SelectedItem is not RingDeviceCandidate selected)
            {
                SetStatus("Select a ring first.");
                return;
            }

            ResetHeartRateStats();
            EnsureBatteryHistory(selected.BluetoothAddress);

            SetStatus($"Connecting to {selected.Name}...");
            BluetoothIdTextBlock.Text = selected.AddressText;
            var info = await _ringService.ConnectAsync(selected);

            SerialModelTextBlock.Text = $"Serial: {TextOrDash(info.Serial)}    Model: {TextOrDash(info.Model)}";
            HwFwTextBlock.Text = $"HW: {TextOrDash(info.HardwareVersion)}    FW: {TextOrDash(info.FirmwareVersion)}";
            SetStatus("Ring connected. Realtime heart-rate acquisition started.");
        }
        catch (Exception ex)
        {
            SetStatus($"Connection failed: {ex.Message}");
            await _ringService.DisconnectAsync();
            ClearDeviceDetails();
        }
        finally
        {
            SetConnectionOperation(ConnectionOperation.None);
            if (AutoReconnectEnabled && !_ringService.IsConnected && _lastReconnectCandidate is not null)
            {
                EnsureReconnectLoop();
            }
        }
    }

    private void RingService_HeartRateUpdated(int bpm)
    {
        var timestamp = DateTimeOffset.UtcNow;
        Volatile.Write(ref _heartRateSnapshot, new HeartRateSnapshot(bpm, timestamp));
        _heartRateHistory.Add(bpm, timestamp);

        int? minimum;
        int? maximum;
        lock (_heartRateStatsGate)
        {
            _minimumHeartRate = _minimumHeartRate.HasValue ? Math.Min(_minimumHeartRate.Value, bpm) : bpm;
            _maximumHeartRate = _maximumHeartRate.HasValue ? Math.Max(_maximumHeartRate.Value, bpm) : bpm;
            minimum = _minimumHeartRate;
            maximum = _maximumHeartRate;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing)
            {
                return;
            }

            HeartRateTextBlock.Text = $"{bpm} BPM";
            LastHeartRateTextBlock.Text = timestamp.LocalDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            MinHeartRateTextBlock.Text = $"{minimum} BPM";
            MaxHeartRateTextBlock.Text = $"{maximum} BPM";
        }));
    }

    private void RingService_BatteryUpdated(BatteryState battery)
    {
        var timestamp = DateTimeOffset.UtcNow;
        _batteryHistory?.Add(battery, timestamp);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing)
            {
                return;
            }

            BatteryTextBlock.Text = $"{battery.Percent} %";
            ChargingTextBlock.Text = battery.Charging ? "Yes" : "No";
            if (_batteryHistoryToolTip?.IsOpen == true)
            {
                RedrawBatteryHistoryGraph();
            }
        }));
    }

    private void RingService_ConnectionChanged(bool connected)
    {
        if (!connected)
        {
            Volatile.Write(ref _heartRateSnapshot, HeartRateSnapshot.Empty);
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing)
            {
                return;
            }

            ConnectionTextBlock.Text = connected ? "Connected" : "Disconnected";
            ConnectButton.Content = connected ? "Disconnect" : "Connect";
            RefreshConnectionControls(connected);

            if (!connected)
            {
                HeartRateTextBlock.Text = "— BPM";
                LastHeartRateTextBlock.Text = "—";
            }
        }));
    }

    private void RingService_ProtocolWarning(string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closing)
            {
                SetStatus($"BLE: {message}");
            }
        }));
    }

    private void ResetHeartRateStatsButton_Click(object sender, RoutedEventArgs e)
    {
        ResetHeartRateStats();
        SetStatus("Heart-rate min/max and graph history reset.");
    }

    private void ResetHeartRateStats()
    {
        lock (_heartRateStatsGate)
        {
            _minimumHeartRate = null;
            _maximumHeartRate = null;
        }

        _heartRateHistory.Clear();
        MinHeartRateTextBlock.Text = "— BPM";
        MaxHeartRateTextBlock.Text = "— BPM";
        RedrawHeartRateGraph();
    }

    private async void OutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_oscOutputService.IsRunning)
        {
            await StopOscOutputAsync();
            return;
        }

        if (!TryBuildOscOptions(out var options, out var error))
        {
            SetStatus(error);
            return;
        }

        if (!_dummyEnabled && !TryGetFreshHeartRate(out _))
        {
            SetStatus("No fresh heart-rate sample is available. Connect the ring or enable Dummy data.");
            return;
        }

        try
        {
            _oscOutputService.Start(options!, GetEffectiveBpm);
            OutputButton.Content = "Stop OSC output";
            OutputStateTextBlock.Text = $"Sending to {options!.Host}:{options.Port}{options.OscAddress}";
            SetOscSettingsEnabled(false);
            SetStatus("OSC output started.");
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to start OSC output: {ex.Message}");
        }
    }

    private async Task StopOscOutputAsync()
    {
        await _oscOutputService.StopAsync();
        OutputButton.Content = "Start OSC output";
        OutputStateTextBlock.Text = "OSC output stopped";
        SetOscSettingsEnabled(true);
        SetStatus("OSC output stopped.");
    }

    private bool TryGetFreshHeartRate(out int bpm)
    {
        var snapshot = Volatile.Read(ref _heartRateSnapshot);
        return snapshot.TryGetFreshBpm(DateTimeOffset.UtcNow, HeartRateFreshnessThreshold, out bpm);
    }

    private int? GetEffectiveBpm()
    {
        if (_dummyEnabled)
        {
            return Math.Clamp(_dummyBpm, 0, 255);
        }

        return TryGetFreshHeartRate(out var bpm)
            ? Math.Clamp(bpm, 0, 255)
            : null;
    }

    private void OscOutputService_OutputSent(int bpm, double outputValue)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closing)
            {
                LastOutputTextBlock.Text = $"Last output: BPM={bpm}, value={outputValue:0.####}, {DateTime.Now:HH:mm:ss}";
            }
        }));
    }

    private void OscOutputService_OutputError(Exception exception)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (_closing)
            {
                return;
            }

            SetStatus($"OSC output error: {exception.Message}");
            await StopOscOutputAsync();
        }));
    }

    private void DummyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _dummyEnabled = DummyCheckBox.IsChecked == true;
        DummyBpmTextBox.IsEnabled = _dummyEnabled;
    }

    private void DummyBpmTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (int.TryParse(DummyBpmTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bpm))
        {
            _dummyBpm = Math.Clamp(bpm, 0, 255);
        }
    }

    private void ParameterNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (OscAddressPreviewTextBlock is null)
        {
            return;
        }

        var text = ParameterNameTextBox.Text?.Trim() ?? string.Empty;
        OscAddressPreviewTextBlock.Text = text.StartsWith("/avatar/parameters/", StringComparison.Ordinal)
            ? text
            : $"/avatar/parameters/{text.TrimStart('/')}";
    }

    private void OscTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScalingComboBox is null)
        {
            return;
        }

        var type = GetSelectedTag(OscTypeComboBox);
        if (type == "Int" && ScalingComboBox.SelectedIndex == 0)
        {
            ScalingComboBox.SelectedIndex = 1;
        }
    }

    private bool TryBuildOscOptions(out OscOutputOptions? options, out string error)
    {
        options = null;
        error = string.Empty;

        var parameterName = ParameterNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(parameterName))
        {
            error = "OSC parameter name is empty.";
            return false;
        }

        if (!int.TryParse(OscPortTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            error = "OSC port must be between 1 and 65535.";
            return false;
        }

        if (!double.TryParse(OutputIntervalTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var intervalSeconds) || intervalSeconds < 0.05)
        {
            error = "OSC output interval must be at least 0.05 seconds.";
            return false;
        }

        var valueType = GetSelectedTag(OscTypeComboBox) == "Int" ? OscValueType.Int : OscValueType.Float;
        var scaling = GetSelectedTag(ScalingComboBox) == "RawBpm" ? ScalingMode.RawBpm : ScalingMode.Normalize255;

        try
        {
            options = new OscOutputOptions(
                OscHostTextBox.Text.Trim(),
                port,
                parameterName,
                valueType,
                scaling,
                TimeSpan.FromSeconds(intervalSeconds));
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void SetOscSettingsEnabled(bool enabled)
    {
        ParameterNameTextBox.IsEnabled = enabled;
        OscTypeComboBox.IsEnabled = enabled;
        ScalingComboBox.IsEnabled = enabled;
        OscHostTextBox.IsEnabled = enabled;
        OscPortTextBox.IsEnabled = enabled;
        OutputIntervalTextBox.IsEnabled = enabled;
    }

    private static string? GetSelectedTag(ComboBox comboBox)
    {
        return (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
    }

    private void IntegerTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !IntegerRegex.IsMatch(e.Text);
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
    }

    private void ClearDeviceDetails()
    {
        BluetoothIdTextBlock.Text = "—";
        SerialModelTextBlock.Text = "—";
        HwFwTextBlock.Text = "—";
        BatteryTextBlock.Text = "— %";
        ChargingTextBlock.Text = "—";
    }

    private static string TextOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        e.Cancel = true;
        _closing = true;
        _heartRateGraphTimer.Stop();

        try
        {
            await ShutdownReconnectAsync();
            await _oscOutputService.DisposeAsync();
            await _ringService.DisposeAsync();
        }
        finally
        {
            Closing -= Window_Closing;
            Close();
        }
    }
}
