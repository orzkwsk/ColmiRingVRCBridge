#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
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
            LogDebugTelemetryEvent("app_start", CreateDebugBuildIdentity());

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

    private static object CreateDebugBuildIdentity()
    {
        var assembly = typeof(MainWindow).Assembly;
        var assemblyName = assembly.GetName();
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var configuration = assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?
            .Configuration;

        var executablePath = Environment.ProcessPath;
        string? executableFileVersion = null;
        DateTimeOffset? executableLastWriteUtc = null;
        string? executableSha256 = null;

        if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
        {
            try
            {
                executableFileVersion = FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
                executableLastWriteUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(executablePath), TimeSpan.Zero);

                using var stream = File.OpenRead(executablePath);
                executableSha256 = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to collect debug build identity: {ex}");
            }
        }

        return new
        {
            appVersion = assemblyName.Version?.ToString(),
            informationalVersion,
            configuration,
            executableFileVersion,
            executableLastWriteUtc,
            executableSha256,
            capabilities = new[]
            {
                "hr_protocol_probe_v1",
                "battery_freshness_v1",
                "telemetry_liveness_v1"
            }
        };
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
