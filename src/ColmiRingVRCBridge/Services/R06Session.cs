using ColmiRingVRCBridge.Models;

namespace ColmiRingVRCBridge.Services;

internal sealed class R06Session : IAsyncDisposable
{
    private const int ObservedLiveHeartRateMinimum = 30;
    private const int ObservedLiveHeartRateMaximum = 220;
    private const byte AlternateRealtimeHeartRateResponseCommand = 0x9E;

    private readonly IColmiTransport _transport;
    private readonly TimeSpan _heartRatePollInterval;
    private readonly TimeSpan _batteryPollInterval;
    private readonly TimeSpan _handshakeDelay;

    private CancellationTokenSource? _sessionCts;
    private Task? _heartRatePollingTask;
    private Task? _batteryPollingTask;
    private bool _transportStarted;
    private int _heartRateSessionStarted;
    private bool _disposed;

    private long _lastRawNotificationUnixMs;
    private long _lastValidPacketUnixMs;
    private long _lastValidHeartRateUnixMs;
    private long _lastBatteryPollUnixMs;
    private long _lastBatteryPacketUnixMs;
    private long _rawNotificationCount;
    private long _invalidPacketCount;
    private long _validPacketCount;
    private long _heartRatePacketCount;
    private long _batteryPacketCount;
    private long _heartRatePollTxCount;
    private long _heartRatePollWriteFailureCount;
    private int _consecutiveHeartRatePollFailures;
    private long _batteryPollTxCount;
    private long _batteryPollWriteFailureCount;
    private int _consecutiveBatteryPollFailures;

    public R06Session(
        IColmiTransport transport,
        TimeSpan? heartRatePollInterval = null,
        TimeSpan? batteryPollInterval = null,
        TimeSpan? handshakeDelay = null)
    {
        _transport = transport;
        _heartRatePollInterval = heartRatePollInterval ?? TimeSpan.FromSeconds(1);
        _batteryPollInterval = batteryPollInterval ?? TimeSpan.FromSeconds(60);
        _handshakeDelay = handshakeDelay ?? TimeSpan.FromMilliseconds(500);
    }

    public event Action<int>? HeartRateUpdated;
    public event Action<BatteryState>? BatteryUpdated;
    public event Action<string>? ProtocolWarning;
    public event Action<HeartRateProtocolProbePacket>? HeartRateProtocolProbePacketObserved;

