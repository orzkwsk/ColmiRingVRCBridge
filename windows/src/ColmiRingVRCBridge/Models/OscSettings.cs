using System.Globalization;

namespace ColmiRingVRCBridge.Models;

internal sealed record OscSettings
{
    public const string DefaultHost = "127.0.0.1";
    public const string DefaultPort = "9000";

    public string Host { get; init; } = DefaultHost;
    public string Port { get; init; } = DefaultPort;
    public string ParameterName { get; init; } = "VRCOSC/Heartrate/Value";
    public OscValueType ValueType { get; init; } = OscValueType.Int;
    public ScalingMode Scaling { get; init; } = ScalingMode.RawBpm;
    public string Interval { get; init; } = "1.0";
    public bool UseFixedBpm { get; init; }
    public string FixedBpm { get; init; } = "50";
    public HeartRateLossMode LossMode { get; init; } = HeartRateLossMode.UseSpecifiedValue;
    public string SpecifiedValue { get; init; } = "0";

    public VrcOutputSettings ToVrcOutputSettings() => new(UseFixedBpm,
        int.TryParse(FixedBpm, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bpm)
            ? Math.Clamp(bpm, 0, 255) : null,
        LossMode, OscOutputValue.Parse(SpecifiedValue, ValueType));
}
