using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ColmiRingVRCBridge.Models;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge;

public partial class MainWindow : Window
{
    private static readonly Regex IntegerRegex = new("^[0-9]+$");

    private readonly ColmiRingBleService _ringService = new();
    private readonly OscOutputService _oscOutputService = new();
    private int _latestHeartRate;
    private bool _hasHeartRate;
    private bool _dummyEnabled;
    private int _dummyBpm = 72;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();

        _ringService.HeartRateUpdated += RingService_HeartRateUpdated;
        _ringService.BatteryUpdated += RingService_BatteryUpdated;
        _ringService.ConnectionChanged += RingService_ConnectionChanged;
        _ringService.ProtocolWarning += RingService_ProtocolWarning;

        _oscOutputService.OutputSent += OscOutputService_OutputSent;
        _oscOutputService.OutputError += OscOutputService_OutputError;
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
            ScanButton.IsEnabled = true;
            RingComboBox.IsEnabled = true;
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            if (_ringService.IsConnected)
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
            ConnectButton.IsEnabled = true;
        }
    }

    private void RingService_HeartRateUpdated(int bpm)
    {
        _latestHeartRate = bpm;
        _hasHeartRate = true;

        Dispatcher.Invoke(() =>
        {
            HeartRateTextBlock.Text = $"{bpm} BPM";
            LastHeartRateTextBlock.Text = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        });
    }

    private void RingService_BatteryUpdated(BatteryState battery)
    {
        Dispatcher.Invoke(() =>
        {
            BatteryTextBlock.Text = $"{battery.Percent} %";
            ChargingTextBlock.Text = battery.Charging ? "Yes" : "No";
        });
    }

    private void RingService_ConnectionChanged(bool connected)
    {
        Dispatcher.Invoke(() =>
        {
            ConnectionTextBlock.Text = connected ? "Connected" : "Disconnected";
            ConnectButton.Content = connected ? "Disconnect" : "Connect";
            RingComboBox.IsEnabled = !connected;
            ScanButton.IsEnabled = !connected;

            if (!connected)
            {
                _hasHeartRate = false;
                HeartRateTextBlock.Text = "— BPM";
                LastHeartRateTextBlock.Text = "—";
            }
        });
    }

    private void RingService_ProtocolWarning(string message)
    {
        Dispatcher.Invoke(() => SetStatus($"BLE: {message}"));
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

        if (!_dummyEnabled && !_ringService.IsConnected)
        {
            SetStatus("Connect a ring or enable Dummy data before starting OSC output.");
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

    private int? GetEffectiveBpm()
    {
        if (_dummyEnabled)
        {
            return Math.Clamp(_dummyBpm, 0, 255);
        }

        return _hasHeartRate ? Math.Clamp(_latestHeartRate, 0, 255) : null;
    }

    private void OscOutputService_OutputSent(int bpm, double outputValue)
    {
        Dispatcher.Invoke(() =>
        {
            LastOutputTextBlock.Text = $"Last output: BPM={bpm}, value={outputValue:0.####}, {DateTime.Now:HH:mm:ss}";
        });
    }

    private void OscOutputService_OutputError(Exception exception)
    {
        Dispatcher.Invoke(async () =>
        {
            SetStatus($"OSC output error: {exception.Message}");
            await StopOscOutputAsync();
        });
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

        options = new OscOutputOptions(
            OscHostTextBox.Text.Trim(),
            port,
            parameterName,
            valueType,
            scaling,
            TimeSpan.FromSeconds(intervalSeconds));
        return true;
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

        try
        {
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
