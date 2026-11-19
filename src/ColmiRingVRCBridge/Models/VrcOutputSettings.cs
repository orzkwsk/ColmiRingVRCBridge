using System.Globalization;

namespace ColmiRingVRCBridge.Models;

public enum HeartRateLossMode
{
    HoldLast,
    UseFixedBpm,
    UseSpecifiedValue
}

internal sealed record OscOutputValue(double Value, int? Bpm = null)
{
    public static OscOutputValue FromBpm(int bpm, ScalingMode scaling)
    {
        var clamped = Math.Clamp(bpm, 0, 255);
        return new OscOutputValue(scaling == ScalingMode.Normalize255 ? clamped / 255.0f : clamped, clamped);
    }

    public static OscOutputValue? Parse(string text, OscValueType type)
    {
        if (type == OscValueType.Int)
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)
                ? new OscOutputValue(integer) : null;

        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && float.IsFinite(value)
            ? new OscOutputValue(value) : null;
    }
}

// Published as a single snapshot so the OSC worker sees a consistent set of choices.
internal sealed record VrcOutputSettings(
    bool UseFixedBpm, int? FixedBpm, HeartRateLossMode LossMode, OscOutputValue? SpecifiedValue)
{
    public OscOutputValue? Select(ScalingMode scaling, int? freshBpm, OscOutputValue? lastSent)
    {
        if (UseFixedBpm)
            return FixedBpm is int fixedBpm ? OscOutputValue.FromBpm(fixedBpm, scaling) : null;
        if (freshBpm is int bpm)
            return OscOutputValue.FromBpm(bpm, scaling);

        return SelectForLoss(scaling, lastSent);
    }

    public OscOutputValue? SelectForLoss(ScalingMode scaling, OscOutputValue? lastSent) =>
        LossMode switch
        {
            HeartRateLossMode.HoldLast => lastSent,
            HeartRateLossMode.UseFixedBpm when FixedBpm is int fallback => OscOutputValue.FromBpm(fallback, scaling),
            HeartRateLossMode.UseSpecifiedValue => SpecifiedValue,
            _ => null
        };
}
