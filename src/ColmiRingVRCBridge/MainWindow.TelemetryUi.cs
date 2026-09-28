using System.Globalization;
using System.Windows;
using ColmiRingVRCBridge.Models;

namespace ColmiRingVRCBridge;

public partial class MainWindow
{
    private bool _nonBlockingTelemetryUiInstalled;

    private void InstallNonBlockingTelemetryUi()
    {
        if (_nonBlockingTelemetryUiInstalled)
        {
            return;
        }

        _nonBlockingTelemetryUiInstalled = true;

        _ringService.HeartRateUpdated -= RingService_HeartRateUpdated;
        _ringService.BatteryUpdated -= RingService_BatteryUpdated;
        _ringService.ConnectionChanged -= RingService_ConnectionChanged;
        _ringService.ProtocolWarning -= RingService_ProtocolWarning;

        _ringService.HeartRateUpdated += RingService_HeartRateUpdatedNonBlocking;
        _ringService.BatteryUpdated += RingService_BatteryUpdatedNonBlocking;
        _ringService.ConnectionChanged += RingService_ConnectionChangedNonBlocking;
        _ringService.ProtocolWarning += RingService_ProtocolWarningNonBlocking;

        _heartRateGraphTimer.Tick += HeartRateGraphTimer_DiagnosticsTick;
    }

    private void UninstallNonBlockingTelemetryUi()
    {
        if (!_nonBlockingTelemetryUiInstalled)
        {
            return;
        }

        _nonBlockingTelemetryUiInstalled = false;
        _heartRateGraphTimer.Tick -= HeartRateGraphTimer_DiagnosticsTick;

        _ringService.HeartRateUpdated -= RingService_HeartRateUpdatedNonBlocking;
        _ringService.BatteryUpdated -= RingService_BatteryUpdatedNonBlocking;
        _ringService.ConnectionChanged -= RingService_ConnectionChangedNonBlocking;
        _ringService.ProtocolWarning -= RingService_ProtocolWarningNonBlocking;
    }

    private void RingService_HeartRateUpdatedNonBlocking(int bpm)
    {
        var timestamp = DateTimeOffset.Now;
        _latestHeartRate = bpm;
        _hasHeartRate = true;
        _minimumHeartRate = _minimumHeartRate.HasValue ? Math.Min(_minimumHeartRate.Value, bpm) : bpm;
        _maximumHeartRate = _maximumHeartRate.HasValue ? Math.Max(_maximumHeartRate.Value, bpm) : bpm;
        _heartRateHistory.Add(bpm, timestamp);

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing)
            {
                return;
            }

            HeartRateTextBlock.Text = $"{bpm} BPM";
            LastHeartRateTextBlock.Text = timestamp.LocalDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            MinHeartRateTextBlock.Text = $"{_minimumHeartRate} BPM";
            MaxHeartRateTextBlock.Text = $"{_maximumHeartRate} BPM";
        }));
    }

    private void RingService_BatteryUpdatedNonBlocking(BatteryState battery)
    {
        var timestamp = DateTimeOffset.Now;
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

    private void RingService_ConnectionChangedNonBlocking(bool connected)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing)
            {
                return;
            }

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
        }));
    }

    private void RingService_ProtocolWarningNonBlocking(string message)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_closing)
            {
                SetStatus($"BLE: {message}");
            }
        }));
    }

    private void HeartRateGraphTimer_DiagnosticsTick(object? sender, EventArgs e)
    {
        var diagnostics = _ringService.GetTelemetryDiagnostics();
        var now = DateTimeOffset.UtcNow;

        static string AgeText(DateTimeOffset now, DateTimeOffset? timestamp)
        {
            if (!timestamp.HasValue)
            {
                return "never";
            }

            var seconds = Math.Max(0, (now - timestamp.Value).TotalSeconds);
            return seconds < 60
                ? $"{seconds:0.0} s ago"
                : $"{seconds / 60.0:0.0} min ago";
        }

        ConnectionTextBlock.ToolTip =
            $"Last valid BLE notification: {AgeText(now, diagnostics.LastValidNotificationAt)}\n" +
            $"Last valid HR: {AgeText(now, diagnostics.LastValidHeartRateAt)}\n" +
            $"HR poll TX: {diagnostics.HeartRatePollTxCount}\n" +
            $"HR poll write failures: {diagnostics.HeartRatePollWriteFailureCount}\n" +
            $"Consecutive poll failures: {diagnostics.ConsecutiveHeartRatePollFailures}";
    }
}
