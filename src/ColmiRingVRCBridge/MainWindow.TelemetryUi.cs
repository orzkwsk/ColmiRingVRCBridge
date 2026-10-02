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

#if DEBUG
        InitializeDebugTelemetryLogging();
#endif

        var diagnostics = _ringService.GetTelemetryDiagnostics();
        var now = DateTimeOffset.UtcNow;
        var telemetryState = _ringService.GetTelemetryState(now, HeartRateFreshnessThreshold);
        var heartRateSnapshot = Volatile.Read(ref _heartRateSnapshot);
        var heartRateFreshness = heartRateSnapshot.GetFreshness(now, HeartRateFreshnessThreshold);
        var batterySnapshot = Volatile.Read(ref _batterySnapshot);
        var batteryFreshness = batterySnapshot.GetFreshness(now, BatteryFreshnessThreshold);

        ConnectionTextBlock.Text = telemetryState switch
        {
            HeartRateTelemetryState.Disconnected => "Disconnected",
            HeartRateTelemetryState.Initializing => "Connected / HR init",
            HeartRateTelemetryState.Streaming => "Connected",
            HeartRateTelemetryState.Stale => "Connected / HR stale",
            _ => "Unknown"
        };

        if (_ringService.IsConnected)
        {
            switch (batteryFreshness)
            {
                case BatteryFreshness.Fresh when batterySnapshot.Battery is { } battery:
                    BatteryTextBlock.Text = $"{battery.Percent} %";
                    ChargingTextBlock.Text = battery.Charging ? "Yes" : "No";
                    break;

                case BatteryFreshness.Stale when batterySnapshot.Battery is { } battery:
                    BatteryTextBlock.Text = $"{battery.Percent} % (stale)";
                    ChargingTextBlock.Text = battery.Charging ? "Yes (stale)" : "No (stale)";
                    break;

                default:
                    BatteryTextBlock.Text = "— %";
                    ChargingTextBlock.Text = "—";
                    break;
            }
        }

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
            $"HR snapshot freshness: {heartRateFreshness} (threshold {HeartRateFreshnessThreshold.TotalSeconds:0.#} s)\n" +
            $"Battery freshness: {batteryFreshness} (threshold {BatteryFreshnessThreshold.TotalMinutes:0.#} min)\n" +
            $"HR session started: {diagnostics.HeartRateSessionStarted}\n" +
            $"Last raw notification: {AgeText(now, diagnostics.LastRawNotificationAt)}\n" +
            $"Last valid packet: {AgeText(now, diagnostics.LastValidPacketAt)}\n" +
            $"Last valid HR: {AgeText(now, diagnostics.LastValidHeartRateAt)}\n" +
            $"Last battery poll: {AgeText(now, diagnostics.LastBatteryPollAt)}\n" +
            $"Last battery packet: {AgeText(now, diagnostics.LastBatteryPacketAt)}\n" +
            $"Raw / valid / invalid packets: {diagnostics.RawNotificationCount} / {diagnostics.ValidPacketCount} / {diagnostics.InvalidPacketCount}\n" +
            $"HR packets: {diagnostics.HeartRatePacketCount}\n" +
            $"Battery packets: {diagnostics.BatteryPacketCount}\n" +
            $"HR poll TX: {diagnostics.HeartRatePollTxCount}\n" +
            $"HR poll write failures: {diagnostics.HeartRatePollWriteFailureCount}\n" +
            $"Consecutive HR poll failures: {diagnostics.ConsecutiveHeartRatePollFailures}\n" +
            $"Battery poll TX: {diagnostics.BatteryPollTxCount}\n" +
            $"Battery poll write failures: {diagnostics.BatteryPollWriteFailureCount}\n" +
            $"Consecutive battery poll failures: {diagnostics.ConsecutiveBatteryPollFailures}";
    }
}
