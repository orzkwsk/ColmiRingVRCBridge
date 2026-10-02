using System.Globalization;
using System.IO;
using System.Threading.Channels;

namespace ColmiRingVRCBridge.Services;

internal readonly record struct BatteryHistoryPersistRequest(
    string FilePath,
    BatteryHistorySample[] Samples);

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

    private static async Task ProcessAsync()
    {
        await foreach (var request in Queue.Reader.ReadAllAsync())
        {
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
            catch
            {
                // Diagnostics persistence must never affect live BLE telemetry.
            }
        }
    }
}
