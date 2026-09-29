using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge.Tests;

public sealed class R06ProtocolTests
{
    [Fact]
    public void TryParse_ParsesBatteryPacket()
    {
        var raw = ColmiPacket.Build(ColmiPacket.CommandBattery, 82, 1);

        Assert.True(R06Protocol.TryParse(raw, out var packet));
        Assert.Equal(R06PacketKind.Battery, packet.Kind);
        Assert.NotNull(packet.Battery);
        Assert.Equal(82, packet.Battery!.Percent);
        Assert.True(packet.Battery.Charging);
    }

    [Fact]
    public void TryParse_ParsesRealtimePollHeartRate()
    {
        var raw = ColmiPacket.Build(ColmiPacket.CommandRealtimeHeartRate, 72);

        Assert.True(R06Protocol.TryParse(raw, out var packet));
        Assert.Equal(R06PacketKind.HeartRate, packet.Kind);
        Assert.Equal(72, packet.HeartRate);
    }

    [Fact]
    public void TryParse_ParsesRealtimeSessionHeartRate()
    {
        var raw = ColmiPacket.Build(
            ColmiPacket.CommandStartRealtime,
            ColmiPacket.RealtimeHeartRate,
            0x00,
            88);

        Assert.True(R06Protocol.TryParse(raw, out var packet));
        Assert.Equal(R06PacketKind.RealtimeStatus, packet.Kind);
        Assert.Equal(88, packet.HeartRate);
        Assert.Null(packet.ErrorCode);
    }

    [Fact]
    public void TryParse_ReportsRealtimeSessionError()
    {
        var raw = ColmiPacket.Build(
            ColmiPacket.CommandStartRealtime,
            ColmiPacket.RealtimeHeartRate,
            0x04,
            0x00);

        Assert.True(R06Protocol.TryParse(raw, out var packet));
        Assert.Equal((byte)0x04, packet.ErrorCode);
        Assert.Null(packet.HeartRate);
    }

    [Fact]
    public void TryParse_RejectsMalformedPacket()
    {
        var raw = ColmiPacket.Build(ColmiPacket.CommandBattery, 50, 0);
        raw[15] ^= 0xFF;

        Assert.False(R06Protocol.TryParse(raw, out _));
    }
}
