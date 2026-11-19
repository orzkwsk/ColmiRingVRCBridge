namespace ColmiRingVRCBridge.Models;

public sealed record RingDeviceInfo(
    string? Model,
    string? Serial,
    string? HardwareVersion,
    string? FirmwareVersion);
