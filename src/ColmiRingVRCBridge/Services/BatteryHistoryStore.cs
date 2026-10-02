using System.Globalization;
using System.IO;
using ColmiRingVRCBridge.Models;

namespace ColmiRingVRCBridge.Services;

internal readonly record struct BatteryHistorySample(DateTimeOffset Timestamp, int Percent, bool Charging);

internal sealed class BatteryHistoryStore
{
    private static readonly TimeSpan UnchangedSampleInterval = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly List<BatteryHistorySample> _samples = new();
    private readonly TimeSpan _retention;
    private readonly string _filePath;

    public BatteryHistoryStore(ulong bluetoothAddress, TimeSpan retention)
    {
        if (retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention));
        }

        _retention = retention;
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ColmiRingVRCBridge");
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, $"battery-{bluetoothAddress:X12}.csv");
        Load();
    }

    public void Add(BatteryState battery, DateTimeOffset timestamp)
    {
        BatteryHistorySample[] persistenceSnapshot;

        lock (_gate)
        {
            var sample = new BatteryHistorySample(timestamp, Math.Clamp(battery.Percent, 0, 100), battery.Charging);
            PruneLocked(timestamp - _retention);

            if (_samples.Count > 0)
            {
                var last = _samples[^1];
                var unchanged = last.Percent == sample.Percent && last.Charging == sample.Charging;
                if (unchanged && sample.Timestamp - last.Timestamp < UnchangedSampleInterval)
                {
                    return;
                }
            }

            _samples.Add(sample);
            persistenceSnapshot = _samples.ToArray();
        }

        BatteryHistoryPersistenceQueue.Enqueue(_filePath, persistenceSnapshot);
    }

    public IReadOnlyList<BatteryHistorySample> Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            PruneLocked(now - _retention);
            return _samples.ToArray();
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var cutoff = DateTimeOffset.Now - _retention;
            foreach (var line in File.ReadLines(_filePath))
            {
                var parts = line.Split(',');
                if (parts.Length != 3 ||
                    !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixMilliseconds) ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent) ||
                    !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var chargingValue))
                {
                    continue;
                }

                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
                if (timestamp < cutoff)
                {
                    continue;
                }

                _samples.Add(new BatteryHistorySample(
                    timestamp,
                    Math.Clamp(percent, 0, 100),
                    chargingValue != 0));
            }

            _samples.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        }
        catch
        {
            _samples.Clear();
        }
    }

    private void PruneLocked(DateTimeOffset cutoff)
    {
        var removeCount = 0;
        while (removeCount < _samples.Count && _samples[removeCount].Timestamp < cutoff)
        {
            removeCount++;
        }

        if (removeCount > 0)
        {
            _samples.RemoveRange(0, removeCount);
        }
    }
}
