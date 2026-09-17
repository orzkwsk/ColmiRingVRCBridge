using ColmiRingVRCBridge.Models;

namespace ColmiRingVRCBridge.Services;

internal sealed class OscOutputService : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public event Action<int, double>? OutputSent;
    public event Action<Exception>? OutputError;

    public bool IsRunning => _cts is not null;

    public void Start(OscOutputOptions options, Func<int?> bpmProvider)
    {
        if (_cts is not null)
        {
            throw new InvalidOperationException("OSC output is already running.");
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunAsync(options, bpmProvider, _cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        var cts = _cts;
        var loopTask = _loopTask;
        _cts = null;
        _loopTask = null;

        cts.Cancel();
        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        cts.Dispose();
    }

    private async Task RunAsync(OscOutputOptions options, Func<int?> bpmProvider, CancellationToken cancellationToken)
    {
        try
        {
            using var sender = new OscSender(options.Host, options.Port);
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
                        var value = options.Scaling == ScalingMode.Normalize255
                            ? (int)Math.Round(clampedBpm / 255.0, MidpointRounding.AwayFromZero)
                            : clampedBpm;
                        outputValue = value;
                        await sender.SendIntAsync(options.OscAddress, value).ConfigureAwait(false);
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
        await StopAsync().ConfigureAwait(false);
    }
}
