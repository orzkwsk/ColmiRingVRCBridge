using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge.Tests;

public sealed class ColmiPacketTests
{
    [Fact]
    public void Build_CreatesValid16BytePacket()
    {
        var packet = ColmiPacket.Build(0x03, 0x11, 0x22);

        Assert.Equal(16, packet.Length);
        Assert.Equal(0x03, packet[0]);
        Assert.Equal(0x11, packet[1]);
        Assert.Equal(0x22, packet[2]);
        Assert.True(ColmiPacket.IsValid(packet));
    }

    [Fact]
    public void IsValid_RejectsModifiedChecksum()
    {
        var packet = ColmiPacket.Build(0x1E, 0x33);
        packet[4] ^= 0x01;

        Assert.False(ColmiPacket.IsValid(packet));
    }

    [Fact]
    public void IsValid_RejectsWrongLength()
    {
        Assert.False(ColmiPacket.IsValid(new byte[15]));
        Assert.False(ColmiPacket.IsValid(new byte[17]));
    }
}
