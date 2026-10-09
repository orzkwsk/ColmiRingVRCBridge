#if DEBUG
using System.IO;
using System.Text.Json;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge.Tests;

public sealed class DebugTelemetryLoggerTests
{
    [Fact]
    public async Task ImmediateDisposeDrainsQueuedEventsAndPreventsRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ColmiRingVRCBridge-tests-" + Guid.NewGuid());
        try
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var logger = new DebugTelemetryLogger(() => new { bpm = 72 }, TimeSpan.FromHours(1),
                    Path.Combine(directory, attempt.ToString()));
                logger.Start();
                for (var i = 0; i < 10; i++)
                {
                    logger.LogEvent("test_event", new { sequence = i });
                }

                await logger.DisposeAsync();
                var events = (await File.ReadAllLinesAsync(logger.LogPath)).Select(line =>
                {
                    using var entry = JsonDocument.Parse(line);
                    return entry.RootElement.GetProperty("Type").GetString();
                }).ToArray();
                Assert.Equal(10, events.Count(type => type == "test_event"));
                Assert.Contains("logger_stopping", events);
                Assert.Throws<ObjectDisposedException>(logger.Start);
                await logger.DisposeAsync();
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
#endif
