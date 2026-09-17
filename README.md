# ColmiRingVRCBridge

Windows WPF bridge that reads realtime heart-rate telemetry from QRing-compatible COLMI smart rings over BLE and sends it directly to VRChat OSC.

## Target

Initial hardware target: **COLMI R06**.

The protocol reference used for the initial implementation documents **R02 / R06 / R10** as fully compatible. Other QRing-compatible models may work, but are not claimed as supported until tested.

## Initial PoC features

The UI is intentionally arranged in operation order:

1. Scan nearby BLE rings, select one, connect.
2. Check ring telemetry and device identification.
3. Configure VRChat OSC parameter, type, scaling, target and output interval.
4. Optionally replace the measured BPM with fixed dummy data for avatar/gimmick testing.
5. Start/stop OSC output.

Implemented telemetry:

- Realtime BPM
- Battery percentage
- Charging state
- Bluetooth address
- Optional GATT model / serial / hardware revision / firmware revision

Implemented OSC output:

- Default endpoint: `127.0.0.1:9000`
- Parameter name is expanded to `/avatar/parameters/<name>`
- Float or Int OSC value
- Default scaling: `BPM / 255.0` => `0.0 .. 1.0`
- Raw BPM mode
- Default output interval: 3 seconds
- Fixed dummy BPM mode

## Build

Requirements:

- Windows 10/11
- .NET 8 SDK
- Visual Studio 2022 or `dotnet build`
- Bluetooth Low Energy adapter

```powershell
dotnet build .\ColmiRingVRCBridge.sln
```

No external NuGet package is required by the initial PoC. BLE uses the Windows Bluetooth APIs and OSC is encoded directly over UDP.

## Protocol notes

Known QRing/COLMI UART GATT service:

- Service: `6E40FFF0-B5A3-F393-E0A9-E50E24DCCA9E`
- RX/write: `6E400002-B5A3-F393-E0A9-E50E24DCCA9E`
- TX/notify: `6E400003-B5A3-F393-E0A9-E50E24DCCA9E`
- Packet size: 16 bytes
- Battery command: `0x03`
- Realtime measurement start: `0x69`
- Realtime measurement stop: `0x6A`

Reference implementations / protocol research:

- https://github.com/tahnok/colmi_r02_client
- https://github.com/robinojw/openring

The bridge reimplements the required protocol surface in C# rather than embedding either project.

## Branch policy

- `main`: stable
- `dev`: integration
- `feature/*`: active development

Current initial implementation branch: `feature/initial-wpf-poc`.

## PoC limitations

- COLMI R06 hardware has not yet been exercised against this C# implementation in this repository.
- Automatic reconnect and tray-only operation are not implemented yet.
- Device scan intentionally filters likely COLMI/QRing devices to keep the selector usable.
- Int + normalized scaling is allowed by the backend but is generally not useful; selecting Int in the GUI switches scaling to Raw BPM by default.
