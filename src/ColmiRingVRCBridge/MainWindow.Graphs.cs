using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge;

public partial class MainWindow
{
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
            StaysOpen = true
        };
        _batteryHistoryToolTip.Opened += (_, _) => RedrawBatteryHistoryGraph();

        var outer = new Border
        {
            Width = 470,
            Padding = new Thickness(10)
        };
        var stack = new StackPanel();
        outer.Child = stack;

        stack.Children.Add(new TextBlock
        {
            Text = "Battery history — last 24 hours",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });

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
}
