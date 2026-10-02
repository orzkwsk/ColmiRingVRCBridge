# ColmiRingVRCBridge

Windows WPF bridge for reading realtime heart-rate telemetry from QRing-compatible COLMI smart rings over Bluetooth Low Energy and forwarding it to VRChat OSC.

> **Status:** `v0.1.0-preview.1` is the first public preview. The currently validated hardware target is **COLMI R06**. This software is experimental and is not intended for medical diagnosis, treatment, or safety-critical monitoring.

## What it does

```text
COLMI / QRing-compatible ring
        ↓ BLE
ColmiRingVRCBridge
        ↓ OSC / UDP
VRChat avatar parameters
```

Current features:

- Scan and connect to nearby compatible BLE rings.
- Realtime heart-rate acquisition from COLMI R06.
- Battery percentage and charging state.
- Heart-rate min/max and a rolling 60-second graph.
- Per-ring 24-hour battery history stored locally.
- Automatic reconnect to the last successful Bluetooth address.
- Connection and telemetry diagnostics for BLE/GATT/protocol troubleshooting.
- VRChat OSC output as `Float` or `Int`.
- Optional fixed dummy BPM for avatar/gimmick testing.

## Supported hardware

### Validated

- **COLMI R06**

### Protocol-compatible references, not yet claimed as validated

The protocol material used during development documents R02 / R06 / R10 as sharing the relevant protocol surface. Until each device is tested with this bridge, only R06 is treated as supported hardware.

Other QRing-compatible COLMI models may work but are currently unsupported/experimental.

## Requirements

For the prebuilt release:

- Windows 10/11 x64
- Bluetooth Low Energy adapter
- VRChat OSC enabled when using OSC output

The `win-x64` GitHub Release package is self-contained and does not require a separately installed .NET runtime.

For source builds:

- .NET 8 SDK
- Visual Studio 2022 or `dotnet`

## Installation

1. Download the latest `ColmiRingVRCBridge-<version>-win-x64.zip` from GitHub Releases.
2. Extract it to a writable directory.
3. Start `ColmiRingVRCBridge.exe`.
4. Scan for the ring, select it, and connect.
5. Configure the VRChat OSC parameter and start OSC output.

No installer or background Windows service is installed.

## VRChat OSC behavior

Default endpoint:

- Host: `127.0.0.1`
- Port: `9000`

Parameter names are expanded to:

```text
/avatar/parameters/<name>
```

Supported output modes:

- `Float`, normalized as `BPM / 255.0`
- `Float`, raw BPM
- `Int`, raw BPM

`Int + Normalize255` is rejected as an invalid configuration.

Default output interval is 3 seconds.

### Stale heart-rate behavior in v0.1.x preview

Heart-rate freshness is tracked independently from the BLE connection. When the current HR sample becomes stale, the bridge **stops emitting new BPM values** until a fresh sample is available again.

VRChat may therefore retain the last parameter value it previously received. The current preview does not send an automatic zero value and does not expose a separate validity OSC parameter.

This behavior is part of the `0.1.x` preview contract and may be expanded in a later version.

## Connection behavior

- Last successful ring address is stored locally.
- Automatic reconnect is enabled by default and can be disabled in the UI.
- Reconnect targets the last successful Bluetooth address directly.
- Retry delay is intentionally fixed at approximately one second while automatic reconnect is enabled.
- The actual wall-clock interval can be longer because Windows BLE/GATT connection attempts may themselves take time to fail or complete.

The fixed retry cadence is intentional: connection recovery takes priority over reducing retry activity while automatic reconnect is enabled.

## Local data and privacy

The application does not require a cloud service.

Runtime state is stored under:

```text
%LocalAppData%\ColmiRingVRCBridge
```

Current persisted data includes:

- last successful Bluetooth address / device name and auto-reconnect setting (`connection.json`)
- per-ring battery history (`battery-<address>.csv`)

`Debug` builds can additionally write diagnostic JSONL logs under `debug-logs`. Release builds compile that diagnostic logger out.

OSC is sent over UDP to the host/port configured in the UI; the default is localhost (`127.0.0.1:9000`).

## Known limitations of v0.1.0-preview.1

- R06 is the only hardware target validated so far.
- Automatic recovery from an HR measurement-session stall after remove/re-wear is not yet guaranteed.
- The exact production stale threshold and recovery escalation policy remain under hardware validation.
- BLE/GATT timeout behavior depends partly on Windows/WinRT behavior.
- Stale HR currently stops new OSC BPM transmission rather than clearing the remote parameter.
- Release packaging currently targets Windows x64 only.

Detailed engineering status is tracked in `docs/adversarial-review-action-list.md`.

## Build and test

Build:

```powershell
dotnet build .\ColmiRingVRCBridge.sln -c Release
```

Tests:

```powershell
dotnet test .\ColmiRingVRCBridge.sln -c Release
```

The application project itself has no external runtime NuGet dependency. The test project uses xUnit and Microsoft.NET.Test.Sdk.

CI performs Release build and unit tests on `windows-latest`.

## Internal architecture

The current scope is deliberately a Windows / R06 / VRChat bridge, not a generic COLMI SDK.

```text
Windows BLE scan / device open / GATT discovery
    ColmiRingBleService
        ↓
Windows GATT packet transport
    GattColmiTransport : IColmiTransport
        ↓
R06 protocol + measurement session
    R06Protocol
    R06Session
        ↓
Heart-rate snapshot / telemetry diagnostics
        ↓
WPF UI + VRChat OSC
```

`IColmiTransport` provides a hardware-independent test seam for packet/session logic and future recovery work.

## Protocol notes

Known QRing/COLMI UART GATT service:

- Service: `6E40FFF0-B5A3-F393-E0A9-E50E24DCCA9E`
- RX/write: `6E400002-B5A3-F393-E0A9-E50E24DCCA9E`
- TX/notify: `6E400003-B5A3-F393-E0A9-E50E24DCCA9E`
- Packet size: 16 bytes
- Battery command: `0x03`
- Realtime HR poll command: `0x1E`
- Realtime measurement start: `0x69`
- Realtime measurement stop: `0x6A`

Reference implementations / protocol research:

- https://github.com/tahnok/colmi_r02_client
- https://github.com/robinojw/openring

This project reimplements the protocol surface it uses in C# and does not embed either project.

## Branch policy

- `main`: published/release baseline
- `dev`: integration
- `feature/*`: active development
- `release/*`: release preparation

Development changes should normally flow `feature/* -> dev -> main`. Release-preparation branches may be used to freeze documentation, packaging, versioning, and release automation before promotion.

## License

MIT License. See `LICENSE`.
