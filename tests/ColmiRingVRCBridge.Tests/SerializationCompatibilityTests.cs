using System.Text.Json;
using ColmiRingVRCBridge.Models;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge.Tests;

public sealed class SerializationCompatibilityTests
{
    [Fact]
    public void LegacyReconnectSettingsRetainFieldNamesAndDefaultAutoReconnect()
    {
        var settings = JsonSerializer.Deserialize<ReconnectSettings>(
            """{"BluetoothAddress":123456,"DeviceName":"R06"}""");
        Assert.Equal(new ReconnectSettings(123456, "R06", true), settings);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(settings));
        Assert.Equal(123456UL, json.RootElement.GetProperty("BluetoothAddress").GetUInt64());
        Assert.Equal("R06", json.RootElement.GetProperty("DeviceName").GetString());
        Assert.True(json.RootElement.GetProperty("AutoReconnect").GetBoolean());
    }

    [Fact]
    public void OscOptionsRetainPositionalRecordJsonRoundTrip()
    {
        var options = new OscOutputOptions("127.0.0.1", 9000, "HeartRate",
            OscValueType.Int, ScalingMode.RawBpm, TimeSpan.FromSeconds(3));
        Assert.Equal(options, JsonSerializer.Deserialize<OscOutputOptions>(JsonSerializer.Serialize(options)));
    }
}
