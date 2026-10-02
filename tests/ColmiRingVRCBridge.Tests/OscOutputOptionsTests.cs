using ColmiRingVRCBridge.Models;

namespace ColmiRingVRCBridge.Tests;

public sealed class OscOutputOptionsTests
{
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
