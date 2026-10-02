namespace ColmiRingVRCBridge.Models;

internal enum HeartRateFreshness
{
    NoSample,
    Fresh,
    Stale
}

internal sealed record HeartRateSnapshot(int? Bpm, DateTimeOffset? Timestamp)
{
    public static HeartRateSnapshot Empty { get; } = new(null, null);

    public HeartRateFreshness GetFreshness(DateTimeOffset now, TimeSpan staleAfter)
    {
        if (!Bpm.HasValue || !Timestamp.HasValue)
        {
            return HeartRateFreshness.NoSample;
        }

        return now - Timestamp.Value <= staleAfter
            ? HeartRateFreshness.Fresh
            : HeartRateFreshness.Stale;
    }

    public bool TryGetFreshBpm(DateTimeOffset now, TimeSpan staleAfter, out int bpm)
    {
        if (GetFreshness(now, staleAfter) == HeartRateFreshness.Fresh && Bpm.HasValue)
        {
            bpm = Bpm.Value;
            return true;
        }

        bpm = default;
        return false;
    }
}
