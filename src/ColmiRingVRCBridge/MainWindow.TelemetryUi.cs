using ColmiRingVRCBridge.Models;

namespace ColmiRingVRCBridge;

public partial class MainWindow
{
    private void HeartRateGraphTimer_DiagnosticsTick(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }

        var diagnostics = _ringService.GetTelemetryDiagnostics();
        var now = DateTimeOffset.UtcNow;
        var telemetryState = _ringService.GetTelemetryState(now, HeartRateFreshnessThreshold);
        var snapshot = Volatile.Read(ref _heartRateSnapshot);
        var freshness = snapshot.GetFreshness(now, HeartRateFreshnessThreshold);

        ConnectionTextBlock.Text = telemetryState switch
        {
            HeartRateTelemetryState.Disconnected => "Disconnected",
            HeartRateTelemetryState.Initializing => "Connected / HR init",
            HeartRateTelemetryState.Streaming => "Connected",
            HeartRateTelemetryState.Stale => "Connected / HR stale",
            _ => "Unknown"
        };

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
            $"Telemetry state: {telemetryState}\n" +
            $"HR snapshot freshness: {freshness} (threshold {HeartRateFreshnessThreshold.TotalSeconds:0.#} s)\n" +
            $"HR session started: {diagnostics.HeartRateSessionStarted}\n" +
            $"Last raw notification: {AgeText(now, diagnostics.LastRawNotificationAt)}\n" +
            $"Last valid packet: {AgeText(now, diagnostics.LastValidPacketAt)}\n" +
            $"Last valid HR: {AgeText(now, diagnostics.LastValidHeartRateAt)}\n" +
            $"Raw / valid / invalid packets: {diagnostics.RawNotificationCount} / {diagnostics.ValidPacketCount} / {diagnostics.InvalidPacketCount}\n" +
            $"HR packets: {diagnostics.HeartRatePacketCount}\n" +
            $"HR poll TX: {diagnostics.HeartRatePollTxCount}\n" +
            $"HR poll write failures: {diagnostics.HeartRatePollWriteFailureCount}\n" +
            $"Consecutive poll failures: {diagnostics.ConsecutiveHeartRatePollFailures}";
    }
}
