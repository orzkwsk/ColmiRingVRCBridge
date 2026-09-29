#if DEBUG
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge;

public partial class MainWindow
{
    private static readonly TimeSpan DebugTelemetryLogInterval = TimeSpan.FromMinutes(1);
    private DebugTelemetryLogger? _debugTelemetryLogger;

    private void InitializeDebugTelemetryLogging()
    {
        try
        {
            _debugTelemetryLogger = new DebugTelemetryLogger(
                CreateDebugTelemetrySnapshot,
                DebugTelemetryLogInterval);
            _debugTelemetryLogger.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to initialize debug telemetry logging: {ex}");
            _debugTelemetryLogger = null;
        }
    }

    private object CreateDebugTelemetrySnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var diagnostics = _ringService.GetTelemetryDiagnostics();
        var heartRate = Volatile.Read(ref _heartRateSnapshot);
        var battery = Volatile.Read(ref _batterySnapshot);

        return new
        {
            linkConnected = _ringService.IsConnected,
            connectionOperation = _connectionOperation.ToString(),
            autoReconnectEnabled = AutoReconnectEnabled,
            telemetryState = _ringService.GetTelemetryState(now, HeartRateFreshnessThreshold).ToString(),
            heartRate = new
            {
                bpm = heartRate.Bpm,
                timestampUtc = heartRate.Timestamp,
                freshness = heartRate.GetFreshness(now, HeartRateFreshnessThreshold).ToString(),
                ageSeconds = AgeSeconds(now, heartRate.Timestamp)
            },
            battery = new
            {
                percent = battery.Battery?.Percent,
                charging = battery.Battery?.Charging,
                timestampUtc = battery.Timestamp,
                freshness = battery.GetFreshness(now, BatteryFreshnessThreshold).ToString(),
                ageSeconds = AgeSeconds(now, battery.Timestamp)
            },
            diagnostics = new
            {
                diagnostics.LastRawNotificationAt,
                diagnostics.LastValidPacketAt,
                diagnostics.LastValidHeartRateAt,
                diagnostics.LastBatteryPollAt,
                diagnostics.LastBatteryPacketAt,
                diagnostics.RawNotificationCount,
                diagnostics.InvalidPacketCount,
                diagnostics.ValidPacketCount,
                diagnostics.HeartRatePacketCount,
                diagnostics.BatteryPacketCount,
                diagnostics.HeartRatePollTxCount,
                diagnostics.HeartRatePollWriteFailureCount,
                diagnostics.ConsecutiveHeartRatePollFailures,
                diagnostics.BatteryPollTxCount,
                diagnostics.BatteryPollWriteFailureCount,
                diagnostics.ConsecutiveBatteryPollFailures,
                diagnostics.HeartRateSessionStarted
            }
        };
    }

    private void LogDebugTelemetryEvent(string type, object? data = null)
    {
        _debugTelemetryLogger?.LogEvent(type, data);
    }

    private async Task ShutdownDebugTelemetryLoggingAsync()
    {
        var logger = _debugTelemetryLogger;
        _debugTelemetryLogger = null;
        if (logger is not null)
        {
            await logger.DisposeAsync();
        }
    }

    private static double? AgeSeconds(DateTimeOffset now, DateTimeOffset? timestamp)
    {
        return timestamp.HasValue
            ? Math.Max(0, (now - timestamp.Value).TotalSeconds)
            : null;
    }
}
#endif
