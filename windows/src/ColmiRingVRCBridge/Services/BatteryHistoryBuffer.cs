using ColmiRingVRCBridge.Models;

namespace ColmiRingVRCBridge.Services;

internal readonly record struct BatteryHistorySample(DateTimeOffset Timestamp, int Percent, bool Charging);

internal sealed class BatteryHistoryBuffer
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    // A one-minute poll gives 1,440 samples per day. Allow room for extra samples on reconnect.
    private const int Capacity = 2048;
    private static readonly TimeSpan UnchangedSampleInterval = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly BatteryHistorySample[] _samples = new BatteryHistorySample[Capacity];
    private int _start;
    private int _count;

    public void Add(BatteryState battery, DateTimeOffset timestamp)
    {
        lock (_gate)
        {
            PruneLocked(timestamp - Retention);
            var sample = new BatteryHistorySample(timestamp, Math.Clamp(battery.Percent, 0, 100), battery.Charging);

            if (_count > 0)
            {
                var last = _samples[(_start + _count - 1) % Capacity];
                var unchanged = last.Percent == sample.Percent && last.Charging == sample.Charging;
                if (unchanged && sample.Timestamp - last.Timestamp < UnchangedSampleInterval)
                    return;
            }

            _samples[(_start + _count) % Capacity] = sample;
            if (_count == Capacity)
                _start = (_start + 1) % Capacity;
            else
                _count++;
        }
    }

    public IReadOnlyList<BatteryHistorySample> Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneLocked(now - Retention);
            if (_count == 0) return [];

            var snapshot = new BatteryHistorySample[_count];
            for (var i = 0; i < _count; i++)
                snapshot[i] = _samples[(_start + i) % Capacity];
            return snapshot;
        }
    }

    private void PruneLocked(DateTimeOffset cutoff)
    {
        while (_count > 0 && _samples[_start].Timestamp < cutoff)
        {
            _start = (_start + 1) % Capacity;
            _count--;
        }
    }
}
