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

public sealed record OscOutputOptions
{
    public OscOutputOptions(
        string host,
        int port,
        string parameterName,
        OscValueType valueType,
        ScalingMode scaling,
        TimeSpan interval)
    {
        if (valueType == OscValueType.Int && scaling == ScalingMode.Normalize255)
        {
            throw new ArgumentException("Integer OSC output must use raw BPM scaling.", nameof(scaling));
        }

        Host = host;
        Port = port;
        ParameterName = parameterName;
        ValueType = valueType;
        Scaling = scaling;
        Interval = interval;
    }

    public string Host { get; }
    public int Port { get; }
    public string ParameterName { get; }
    public OscValueType ValueType { get; }
    public ScalingMode Scaling { get; }
    public TimeSpan Interval { get; }

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
