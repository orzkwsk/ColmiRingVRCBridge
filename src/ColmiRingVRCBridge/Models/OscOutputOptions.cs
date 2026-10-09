namespace ColmiRingVRCBridge.Models;

public enum OscValueType
{
    Float,
    Int
}

public enum ScalingMode
{
    Normalize255,
    RawBpm
}

public sealed record OscOutputOptions(
    string Host,
    int Port,
    string ParameterName,
    OscValueType ValueType,
    ScalingMode Scaling,
    TimeSpan Interval)
{
    // Retain the positional-record API (named arguments, deconstruction and with).
    public OscValueType ValueType { get; init; } = ValidateValueType(ValueType, Scaling);

    internal void Validate() => ValidateValueType(ValueType, Scaling);

    private static OscValueType ValidateValueType(OscValueType valueType, ScalingMode scaling)
    {
        if (valueType == OscValueType.Int && scaling == ScalingMode.Normalize255)
        {
            throw new ArgumentException("Integer OSC output must use raw BPM scaling.", nameof(scaling));
        }

        return valueType;
    }

    public string OscAddress
    {
        get
        {
            var value = ParameterName.Trim();
            if (value.StartsWith("/avatar/parameters/", StringComparison.Ordinal))
            {
                return value;
            }

            value = value.TrimStart('/');
            return $"/avatar/parameters/{value}";
        }
    }
}
