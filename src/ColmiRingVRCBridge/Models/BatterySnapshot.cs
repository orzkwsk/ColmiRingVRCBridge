namespace ColmiRingVRCBridge.Models;

internal enum BatteryFreshness
{
    NoSample,
    Fresh,
    Stale
}

internal sealed record BatterySnapshot(BatteryState? Battery, DateTimeOffset? Timestamp)
{
    public static BatterySnapshot Empty { get; } = new(null, null);

    public BatteryFreshness GetFreshness(DateTimeOffset now, TimeSpan staleAfter)
    {
        if (Battery is null || !Timestamp.HasValue)
        {
            return BatteryFreshness.NoSample;
        }

        return now - Timestamp.Value <= staleAfter
            ? BatteryFreshness.Fresh
            : BatteryFreshness.Stale;
    }

    public bool TryGetFresh(DateTimeOffset now, TimeSpan staleAfter, out BatteryState? battery)
    {
        battery = Battery;
        return GetFreshness(now, staleAfter) == BatteryFreshness.Fresh;
    }
}
