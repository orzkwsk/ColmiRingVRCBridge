using System.Windows;
using System.Windows.Controls;
using ColmiRingVRCBridge.Models;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge;

public partial class MainWindow
{
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(1);

    private CheckBox? _autoReconnectCheckBox;
    private CancellationTokenSource? _reconnectLoopCts;
    private Task? _reconnectLoopTask;
    private RingDeviceCandidate? _lastReconnectCandidate;
    private RingDeviceCandidate? _reconnectAttemptCandidate;
    private ulong? _batteryHistoryAddress;
    private bool _reconnectAttemptInProgress;
    private bool _reconnectUiInitialized;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (_reconnectUiInitialized)
        {
            return;
        }

        _reconnectUiInitialized = true;
        InstallNonBlockingTelemetryUi();

        var settings = ReconnectSettingsStore.Load();
        if (settings.BluetoothAddress.HasValue)
        {
            _lastReconnectCandidate = new RingDeviceCandidate(
                string.IsNullOrWhiteSpace(settings.DeviceName) ? "Last ring" : settings.DeviceName,
                settings.BluetoothAddress.Value,
                0);
        }

        InstallAutoReconnectToggle(settings.AutoReconnect);
        _ringService.ConnectionChanged += RingService_AutoReconnectConnectionChanged;
        Closing += MainWindow_AutoReconnectClosing;

