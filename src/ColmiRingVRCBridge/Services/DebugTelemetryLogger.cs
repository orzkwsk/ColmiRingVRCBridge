#if DEBUG
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;

namespace ColmiRingVRCBridge.Services;

internal sealed class DebugTelemetryLogger : IAsyncDisposable
{
    private static readonly TimeSpan LogRetention = TimeSpan.FromDays(7);

    private readonly Func<object> _snapshotFactory;
    private readonly TimeSpan _snapshotInterval;
    private readonly Channel<DebugLogEntry> _channel = Channel.CreateUnbounded<DebugLogEntry>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private CancellationTokenSource? _cts;
    private Task? _snapshotTask;
    private Task? _writerTask;
    private bool _started;

    public DebugTelemetryLogger(Func<object> snapshotFactory, TimeSpan snapshotInterval)
    {
        _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        if (snapshotInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(snapshotInterval));
        }

        _snapshotInterval = snapshotInterval;

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ColmiRingVRCBridge",
            "debug-logs");
        Directory.CreateDirectory(directory);
        PruneOldLogs(directory);

        LogPath = Path.Combine(
            directory,
            $"telemetry-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jsonl");
    }

    public string LogPath { get; }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _cts = new CancellationTokenSource();
        _writerTask = Task.Run(() => WriterLoopAsync(_cts.Token));
        _snapshotTask = Task.Run(() => SnapshotLoopAsync(_cts.Token));

        LogEvent("logger_started", new
        {
            snapshotIntervalSeconds = _snapshotInterval.TotalSeconds,
            logPath = LogPath
        });
    }

    public void LogEvent(string type, object? data = null)
    {
        if (!_started || string.IsNullOrWhiteSpace(type))
        {
            return;
        }

        _channel.Writer.TryWrite(new DebugLogEntry(DateTimeOffset.UtcNow, type, data));
    }

    private async Task SnapshotLoopAsync(CancellationToken cancellationToken)
    {
        TryQueueSnapshot();

        using var timer = new PeriodicTimer(_snapshotInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                TryQueueSnapshot();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void TryQueueSnapshot()
    {
        try
        {
            LogEvent("telemetry_snapshot", _snapshotFactory());
        }
        catch (Exception ex)
        {
            LogEvent("snapshot_error", new
            {
                exception = ex.GetType().FullName,
                ex.Message
            });
        }
    }

    private async Task WriterLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                LogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            await using var writer = new StreamWriter(stream)
            {
                AutoFlush = true
            };

            await foreach (var entry in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var line = JsonSerializer.Serialize(entry);
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown. Any entries already flushed remain available for diagnosis.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Debug telemetry logger stopped: {ex}");
        }
    }

    private static void PruneOldLogs(string directory)
    {
        try
        {
            var cutoffUtc = DateTime.UtcNow - LogRetention;
            foreach (var path in Directory.EnumerateFiles(directory, "telemetry-*.jsonl"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoffUtc)
                    {
                        File.Delete(path);
                    }
                }
                catch
                {
                    // Debug logging must never interfere with the bridge.
                }
            }
        }
        catch
        {
            // Debug logging must never interfere with the bridge.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_started)
        {
            return;
        }

        LogEvent("logger_stopping");
        _started = false;

        var cts = _cts;
        var snapshotTask = _snapshotTask;
        var writerTask = _writerTask;
        _cts = null;
        _snapshotTask = null;
        _writerTask = null;

        if (cts is not null)
        {
            cts.Cancel();
        }

        if (snapshotTask is not null)
        {
            try
            {
                await snapshotTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _channel.Writer.TryComplete();

        if (writerTask is not null)
        {
            try
            {
                await writerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts?.Dispose();
    }

    private sealed record DebugLogEntry(
        DateTimeOffset TimestampUtc,
        string Type,
        object? Data);
}
#endif
