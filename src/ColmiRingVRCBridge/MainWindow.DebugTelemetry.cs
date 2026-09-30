#if DEBUG
using ColmiRingVRCBridge.Models;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge;

public partial class MainWindow
{
    private static readonly TimeSpan DebugTelemetryLogInterval = TimeSpan.FromMinutes(1);
    private DebugTelemetryLogger? _debugTelemetryLogger;
    private bool _debugTelemetryLoggingInitialized;

    private void InitializeDebugTelemetryLogging()
    {
        if (_debugTelemetryLoggingInitialized)
        {
            return;
        }

        _debugTelemetryLoggingInitialized = true;

        try
        {
            _debugTelemetryLogger = new DebugTelemetryLogger(
                CreateDebugTelemetrySnapshot,
                DebugTelemetryLogInterval);
            _debugTelemetryLogger.Start();

            _ringService.ConnectionChanged += DebugTelemetry_ConnectionChanged;
            _ringService.BatteryUpdated += DebugTelemetry_BatteryUpdated;
            _ringService.ProtocolWarning += DebugTelemetry_ProtocolWarning;
            _ringService.HeartRateProtocolProbePacketObserved += DebugTelemetry_HeartRateProtocolProbePacketObserved;
            Closed += MainWindow_DebugTelemetryClosed;
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

    private void DebugTelemetry_ConnectionChanged(bool connected)
    {
        LogDebugTelemetryEvent("connection_changed", new { connected });
    }

    private void DebugTelemetry_BatteryUpdated(BatteryState battery)
    {
        LogDebugTelemetryEvent("battery_updated", new
        {
            percent = battery.Percent,
            charging = battery.Charging
        });
    }

    private void DebugTelemetry_ProtocolWarning(string message)
    {
        LogDebugTelemetryEvent("protocol_warning", new { message });
    }

    private void DebugTelemetry_HeartRateProtocolProbePacketObserved(HeartRateProtocolProbePacket packet)
    {
        var diagnostics = _ringService.GetTelemetryDiagnostics();
        LogDebugTelemetryEvent("hr_protocol_probe", new
        {
            packet.TimestampUtc,
            command = $"0x{packet.Command:X2}",
            packet.CandidateBpm,
            packet.Reason,
            packet.RawHex,
            linkConnected = _ringService.IsConnected,
            diagnostics = new
            {
                diagnostics.LastRawNotificationAt,
                diagnostics.LastValidPacketAt,
                diagnostics.LastValidHeartRateAt,
                diagnostics.RawNotificationCount,
                diagnostics.ValidPacketCount,
                diagnostics.HeartRatePacketCount,
                diagnostics.HeartRatePollTxCount,
                diagnostics.HeartRatePollWriteFailureCount,
                diagnostics.ConsecutiveHeartRatePollFailures,
                diagnostics.HeartRateSessionStarted
            }
        });
    }

    private void LogDebugTelemetryEvent(string type, object? data = null)
    {
        _debugTelemetryLogger?.LogEvent(type, data);
    }

    private async void MainWindow_DebugTelemetryClosed(object? sender, EventArgs e)
    {
        Closed -= MainWindow_DebugTelemetryClosed;
        _ringService.ConnectionChanged -= DebugTelemetry_ConnectionChanged;
        _ringService.BatteryUpdated -= DebugTelemetry_BatteryUpdated;
        _ringService.ProtocolWarning -= DebugTelemetry_ProtocolWarning;
        _ringService.HeartRateProtocolProbePacketObserved -= DebugTelemetry_HeartRateProtocolProbePacketObserved;
        await ShutdownDebugTelemetryLoggingAsync();
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
