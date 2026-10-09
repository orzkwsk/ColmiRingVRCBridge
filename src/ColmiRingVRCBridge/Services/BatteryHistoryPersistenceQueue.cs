using System.Globalization;
using System.IO;
using System.Threading.Channels;

namespace ColmiRingVRCBridge.Services;

internal readonly record struct BatteryHistoryPersistRequest(
    string FilePath,
    BatteryHistorySample[] Samples,
    TaskCompletionSource? Completion = null);

internal static class BatteryHistoryPersistenceQueue
{
    private static readonly Channel<BatteryHistoryPersistRequest> Queue =
        Channel.CreateUnbounded<BatteryHistoryPersistRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private static readonly Task WriterTask = Task.Run(ProcessAsync);

    public static void Enqueue(string filePath, BatteryHistorySample[] samples)
    {
        Queue.Writer.TryWrite(new BatteryHistoryPersistRequest(filePath, samples));
    }

    // A barrier drains preceding writes without closing the process-wide queue.
    // Call after telemetry producers have stopped during application shutdown.
    public static Task FlushAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Queue.Writer.TryWrite(new BatteryHistoryPersistRequest(string.Empty, [], completion)))
        {
            throw new InvalidOperationException("Battery history persistence queue is closed.");
        }

        return completion.Task;
    }

    private static async Task ProcessAsync()
    {
        await foreach (var request in Queue.Reader.ReadAllAsync())
        {
            if (request.Completion is { } completion)
            {
                completion.SetResult();
                continue;
            }

            try
            {
                var tempPath = request.FilePath + ".tmp";
                await using (var stream = new FileStream(
                                 tempPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 4096,
                                 useAsync: true))
                await using (var writer = new StreamWriter(stream))
                {
                    foreach (var sample in request.Samples)
                    {
                        await writer.WriteAsync(sample.Timestamp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
                        await writer.WriteAsync(',');
                        await writer.WriteAsync(sample.Percent.ToString(CultureInfo.InvariantCulture));
                        await writer.WriteAsync(',');
                        await writer.WriteLineAsync(sample.Charging ? "1" : "0");
                    }
                }

                File.Move(tempPath, request.FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                // Diagnostics persistence must never affect live BLE telemetry.
                System.Diagnostics.Debug.WriteLine($"Battery history persistence failed: {ex}");
            }
        }
    }
}
