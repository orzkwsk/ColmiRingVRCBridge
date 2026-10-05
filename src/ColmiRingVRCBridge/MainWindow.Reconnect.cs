using System.Windows;
using System.Windows.Controls;
using ColmiRingVRCBridge.Models;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge;

public partial class MainWindow
{
    // Operational requirement: keep trying to restore the upstream BLE source promptly.
    // This is intentionally a fixed 1-second retry delay rather than exponential backoff.
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(1);

    private enum ConnectionOperation
    {
        None,
        ManualScan,
        ManualConnect,
        ManualDisconnect,
        ManualReboot,
        AutoReconnect
    }

    private readonly SemaphoreSlim _reconnectControlLock = new(1, 1);
    private CheckBox? _autoReconnectCheckBox;
    private CancellationTokenSource? _reconnectLoopCts;
    private Task? _reconnectLoopTask;
    private RingDeviceCandidate? _lastReconnectCandidate;
    private RingDeviceCandidate? _reconnectAttemptCandidate;
    private ulong? _batteryHistoryAddress;
    private ConnectionOperation _connectionOperation;
    private bool _autoReconnectEnabled;
    private bool _autoReconnectSuppressed;
    private bool _reconnectUiInitialized;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (_reconnectUiInitialized)
        {
            return;
        }

        _reconnectUiInitialized = true;

        var settings = ReconnectSettingsStore.Load();
        _autoReconnectEnabled = settings.AutoReconnect;
        if (settings.BluetoothAddress.HasValue)
        {
            _lastReconnectCandidate = new RingDeviceCandidate(
                string.IsNullOrWhiteSpace(settings.DeviceName) ? "Last ring" : settings.DeviceName,
                settings.BluetoothAddress.Value,
                0);
        }

        InstallAutoReconnectToggle(settings.AutoReconnect);
        _ringService.ConnectionChanged += RingService_AutoReconnectConnectionChanged;
        RefreshConnectionControls();

        if (AutoReconnectAllowed && _lastReconnectCandidate is not null && !_ringService.IsConnected)
        {
            SetStatus($"Auto reconnect armed for {_lastReconnectCandidate.AddressText}.");
            EnsureReconnectLoop();
        }
    }

    private bool AutoReconnectEnabled => _autoReconnectEnabled;
    private bool AutoReconnectAllowed => AutoReconnectEnabled && !_autoReconnectSuppressed;

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
            ToolTip = "Keep restoring the last successful Bluetooth connection with a fixed 1-second retry delay."
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
        var enabled = (sender as CheckBox)?.IsChecked == true;
        _ = ApplyAutoReconnectSettingAsync(enabled);
    }

    private async Task ApplyAutoReconnectSettingAsync(bool enabled)
    {
        await _reconnectControlLock.WaitAsync();
        try
        {
            _autoReconnectEnabled = enabled;
            SaveReconnectSettings();

            if (!enabled)
            {
                await StopReconnectLoopAsync();
                SetStatus(_ringService.IsConnected
                    ? "Auto reconnect disabled; current BLE connection remains active."
                    : "Auto reconnect disabled.");
                return;
            }

            // Ensure an old cancelled loop cannot block the newly enabled state.
            await StopReconnectLoopAsync();

            if (!_ringService.IsConnected &&
                _lastReconnectCandidate is not null &&
                _connectionOperation == ConnectionOperation.None &&
                AutoReconnectAllowed)
            {
                EnsureReconnectLoop();
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Auto reconnect setting failed: {ex.Message}");
        }
        finally
        {
            _reconnectControlLock.Release();
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
                    EnsureBatteryHistory(candidate.BluetoothAddress);
                    SaveReconnectSettings();
                }

                return;
            }

            if (!AutoReconnectAllowed ||
                _lastReconnectCandidate is null ||
                _connectionOperation != ConnectionOperation.None)
            {
                return;
            }

            EnsureReconnectLoop();
        }));
    }

    private void SetConnectionOperation(ConnectionOperation operation)
    {
        _connectionOperation = operation;
        RefreshConnectionControls();
    }

    private void RefreshConnectionControls(bool? connected = null)
    {
        var linkConnected = connected ?? _ringService.IsConnected;
        var manualBusy = _connectionOperation is
            ConnectionOperation.ManualScan or
            ConnectionOperation.ManualConnect or
            ConnectionOperation.ManualDisconnect or
            ConnectionOperation.ManualReboot;

        // Manual actions are allowed to preempt an AutoReconnect attempt.
        // The click handlers first suppress/cancel the background reconnect loop.
        ConnectButton.IsEnabled = !manualBusy;
        RingComboBox.IsEnabled = !linkConnected && !manualBusy;
        ScanButton.IsEnabled = !linkConnected && !manualBusy;
        RebootButton.IsEnabled = linkConnected && !manualBusy;
    }

    private void EnsureReconnectLoop()
    {
        if (_closing ||
            !AutoReconnectAllowed ||
            _lastReconnectCandidate is null ||
            _ringService.IsConnected ||
            _connectionOperation != ConnectionOperation.None ||
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
               AutoReconnectAllowed &&
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

            if (_closing || !AutoReconnectAllowed || _ringService.IsConnected)
            {
                return;
            }

            if (_connectionOperation != ConnectionOperation.None)
            {
                continue;
            }

            var candidate = _lastReconnectCandidate;
            if (candidate is null)
            {
                return;
            }

            _reconnectAttemptCandidate = candidate;
            SetConnectionOperation(ConnectionOperation.AutoReconnect);

            try
            {
                SetStatus($"Auto reconnecting to {candidate.AddressText}...");
                BluetoothIdTextBlock.Text = candidate.AddressText;
                EnsureBatteryHistory(candidate.BluetoothAddress);

                var info = await _ringService.ConnectAsync(candidate, cancellationToken);

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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (_closing)
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
                SetConnectionOperation(ConnectionOperation.None);
            }
        }
    }

    private async Task SuppressAutoReconnectAsync()
    {
        _autoReconnectSuppressed = true;
        await StopReconnectLoopAsync();
        RefreshConnectionControls();
    }

    private void ResumeAutoReconnect()
    {
        _autoReconnectSuppressed = false;
        RefreshConnectionControls();

        if (AutoReconnectAllowed &&
            !_ringService.IsConnected &&
            _lastReconnectCandidate is not null &&
            _connectionOperation == ConnectionOperation.None)
        {
            EnsureReconnectLoop();
        }
    }

    private async Task StopReconnectLoopAsync()
    {
        var cts = _reconnectLoopCts;
        var task = _reconnectLoopTask;
        _reconnectLoopCts = null;
        _reconnectLoopTask = null;

        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
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

    private async Task ShutdownReconnectAsync()
    {
        await StopReconnectLoopAsync();
        SaveReconnectSettings();
        _ringService.ConnectionChanged -= RingService_AutoReconnectConnectionChanged;
    }
}
