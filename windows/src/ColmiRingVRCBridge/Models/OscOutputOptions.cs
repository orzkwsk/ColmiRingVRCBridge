using System.Globalization;

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
    private const string Prefix = "/avatar/parameters/";
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    // Retain the positional-record API (named arguments, deconstruction and with).
    public OscValueType ValueType { get; init; } = ValidateValueType(ValueType, Scaling);

    internal void Validate()
    {
        ValidateValueType(ValueType, Scaling);
        if (Uri.CheckHostName(Host) == UriHostNameType.Unknown)
            throw new ArgumentException("Enter a DNS name or IP address for the OSC target.");
        if (Port is < 1 or > 65535)
            throw new ArgumentException("OSC port must be between 1 and 65535.");
        if (OscAddress.Length <= Prefix.Length || OscAddress.Any(char.IsControl))
            throw new ArgumentException("Enter a non-empty OSC parameter name without control characters.");
        if (Interval < MinimumInterval || Interval > MaximumInterval)
            throw new ArgumentException("OSC output interval is outside the supported range.");
    }

    private static OscValueType ValidateValueType(OscValueType valueType, ScalingMode scaling)
    {
        if (!Enum.IsDefined(valueType) || !Enum.IsDefined(scaling))
            throw new ArgumentException("Select a valid OSC parameter type and scaling.");
        if (valueType == OscValueType.Int && scaling == ScalingMode.Normalize255)
        {
            throw new ArgumentException("Integer OSC output must use raw BPM scaling.", nameof(scaling));
        }

        return valueType;
    }

    internal static OscOutputOptions FromInput(string host, string port, string parameterName,
        OscValueType type, ScalingMode scaling, string interval)
    {
        if (!int.TryParse(port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var portNumber))
            throw new ArgumentException("OSC port must be between 1 and 65535.");
        if (!double.TryParse(interval, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < MinimumInterval.TotalSeconds || seconds > MaximumInterval.TotalSeconds)
            throw new ArgumentException("OSC output interval must be a finite, supported value of at least 0.05 seconds.");

        var options = new OscOutputOptions(host.Trim(), portNumber, parameterName, type, scaling, TimeSpan.FromSeconds(seconds));
        options.Validate();
        return options;
    }

    public string OscAddress => FormatAddress(ParameterName);

    internal static string FormatAddress(string parameterName)
    {
        var value = parameterName?.Trim() ?? string.Empty;
        return value.StartsWith(Prefix, StringComparison.Ordinal) ? value : Prefix + value.TrimStart('/');
    }
}
