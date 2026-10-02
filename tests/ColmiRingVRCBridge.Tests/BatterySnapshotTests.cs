using ColmiRingVRCBridge.Models;

namespace ColmiRingVRCBridge.Tests;

public sealed class BatterySnapshotTests
{
    [Fact]
    public void EmptySnapshot_IsNoSample()
    {
        Assert.Equal(
            BatteryFreshness.NoSample,
            BatterySnapshot.Empty.GetFreshness(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void Snapshot_TransitionsFromFreshToStale()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var snapshot = new BatterySnapshot(new BatteryState(87, false), timestamp);
        var threshold = TimeSpan.FromMinutes(3);

        Assert.Equal(BatteryFreshness.Fresh, snapshot.GetFreshness(timestamp.AddMinutes(2), threshold));
        Assert.Equal(BatteryFreshness.Stale, snapshot.GetFreshness(timestamp.AddMinutes(4), threshold));
    }
}
