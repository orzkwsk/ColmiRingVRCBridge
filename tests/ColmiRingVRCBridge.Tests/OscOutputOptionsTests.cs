using ColmiRingVRCBridge.Models;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge.Tests;

public sealed class OscOutputOptionsTests
{
    [Fact]
    public void DevNamedArgumentApiRemainsCompatibleAndValidated()
    {
        var options = new OscOutputOptions(
            host: "127.0.0.1", port: 9000, parameterName: "HeartRate",
            valueType: OscValueType.Int, scaling: ScalingMode.RawBpm,
            interval: TimeSpan.FromSeconds(3));
        Assert.Equal(ScalingMode.RawBpm, options.Scaling);
        Assert.Throws<ArgumentException>(() => new OscOutputOptions(
            host: "127.0.0.1", port: 9000, parameterName: "HeartRate",
            valueType: OscValueType.Int, scaling: ScalingMode.Normalize255,
            interval: TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void PositionalRecordApiSupportsNamedArgumentsDeconstructionAndCopy()
    {
        var options = new OscOutputOptions(
            Host: "127.0.0.1", Port: 9000, ParameterName: "HeartRate",
            ValueType: OscValueType.Float, Scaling: ScalingMode.Normalize255,
            Interval: TimeSpan.FromSeconds(3));
        var (host, port, parameter, type, scaling, interval) = options;
        Assert.Equal(("127.0.0.1", 9000, "HeartRate", OscValueType.Float,
            ScalingMode.Normalize255, TimeSpan.FromSeconds(3)),
            (host, port, parameter, type, scaling, interval));

        // Both property orders retain valid copy behavior.
        var copy = options with { ValueType = OscValueType.Int, Scaling = ScalingMode.RawBpm };
        copy.Validate();
        (options with { Scaling = ScalingMode.RawBpm, ValueType = OscValueType.Int }).Validate();
        Assert.Equal(ScalingMode.Normalize255, options.Scaling);
        Assert.Equal(ScalingMode.RawBpm, copy.Scaling);
    }

    [Fact]
    public async Task InvalidRecordCopyIsRejectedBeforeOutputStarts()
    {
        var options = new OscOutputOptions("127.0.0.1", 9000, "HeartRate",
            OscValueType.Float, ScalingMode.Normalize255, TimeSpan.FromSeconds(3));
        await using var output = new OscOutputService();
        Assert.Throws<ArgumentException>(() => output.Start(options with { ValueType = OscValueType.Int }, () => 72));
        Assert.False(output.IsRunning);
    }

    [Fact]
    public void Constructor_RejectsIntegerNormalization()
    {
        Assert.Throws<ArgumentException>(() => new OscOutputOptions(
            "127.0.0.1",
            9000,
            "HeartRate",
            OscValueType.Int,
            ScalingMode.Normalize255,
            TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Constructor_AllowsRawIntegerBpm()
    {
        var options = new OscOutputOptions(
            "127.0.0.1",
            9000,
            "HeartRate",
            OscValueType.Int,
            ScalingMode.RawBpm,
            TimeSpan.FromSeconds(3));

        Assert.Equal(ScalingMode.RawBpm, options.Scaling);
    }
}
