using ColmiRingVRCBridge.Models;

namespace ColmiRingVRCBridge.Services;

internal sealed class OscOutputService : IAsyncDisposable
{
    private readonly object _lifecycleGate = new();
    private readonly Func<string, int, IOscSender> _senderFactory;
    private readonly Func<Func<Task>, Task> _scheduleWorker;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Task? _stopTask;
    private bool _disposed;

    public OscOutputService()
        : this((host, port) => new OscSender(host, port), work => Task.Run(work))
    {
    }

    // These seams let tests hold worker startup/send completion without BLE or UI.
    internal OscOutputService(
        Func<string, int, IOscSender> senderFactory,
        Func<Func<Task>, Task> scheduleWorker)
    {
        _senderFactory = senderFactory;
        _scheduleWorker = scheduleWorker;
    }

    public event Action<int, double>? OutputSent;
    public event Action<Exception>? OutputError;

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _cts is not null;
            }
        }
    }

    public void Start(OscOutputOptions options, Func<int?> bpmProvider)
    {
        // A record copy/object initializer can change the constructor-validated pair.
        options.Validate();
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cts is not null)
            {
                throw new InvalidOperationException("OSC output is already running or stopping.");
            }

            var cts = new CancellationTokenSource();
            var token = cts.Token;
            _cts = cts;
            try
            {
                _loopTask = _scheduleWorker(() => RunAsync(options, bpmProvider, token));
            }
            catch
            {
                _cts = null;
                cts.Dispose();
                throw;
            }
        }
    }

    public async Task StopAsync()
    {
        (CancellationTokenSource Cts, Task Worker, TaskCompletionSource Completion)? stopOwner = null;
        Task stopTask;
        lock (_lifecycleGate)
        {
            if (_cts is null)
            {
                return;
            }

            if (_stopTask is { } stopping)
            {
                stopTask = stopping;
            }
            else
            {
                var worker = _loopTask
                    ?? throw new InvalidOperationException("OSC lifetime has no worker task.");
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                stopOwner = (_cts, worker, completion);
                stopTask = _stopTask = completion.Task;
            }
        }

        // One Stop caller owns cleanup. Other callers join its completion;
        // Start cannot replace this lifetime until its worker and sender are gone.
        if (stopOwner is { } owner)
        {
            Exception? failure = null;
            try
            {
                try
                {
                    owner.Cts.Cancel();
                }
                finally
                {
                    // Even a throwing cancellation callback must not abandon the worker.
                    await owner.Worker.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (owner.Cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                owner.Cts.Dispose();
                lock (_lifecycleGate)
                {
                    _cts = null;
                    _loopTask = null;
                    _stopTask = null;
                }
            }

            if (failure is null)
            {
                owner.Completion.SetResult();
            }
            else
            {
                owner.Completion.SetException(failure);
            }
        }

        await stopTask.ConfigureAwait(false);
    }

    private async Task RunAsync(OscOutputOptions options, Func<int?> bpmProvider, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sender = _senderFactory(options.Host, options.Port);
            while (!cancellationToken.IsCancellationRequested)
            {
                var bpm = bpmProvider();
                if (bpm.HasValue)
                {
                    var clampedBpm = Math.Clamp(bpm.Value, 0, 255);
                    double outputValue;

                    if (options.ValueType == OscValueType.Float)
                    {
                        var value = options.Scaling == ScalingMode.Normalize255
                            ? clampedBpm / 255.0f
                            : (float)clampedBpm;
                        outputValue = value;
                        await sender.SendFloatAsync(options.OscAddress, value).ConfigureAwait(false);
                    }
                    else
                    {
                        if (options.Scaling != ScalingMode.RawBpm)
                        {
                            throw new InvalidOperationException("Integer OSC output requires raw BPM scaling.");
                        }

                        outputValue = clampedBpm;
                        await sender.SendIntAsync(options.OscAddress, clampedBpm).ConfigureAwait(false);
                    }

                    OutputSent?.Invoke(clampedBpm, outputValue);
                }

                await Task.Delay(options.Interval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            OutputError?.Invoke(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            _disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
    }
}
