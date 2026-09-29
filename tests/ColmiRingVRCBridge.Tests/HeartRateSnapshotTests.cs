using ColmiRingVRCBridge.Models;

namespace ColmiRingVRCBridge.Tests;

public sealed class HeartRateSnapshotTests
{
    [Fact]
    public void EmptySnapshot_IsNoSample()
    {
        Assert.Equal(
            HeartRateFreshness.NoSample,
            HeartRateSnapshot.Empty.GetFreshness(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Snapshot_TransitionsFromFreshToStale()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var snapshot = new HeartRateSnapshot(70, timestamp);
        var threshold = TimeSpan.FromSeconds(5);

        Assert.Equal(HeartRateFreshness.Fresh, snapshot.GetFreshness(timestamp.AddSeconds(4), threshold));
        Assert.Equal(HeartRateFreshness.Stale, snapshot.GetFreshness(timestamp.AddSeconds(6), threshold));
        Assert.False(snapshot.TryGetFreshBpm(timestamp.AddSeconds(6), threshold, out _));
    }
}
