using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private static readonly TimeSpan BatteryHistoryRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan BatteryGraphGapThreshold = TimeSpan.FromMinutes(30);

    private readonly ColmiRingBleService _ringService = new();
    private readonly OscOutputService _oscOutputService = new();
    private readonly HeartRateHistoryBuffer _heartRateHistory = new(HeartRateHistoryRetention);
    private readonly DispatcherTimer _heartRateGraphTimer;

    private BatteryHistoryStore? _batteryHistory;
    private ToolTip? _batteryHistoryToolTip;
    private Canvas? _batteryChargingIntervalsCanvas;
    private Canvas? _batteryHistoryGraphCanvas;
    private System.Windows.Shapes.Path? _batteryHistoryGraphPath;
    private TextBlock? _batteryHistoryEmptyText;
    private TextBlock? _batteryHistorySummaryText;

    private int _latestHeartRate;
    private bool _hasHeartRate;
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

            ResetHeartRateStats();
            _batteryHistory = new BatteryHistoryStore(selected.BluetoothAddress, BatteryHistoryRetention);
            RedrawBatteryHistoryGraph();

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
        var timestamp = DateTimeOffset.Now;
        _latestHeartRate = bpm;
        _hasHeartRate = true;
        _minimumHeartRate = _minimumHeartRate.HasValue ? Math.Min(_minimumHeartRate.Value, bpm) : bpm;
        _maximumHeartRate = _maximumHeartRate.HasValue ? Math.Max(_maximumHeartRate.Value, bpm) : bpm;
        _heartRateHistory.Add(bpm, timestamp);

        Dispatcher.Invoke(() =>
        {
            HeartRateTextBlock.Text = $"{bpm} BPM";
            LastHeartRateTextBlock.Text = timestamp.LocalDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            MinHeartRateTextBlock.Text = $"{_minimumHeartRate} BPM";
            MaxHeartRateTextBlock.Text = $"{_maximumHeartRate} BPM";
            RedrawHeartRateGraph();
        });
    }

    private void RingService_BatteryUpdated(BatteryState battery)
    {
        var timestamp = DateTimeOffset.Now;
        _batteryHistory?.Add(battery, timestamp);

        Dispatcher.Invoke(() =>
        {
            BatteryTextBlock.Text = $"{battery.Percent} %";
            ChargingTextBlock.Text = battery.Charging ? "Yes" : "No";
            if (_batteryHistoryToolTip?.IsOpen == true)
            {
                RedrawBatteryHistoryGraph();
            }
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

    private void ResetHeartRateStatsButton_Click(object sender, RoutedEventArgs e)
    {
        ResetHeartRateStats();
        SetStatus("Heart-rate min/max and graph history reset.");
    }

    private void ResetHeartRateStats()
    {
        _minimumHeartRate = null;
        _maximumHeartRate = null;
        _heartRateHistory.Clear();

        if (MinHeartRateTextBlock is not null)
        {
            MinHeartRateTextBlock.Text = "— BPM";
        }

        if (MaxHeartRateTextBlock is not null)
        {
            MaxHeartRateTextBlock.Text = "— BPM";
        }

        RedrawHeartRateGraph();
    }

    private void HeartRateGraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawHeartRateGraph();
    }

    private void RedrawHeartRateGraph()
    {
        if (HeartRateGraphCanvas is null || HeartRateGraphPath is null || HeartRateGraphEmptyText is null)
        {
            return;
        }

        var width = HeartRateGraphCanvas.ActualWidth;
        var height = HeartRateGraphCanvas.ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var windowStart = now - HeartRateGraphWindow;
        var samples = _heartRateHistory.Snapshot(windowStart)
            .Where(sample => sample.Timestamp <= now)
            .OrderBy(sample => sample.Timestamp)
            .ToArray();

        if (samples.Length == 0)
        {
            HeartRateGraphPath.Data = null;
            HeartRateGraphEmptyText.Visibility = Visibility.Visible;
            return;
        }

        HeartRateGraphEmptyText.Visibility = Visibility.Collapsed;

        var minimum = samples.Min(sample => (double)sample.Bpm);
        var maximum = samples.Max(sample => (double)sample.Bpm);
        var center = (minimum + maximum) / 2.0;
        var span = Math.Max(20.0, maximum - minimum);
        var paddedSpan = span * 1.2;
        var yMin = center - (paddedSpan / 2.0);
        var yMax = center + (paddedSpan / 2.0);
        var yRange = Math.Max(1.0, yMax - yMin);

        Point ToPoint(HeartRateSample sample)
        {
            var xRatio = (sample.Timestamp - windowStart).TotalMilliseconds / HeartRateGraphWindow.TotalMilliseconds;
            var x = Math.Clamp(xRatio, 0.0, 1.0) * width;
            var yRatio = (sample.Bpm - yMin) / yRange;
            var y = height - (Math.Clamp(yRatio, 0.0, 1.0) * height);
            return new Point(x, y);
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            HeartRateSample? previous = null;
            foreach (var sample in samples)
            {
                var point = ToPoint(sample);
                if (previous is null || sample.Timestamp - previous.Value.Timestamp > HeartRateGraphGapThreshold)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: false);
                }

                previous = sample;
            }
        }

        geometry.Freeze();
        HeartRateGraphPath.Data = geometry;
    }

    private void InitializeBatteryHistoryToolTip()
    {
        _batteryHistoryToolTip = new ToolTip
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse,
            StaysOpen = false
        };
        _batteryHistoryToolTip.Opened += (_, _) => RedrawBatteryHistoryGraph();

        var outer = new Border
        {
            Width = 470,
            Padding = new Thickness(10)
        };
        var stack = new StackPanel();
        outer.Child = stack;

        var title = new TextBlock
        {
            Text = "Battery history — last 24 hours",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        stack.Children.Add(title);

        var graphBorder = new Border
        {
            Height = 170,
            BorderBrush = new SolidColorBrush(Color.FromRgb(216, 216, 216)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(250, 250, 250)),
            ClipToBounds = true
        };
        stack.Children.Add(graphBorder);

        var graphGrid = new Grid();
        graphBorder.Child = graphGrid;

        _batteryChargingIntervalsCanvas = new Canvas
        {
            Margin = new Thickness(34, 8, 8, 22),
            IsHitTestVisible = false
        };
        graphGrid.Children.Add(_batteryChargingIntervalsCanvas);

        _batteryHistoryGraphCanvas = new Canvas
        {
            Margin = new Thickness(34, 8, 8, 22),
            IsHitTestVisible = false
        };
        _batteryHistoryGraphCanvas.SizeChanged += (_, _) => RedrawBatteryHistoryGraph();
        graphGrid.Children.Add(_batteryHistoryGraphCanvas);

        _batteryHistoryGraphPath = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(79, 127, 211)),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        _batteryHistoryGraphCanvas.Children.Add(_batteryHistoryGraphPath);

        _batteryHistoryEmptyText = new TextBlock
        {
            Text = "No battery history yet",
            Foreground = Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        graphGrid.Children.Add(_batteryHistoryEmptyText);

        graphGrid.Children.Add(new TextBlock
        {
            Text = "100%",
            Foreground = Brushes.Gray,
            FontSize = 10,
            Margin = new Thickness(3, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        });
        graphGrid.Children.Add(new TextBlock
        {
            Text = "50%",
            Foreground = Brushes.Gray,
            FontSize = 10,
            Margin = new Thickness(6, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        });
        graphGrid.Children.Add(new TextBlock
        {
            Text = "0%",
            Foreground = Brushes.Gray,
            FontSize = 10,
            Margin = new Thickness(10, 0, 0, 18),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false
        });
        graphGrid.Children.Add(new TextBlock
        {
            Text = "-24 h",
            Foreground = Brushes.Gray,
            FontSize = 10,
            Margin = new Thickness(34, 0, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false
        });
        graphGrid.Children.Add(new TextBlock
        {
            Text = "now",
            Foreground = Brushes.Gray,
            FontSize = 10,
            Margin = new Thickness(0, 0, 8, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false
        });

        _batteryHistorySummaryText = new TextBlock
        {
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(0, 7, 0, 0)
        };
        stack.Children.Add(_batteryHistorySummaryText);

        var legend = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 0)
        };
        legend.Children.Add(new Border
        {
            Width = 13,
            Height = 8,
            Margin = new Thickness(0, 3, 5, 0),
            Background = new SolidColorBrush(Color.FromArgb(70, 76, 175, 80))
        });
        legend.Children.Add(new TextBlock
        {
            Text = "charging interval",
            Foreground = Brushes.Gray,
            FontSize = 11
        });
        stack.Children.Add(legend);

        _batteryHistoryToolTip.Content = outer;
        BatteryTextBlock.ToolTip = _batteryHistoryToolTip;
        ToolTipService.SetInitialShowDelay(BatteryTextBlock, 250);
        ToolTipService.SetShowDuration(BatteryTextBlock, 60000);
    }

    private void RedrawBatteryHistoryGraph()
    {
        if (_batteryHistoryGraphCanvas is null ||
            _batteryChargingIntervalsCanvas is null ||
            _batteryHistoryGraphPath is null ||
            _batteryHistoryEmptyText is null ||
            _batteryHistorySummaryText is null)
        {
            return;
        }

        var width = _batteryHistoryGraphCanvas.ActualWidth;
        var height = _batteryHistoryGraphCanvas.ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var windowStart = now - BatteryHistoryRetention;
        var samples = _batteryHistory?.Snapshot(now)
            .Where(sample => sample.Timestamp >= windowStart && sample.Timestamp <= now)
            .OrderBy(sample => sample.Timestamp)
            .ToArray() ?? Array.Empty<BatteryHistorySample>();

        _batteryChargingIntervalsCanvas.Children.Clear();

        if (samples.Length == 0)
        {
            _batteryHistoryGraphPath.Data = null;
            _batteryHistoryEmptyText.Visibility = Visibility.Visible;
            _batteryHistorySummaryText.Text = "History is stored per ring and survives app restarts.";
            return;
        }

        _batteryHistoryEmptyText.Visibility = Visibility.Collapsed;

        double X(DateTimeOffset timestamp)
        {
            var ratio = (timestamp - windowStart).TotalMilliseconds / BatteryHistoryRetention.TotalMilliseconds;
            return Math.Clamp(ratio, 0.0, 1.0) * width;
        }

        double Y(int percent)
        {
            return height - (Math.Clamp(percent, 0, 100) / 100.0 * height);
        }

        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            if (!sample.Charging)
            {
                continue;
            }

            var end = i + 1 < samples.Length ? samples[i + 1].Timestamp : now;
            var duration = end - sample.Timestamp;
            if (duration <= TimeSpan.Zero || duration > BatteryGraphGapThreshold)
            {
                continue;
            }

            var left = X(sample.Timestamp);
            var right = X(end);
            var rectangle = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1.0, right - left),
                Height = height,
                Fill = new SolidColorBrush(Color.FromArgb(55, 76, 175, 80)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(rectangle, left);
            Canvas.SetTop(rectangle, 0);
            _batteryChargingIntervalsCanvas.Children.Add(rectangle);
        }

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            BatteryHistorySample? previous = null;
            foreach (var sample in samples)
            {
                var point = new Point(X(sample.Timestamp), Y(sample.Percent));
                if (previous is null || sample.Timestamp - previous.Value.Timestamp > BatteryGraphGapThreshold)
                {
                    context.BeginFigure(point, isFilled: false, isClosed: false);
                }
                else
                {
                    context.LineTo(point, isStroked: true, isSmoothJoin: false);
                }

                previous = sample;
            }
        }

        geometry.Freeze();
        _batteryHistoryGraphPath.Data = geometry;

        var first = samples[0];
        var last = samples[^1];
        var delta = last.Percent - first.Percent;
        var deltaText = delta > 0 ? $"+{delta}" : delta.ToString(CultureInfo.InvariantCulture);
        _batteryHistorySummaryText.Text = $"{first.Percent}% → {last.Percent}%  ({deltaText} pp) · {samples.Length} stored samples";
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
        _heartRateGraphTimer.Stop();

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
