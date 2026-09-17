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
