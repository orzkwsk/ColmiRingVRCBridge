using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge;

public partial class MainWindow
{
    private static readonly TimeSpan HeartRateGraphWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan HeartRateGraphGapThreshold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BatteryGraphGapThreshold = TimeSpan.FromMinutes(30);

    private void BatteryHistoryGraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawBatteryHistoryGraph();

    private void HeartRateGraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawHeartRateGraph();
    }

    private void RedrawHeartRateGraph()
    {
        if (_viewModel is null)
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
        var samples = _viewModel.HeartRateSamples(windowStart)
            .Where(sample => sample.Timestamp <= now)
            .OrderBy(sample => sample.Timestamp)
            .ToArray();

        if (samples.Length == 0)
        {
            HeartRateGraphPath.Data = null;
            HeartRateGraphEmptyText.Visibility = Visibility.Visible;
            HeartRateAxisHigh.Text = HeartRateAxisMid.Text = HeartRateAxisLow.Text = "";
            return;
        }

        HeartRateGraphEmptyText.Visibility = Visibility.Collapsed;

        var minimum = samples.Min(sample => (double)sample.Bpm);
        var maximum = samples.Max(sample => (double)sample.Bpm);
        var center = (minimum + maximum) / 2.0;
        var span = Math.Max(20.0, maximum - minimum);
        var paddedSpan = span * 1.2;
        var yMin = Math.Max(0, Math.Floor((center - paddedSpan / 2) / 5) * 5);
        var yMax = Math.Ceiling((center + paddedSpan / 2) / 5) * 5;
        var yRange = yMax - yMin;
        HeartRateAxisHigh.Text = yMax.ToString("0", CultureInfo.InvariantCulture);
        HeartRateAxisMid.Text = ((yMin + yMax) / 2).ToString("0.#", CultureInfo.InvariantCulture);
        HeartRateAxisLow.Text = yMin.ToString("0", CultureInfo.InvariantCulture);

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

    private void RedrawBatteryHistoryGraph()
    {
        if (_viewModel is null || BatteryHistoryGraphCanvas is null ||
            BatteryChargingIntervalsCanvas is null ||
            BatteryHistoryGraphPath is null ||
            BatteryHistoryEmptyText is null ||
            BatteryHistorySummaryText is null)
        {
            return;
        }

        var width = BatteryHistoryGraphCanvas.ActualWidth;
        var height = BatteryHistoryGraphCanvas.ActualHeight;
        if (width <= 1 || height <= 1)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var windowStart = now - BatteryHistoryBuffer.Retention;
        var samples = _viewModel.BatterySamples(now)
            .Where(sample => sample.Timestamp >= windowStart && sample.Timestamp <= now)
            .OrderBy(sample => sample.Timestamp)
            .ToArray();

        BatteryChargingIntervalsCanvas.Children.Clear();

        if (samples.Length == 0)
        {
            BatteryHistoryGraphPath.Data = null;
            BatteryHistoryEmptyText.Visibility = Visibility.Visible;
            BatteryHistorySummaryText.Text = "History is kept in memory while the app is open.";
            return;
        }

        BatteryHistoryEmptyText.Visibility = Visibility.Collapsed;

        double X(DateTimeOffset timestamp)
        {
            var ratio = (timestamp - windowStart).TotalMilliseconds / BatteryHistoryBuffer.Retention.TotalMilliseconds;
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
            BatteryChargingIntervalsCanvas.Children.Add(rectangle);
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
        BatteryHistoryGraphPath.Data = geometry;

        var first = samples[0];
        var last = samples[^1];
        var delta = last.Percent - first.Percent;
        var deltaText = delta > 0 ? $"+{delta}" : delta.ToString(CultureInfo.InvariantCulture);
        BatteryHistorySummaryText.Text = $"{first.Percent}% → {last.Percent}%  ({deltaText} pp) · {samples.Length} stored samples";
    }
}
