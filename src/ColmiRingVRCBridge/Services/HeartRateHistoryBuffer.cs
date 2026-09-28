namespace ColmiRingVRCBridge.Services;

internal readonly record struct HeartRateSample(DateTimeOffset Timestamp, int Bpm);

internal sealed class HeartRateHistoryBuffer
{
    private readonly object _gate = new();
    private readonly Queue<HeartRateSample> _samples = new();
    private readonly TimeSpan _retention;

    public HeartRateHistoryBuffer(TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention));
        }

        _retention = retention;
    }

    public void Add(int bpm, DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            _samples.Enqueue(new HeartRateSample(timestamp, bpm));
            PruneLocked(timestamp - _retention);
        }
    }

    public IReadOnlyList<HeartRateSample> Snapshot(DateTimeOffset since)
    {
        lock (_gate)
        {
            PruneLocked(DateTimeOffset.Now - _retention);
            return _samples.Where(sample => sample.Timestamp >= since).ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _samples.Clear();
        }
    }

    private void PruneLocked(DateTimeOffset cutoff)
    {
        while (_samples.Count > 0 && _samples.Peek().Timestamp < cutoff)
        {
            _samples.Dequeue();
        }
    }
}
