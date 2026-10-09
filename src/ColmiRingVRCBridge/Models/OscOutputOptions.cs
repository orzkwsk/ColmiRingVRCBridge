using System.Text.Json.Serialization;

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

[method: JsonConstructor]
public sealed record OscOutputOptions(
    string Host,
    int Port,
    string ParameterName,
    OscValueType ValueType,
    ScalingMode Scaling,
    TimeSpan Interval)
{
    // origin/dev used lowercase parameter names. Keep those named-argument calls
    // source-compatible as well as the original positional-record API. The optional
    // marker only distinguishes overload signatures; validation is always applied.
    public OscOutputOptions(
        string host,
        int port,
        string parameterName,
        OscValueType valueType,
        ScalingMode scaling,
        TimeSpan interval,
        bool legacyNamedArguments = true)
        : this(host, port, parameterName, valueType, scaling, interval)
    {
    }

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