        if (AutoReconnectEnabled && _lastReconnectCandidate is not null && !_ringService.IsConnected)
        {
            SetStatus($"Auto reconnect armed for {_lastReconnectCandidate.AddressText}.");
            EnsureReconnectLoop();
        }
    }

    private bool AutoReconnectEnabled => _autoReconnectCheckBox?.IsChecked == true;

    private void InstallAutoReconnectToggle(bool enabled)
    {
        if (ConnectButton.Parent is not Grid parent)
        {
            return;
        }

        var row = Grid.GetRow(ConnectButton);
        var column = Grid.GetColumn(ConnectButton);
        var columnSpan = Grid.GetColumnSpan(ConnectButton);

        parent.Children.Remove(ConnectButton);

        _autoReconnectCheckBox = new CheckBox
        {
            Content = "Auto reconnect",
            IsChecked = enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 8, 10, 0),
            ToolTip = "Reconnect to the last successful Bluetooth address every ~1 second after disconnect."
        };
        _autoReconnectCheckBox.Checked += AutoReconnectCheckBox_Changed;
        _autoReconnectCheckBox.Unchecked += AutoReconnectCheckBox_Changed;

        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, column);
        Grid.SetColumnSpan(panel, columnSpan);

        panel.Children.Add(_autoReconnectCheckBox);
        panel.Children.Add(ConnectButton);
        parent.Children.Add(panel);
    }

    private void AutoReconnectCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        SaveReconnectSettings();

        if (!AutoReconnectEnabled)
        {
            CancelReconnectLoop();
            SetStatus(_ringService.IsConnected
                ? "Auto reconnect disabled; current BLE connection remains active."
                : "Auto reconnect disabled.");
            return;
        }

        if (!_ringService.IsConnected && _lastReconnectCandidate is not null)
        {
            EnsureReconnectLoop();
        }
    }

    private void RingService_AutoReconnectConnectionChanged(bool connected)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing)
            {
                return;
            }

            if (connected)
            {
                var candidate = _reconnectAttemptCandidate
                                ?? RingComboBox.SelectedItem as RingDeviceCandidate
                                ?? _lastReconnectCandidate;

                if (candidate is not null)
                {
                    _lastReconnectCandidate = candidate;
                    _batteryHistoryAddress = candidate.BluetoothAddress;
                    SaveReconnectSettings();
                }

                return;
            }

            if (_reconnectAttemptInProgress || !AutoReconnectEnabled || _lastReconnectCandidate is null)
            {
                return;
            }

            // ConnectButton_Click disables the button around intentional UI actions.
            // Wait for that operation to finish before deciding whether a reconnect is still needed.
            if (!ConnectButton.IsEnabled)
            {
                _ = WaitForConnectionUiActionAsync();
                return;
            }

            EnsureReconnectLoop();
        }));
    }

    private async Task WaitForConnectionUiActionAsync()
    {
        while (!_closing && !ConnectButton.IsEnabled)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        if (!_closing && AutoReconnectEnabled && !_ringService.IsConnected && _lastReconnectCandidate is not null)
        {
            EnsureReconnectLoop();
        }
    }

    private void EnsureReconnectLoop()
    {
        if (_closing ||
            !AutoReconnectEnabled ||
            _lastReconnectCandidate is null ||
            _ringService.IsConnected ||
            (_reconnectLoopTask is not null && !_reconnectLoopTask.IsCompleted))
        {
            return;
        }

        _reconnectLoopCts?.Dispose();
        _reconnectLoopCts = new CancellationTokenSource();
        _reconnectLoopTask = ReconnectLoopAsync(_reconnectLoopCts.Token);
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested &&
               !_closing &&
               AutoReconnectEnabled &&
               !_ringService.IsConnected)
        {
            try
            {
                await Task.Delay(ReconnectInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (_closing || !AutoReconnectEnabled || _ringService.IsConnected)
            {
                return;
            }

            var candidate = _lastReconnectCandidate;
            if (candidate is null)
            {
                return;
            }

            _reconnectAttemptInProgress = true;
            _reconnectAttemptCandidate = candidate;

            try
            {
                SetStatus($"Auto reconnecting to {candidate.AddressText}...");
                BluetoothIdTextBlock.Text = candidate.AddressText;
                EnsureBatteryHistory(candidate.BluetoothAddress);

                var info = await _ringService.ConnectAsync(candidate);

                if (_closing)
                {
                    return;
                }

                SerialModelTextBlock.Text = $"Serial: {TextOrDash(info.Serial)}    Model: {TextOrDash(info.Model)}";
                HwFwTextBlock.Text = $"HW: {TextOrDash(info.HardwareVersion)}    FW: {TextOrDash(info.FirmwareVersion)}";
                _lastReconnectCandidate = candidate;
                SaveReconnectSettings();
                SetStatus("Ring reconnected. Realtime heart-rate acquisition started.");
                return;
            }
            catch (Exception ex)
            {
                if (_closing || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                SetStatus($"Auto reconnect failed: {ex.Message} Retrying in 1 s...");

                try
                {
                    await _ringService.DisconnectAsync();
                }
                catch
                {
                }
            }
            finally
            {
                _reconnectAttemptCandidate = null;
                _reconnectAttemptInProgress = false;
            }
        }
    }

    private void EnsureBatteryHistory(ulong bluetoothAddress)
    {
        if (_batteryHistory is not null && _batteryHistoryAddress == bluetoothAddress)
        {
            return;
        }

        _batteryHistory = new BatteryHistoryStore(bluetoothAddress, BatteryHistoryRetention);
        _batteryHistoryAddress = bluetoothAddress;
        RedrawBatteryHistoryGraph();
    }

    private void SaveReconnectSettings()
    {
        var candidate = _lastReconnectCandidate;
        ReconnectSettingsStore.Save(new ReconnectSettings(
            candidate?.BluetoothAddress,
            candidate?.Name,
            AutoReconnectEnabled));
    }

    private void CancelReconnectLoop()
    {
        if (_reconnectLoopCts is null)
        {
            return;
        }

        _reconnectLoopCts.Cancel();
    }

    private void MainWindow_AutoReconnectClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        CancelReconnectLoop();
        SaveReconnectSettings();
        UninstallNonBlockingTelemetryUi();
        _ringService.ConnectionChanged -= RingService_AutoReconnectConnectionChanged;
    }
}