    public bool IsHeartRateSessionStarted => Volatile.Read(ref _heartRateSessionStarted) != 0;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_sessionCts is not null)
        {
            throw new InvalidOperationException("R06 session is already started.");
        }

        _transport.PacketReceived += Transport_PacketReceived;
        try
        {
            await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
            _transportStarted = true;

            await StartHeartRateSessionAsync(cancellationToken).ConfigureAwait(false);
            await RequestBatteryAsync(cancellationToken).ConfigureAwait(false);

            // The caller token scopes connection/initialization only. Once initialization
            // succeeds, telemetry polling owns an independent lifetime until DisposeAsync().
            _sessionCts = new CancellationTokenSource();
            var sessionToken = _sessionCts.Token;
            _heartRatePollingTask = Task.Run(() => HeartRatePollingLoopAsync(sessionToken));
            _batteryPollingTask = Task.Run(() => BatteryPollingLoopAsync(sessionToken));
        }
        catch
        {
            _transport.PacketReceived -= Transport_PacketReceived;
            throw;
        }
    }

    public async Task StartHeartRateSessionAsync(CancellationToken cancellationToken = default)
    {
        await _transport.WriteAsync(R06Protocol.BuildStartHeartRatePacket(), cancellationToken).ConfigureAwait(false);

        // START has been accepted by the transport. Mark the session active here so a
        // later CONTINUE/cancellation failure can still be cleaned up with STOP.
        Volatile.Write(ref _heartRateSessionStarted, 1);

        await Task.Delay(_handshakeDelay, cancellationToken).ConfigureAwait(false);
        await _transport.WriteAsync(R06Protocol.BuildContinueHeartRatePacket(), cancellationToken).ConfigureAwait(false);
    }

    public Task RebootAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _transport.WriteAsync(R06Protocol.BuildRebootPacket(), cancellationToken);
    }

    public async Task StopHeartRateSessionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsHeartRateSessionStarted)
        {
            return;
        }

        await _transport.WriteAsync(R06Protocol.BuildStopHeartRatePacket(), cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _heartRateSessionStarted, 0);
    }

    public BleTelemetryDiagnostics GetDiagnostics()
    {
        return new BleTelemetryDiagnostics(
            ToNullableTimestamp(Volatile.Read(ref _lastRawNotificationUnixMs)),
            ToNullableTimestamp(Volatile.Read(ref _lastValidPacketUnixMs)),
            ToNullableTimestamp(Volatile.Read(ref _lastValidHeartRateUnixMs)),
            ToNullableTimestamp(Volatile.Read(ref _lastBatteryPollUnixMs)),
            ToNullableTimestamp(Volatile.Read(ref _lastBatteryPacketUnixMs)),
            Interlocked.Read(ref _rawNotificationCount),
            Interlocked.Read(ref _invalidPacketCount),
            Interlocked.Read(ref _validPacketCount),
            Interlocked.Read(ref _heartRatePacketCount),
            Interlocked.Read(ref _batteryPacketCount),
            Interlocked.Read(ref _heartRatePollTxCount),
            Interlocked.Read(ref _heartRatePollWriteFailureCount),
            Volatile.Read(ref _consecutiveHeartRatePollFailures),
            Interlocked.Read(ref _batteryPollTxCount),
            Interlocked.Read(ref _batteryPollWriteFailureCount),
            Volatile.Read(ref _consecutiveBatteryPollFailures),
            IsHeartRateSessionStarted);
    }

    public HeartRateTelemetryState GetTelemetryState(bool linkConnected, DateTimeOffset now, TimeSpan staleAfter)
    {
        if (!linkConnected)
        {
            return HeartRateTelemetryState.Disconnected;
        }

        if (!IsHeartRateSessionStarted)
        {
            return HeartRateTelemetryState.Initializing;
        }

        var lastHeartRate = ToNullableTimestamp(Volatile.Read(ref _lastValidHeartRateUnixMs));
        if (!lastHeartRate.HasValue)
        {
            return HeartRateTelemetryState.Initializing;
        }

        return now - lastHeartRate.Value <= staleAfter
            ? HeartRateTelemetryState.Streaming
            : HeartRateTelemetryState.Stale;
    }

    private void Transport_PacketReceived(byte[] data)
    {
        var now = DateTimeOffset.UtcNow;
        var nowUnixMs = now.ToUnixTimeMilliseconds();
        Interlocked.Exchange(ref _lastRawNotificationUnixMs, nowUnixMs);
        Interlocked.Increment(ref _rawNotificationCount);

        if (!ColmiPacket.IsValid(data))
        {
            Interlocked.Increment(ref _invalidPacketCount);
            ProtocolWarning?.Invoke(data.Length == 16
                ? "Received packet with invalid checksum."
                : $"Unexpected packet length: {data.Length}");
            return;
        }

        Interlocked.Exchange(ref _lastValidPacketUnixMs, nowUnixMs);
        Interlocked.Increment(ref _validPacketCount);

        // Some current QRing-family implementations observe 0x9E (0x1E | 0x80)
        // as the realtime-HR poll response. The R06 bridge does not parse that dialect yet,
        // so capture it verbatim for validation without changing production behavior.
        if (data[0] == AlternateRealtimeHeartRateResponseCommand)
        {
            EmitHeartRateProtocolProbe(
                now,
                data,
                data.Length > 1 ? (int?)data[1] : null,
                "alternate_0x9e_response");
        }

        if (!R06Protocol.TryParse(data, out var packet))
        {
            Interlocked.Increment(ref _invalidPacketCount);
            return;
        }

        if (packet.Battery is { } battery)
        {
            Interlocked.Exchange(ref _lastBatteryPacketUnixMs, nowUnixMs);
            Interlocked.Increment(ref _batteryPacketCount);
            BatteryUpdated?.Invoke(battery);
        }

        if (packet.ErrorCode is { } errorCode)
        {
            ProtocolWarning?.Invoke($"Realtime HR error code: {errorCode}");
        }

        if (packet.HeartRate is { } bpm && bpm > 0)
        {
            if (bpm is < ObservedLiveHeartRateMinimum or > ObservedLiveHeartRateMaximum)
            {
                EmitHeartRateProtocolProbe(
                    now,
                    data,
                    bpm,
                    "candidate_outside_observed_30_220_range");
            }

            // Keep forwarding the candidate unchanged for this capture run. The probe is
            // intentionally observational so we can identify the R06 packet dialect before
            // deciding whether low values are status codes, warm-up values, or true BPM data.
            Interlocked.Exchange(ref _lastValidHeartRateUnixMs, nowUnixMs);
            Interlocked.Increment(ref _heartRatePacketCount);
            HeartRateUpdated?.Invoke(bpm);
        }
    }

    private void EmitHeartRateProtocolProbe(
        DateTimeOffset timestamp,
        byte[] data,
        int? candidateBpm,
        string reason)
    {
        var handler = HeartRateProtocolProbePacketObserved;
        if (handler is null)
        {
            return;
        }

        handler(new HeartRateProtocolProbePacket(
            timestamp,
            data.Length > 0 ? data[0] : (byte)0,
            candidateBpm,
            reason,
            Convert.ToHexString(data)));
    }

    private async Task HeartRatePollingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _transport.WriteAsync(R06Protocol.BuildPollHeartRatePacket(), cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _heartRatePollTxCount);
                Interlocked.Exchange(ref _consecutiveHeartRatePollFailures, 0);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var consecutive = Interlocked.Increment(ref _consecutiveHeartRatePollFailures);
                Interlocked.Increment(ref _heartRatePollWriteFailureCount);

                if (consecutive == 1 || consecutive == 3 || consecutive % 10 == 0)
                {
                    ProtocolWarning?.Invoke($"Heart-rate poll failed ({consecutive} consecutive): {ex.Message}");
                }
            }

            try
            {
                await Task.Delay(_heartRatePollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task RequestBatteryAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _lastBatteryPollUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        try
        {
            await _transport.WriteAsync(R06Protocol.BuildBatteryPacket(), cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _batteryPollTxCount);
            Interlocked.Exchange(ref _consecutiveBatteryPollFailures, 0);
        }
        catch
        {
            Interlocked.Increment(ref _batteryPollWriteFailureCount);
            Interlocked.Increment(ref _consecutiveBatteryPollFailures);
            throw;
        }
    }

    private async Task BatteryPollingLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_batteryPollInterval, cancellationToken).ConfigureAwait(false);
                await RequestBatteryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var consecutive = Volatile.Read(ref _consecutiveBatteryPollFailures);
                if (consecutive == 1 || consecutive == 3 || consecutive % 10 == 0)
                {
                    ProtocolWarning?.Invoke($"Battery poll failed ({consecutive} consecutive): {ex.Message}");
                }
            }
        }
    }

    private async Task StopPollingAsync()
    {
        var cts = _sessionCts;
        var heartRateTask = _heartRatePollingTask;
        var batteryTask = _batteryPollingTask;

        _sessionCts = null;
        _heartRatePollingTask = null;
        _batteryPollingTask = null;

        if (cts is null)
        {
            return;
        }

        cts.Cancel();

        try
        {
            await Task.WhenAll(heartRateTask ?? Task.CompletedTask, batteryTask ?? Task.CompletedTask)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(R06Session));
        }
    }

    private static DateTimeOffset? ToNullableTimestamp(long unixMilliseconds)
    {
        return unixMilliseconds <= 0
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await StopPollingAsync().ConfigureAwait(false);
        }
        finally
        {
            if (_transportStarted && IsHeartRateSessionStarted)
            {
                try
                {
                    using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await StopHeartRateSessionAsync(cleanupCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to stop R06 measurement during disposal: {ex}");
                }
            }

            Volatile.Write(ref _heartRateSessionStarted, 0);
            _transport.PacketReceived -= Transport_PacketReceived;
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
    }
}
