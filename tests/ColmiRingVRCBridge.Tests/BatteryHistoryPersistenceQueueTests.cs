using System.IO;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge.Tests;

public sealed class BatteryHistoryPersistenceQueueTests
{
    [Fact]
    public async Task Flush_DrainsSnapshotsInOrderAndPreservesCsvFormat()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ColmiRingVRCBridge-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "battery.csv");
        try
        {
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000);
            BatteryHistoryPersistenceQueue.Enqueue(path, [new(timestamp, 80, false)]);
            BatteryHistoryPersistenceQueue.Enqueue(path,
                [new(timestamp, 80, false), new(timestamp.AddMinutes(1), 81, true)]);
            await BatteryHistoryPersistenceQueue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(new[] { "1700000000000,80,0", "1700000060000,81,1" }, await File.ReadAllLinesAsync(path));
            Assert.False(File.Exists(path + ".tmp"));

            // Flushing must not close the shared writer for a later producer.
            BatteryHistoryPersistenceQueue.Enqueue(path, [new(timestamp.AddMinutes(2), 82, true)]);
            await BatteryHistoryPersistenceQueue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "1700000120000,82,1" }, await File.ReadAllLinesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedWriteDoesNotPreventSubsequentWriteOrFlush()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ColmiRingVRCBridge-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000);
            BatteryHistoryPersistenceQueue.Enqueue(Path.Combine(directory, "missing", "battery.csv"), [new(timestamp, 1, false)]);
            var path = Path.Combine(directory, "battery.csv");
            BatteryHistoryPersistenceQueue.Enqueue(path, [new(timestamp, 50, false)]);
            await BatteryHistoryPersistenceQueue.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "1700000000000,50,0" }, await File.ReadAllLinesAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
