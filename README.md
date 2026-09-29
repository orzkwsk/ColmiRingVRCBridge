# ColmiRingVRCBridge

Windows WPF bridge that reads realtime heart-rate telemetry from QRing-compatible COLMI smart rings over BLE and sends it directly to VRChat OSC.

## Target

Initial hardware target: **COLMI R06**.

The protocol reference used for the initial implementation documents **R02 / R06 / R10** as fully compatible. Other QRing-compatible models may work, but are not claimed as supported until tested.

The R06 path has been exercised on real hardware. Current development is focused on telemetry validity, reconnect reliability and recovery behavior around measurement-session stalls.

## Current PoC features

The UI is intentionally arranged in operation order:

1. Scan nearby BLE rings, select one, connect.
2. Check ring telemetry and device identification.
3. Configure VRChat OSC parameter, type, scaling, target and output interval.
4. Optionally replace measured BPM with fixed dummy data for avatar/gimmick testing.
5. Start/stop OSC output.

Implemented telemetry and diagnostics:

- Realtime BPM
- Heart-rate minimum / maximum since reset
- Last 60 seconds heart-rate graph
- Heart-rate min/max/history reset
- Explicit HR `NoSample / Fresh / Stale` validity state
- Battery percentage
- Charging state
- Persistent per-ring 24 hour battery history shown from the battery-value tooltip
- Charging intervals highlighted in battery history
- Bluetooth address
- Optional GATT model / serial / hardware revision / firmware revision
- Connection-status diagnostics:
  - link/telemetry state
  - last raw GATT notification age
  - last valid protocol packet age
  - last valid HR age
  - raw / valid / invalid packet counters
  - valid HR packet count
  - HR poll TX count
  - HR poll write failure count
  - consecutive HR poll failures

Connection behavior:

- Manual scan and connect
- Last successful ring address is persisted locally
- Automatic reconnect can be enabled/disabled from the connection row
- Automatic reconnect defaults to enabled
- Reconnect targets the last successful Bluetooth address directly rather than requiring a fresh scan
- Manual connect/disconnect and auto reconnect use explicit internal operation state rather than UI-control state
- Reconnect enable/disable transitions cancel and await the old reconnect loop before re-enabling

Implemented OSC output:

- Default endpoint: `127.0.0.1:9000`
- Parameter name is expanded to `/avatar/parameters/<name>`
- Float or Int OSC value
- Float default scaling: `BPM / 255.0` => `0.0 .. 1.0`
- Raw BPM mode
- Integer output is raw BPM only
- Invalid `Int + Normalize255` configuration is rejected
- Default output interval: 3 seconds
- Fixed dummy BPM mode
- Only a fresh HR snapshot is exposed to the OSC output loop

When HR becomes stale, the current implementation stops producing new BPM values. VRChat may still retain the last parameter value already received. Whether to additionally send zero and/or a validity parameter is intentionally left as a product/API decision.

## Internal architecture

The PoC remains a Windows / R06 / VRChat bridge rather than a generic COLMI SDK.

Current layering:

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

`IColmiTransport` exists primarily as a hardware-independent test seam and as the boundary needed for later HR-session recovery work.

## Build and test

Requirements:

- Windows 10/11
- .NET 8 SDK
- Visual Studio 2022 or `dotnet`
- Bluetooth Low Energy adapter for the application

Build:

```powershell
dotnet build .\ColmiRingVRCBridge.sln -c Release
```

Tests:

```powershell
dotnet test .\ColmiRingVRCBridge.sln -c Release
```

The application itself has no external runtime NuGet dependency. The test project uses xUnit / Microsoft.NET.Test.Sdk.

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

The currently validated R06 PoC uses the dedicated realtime HR type selected from hardware testing plus periodic realtime-HR polling. It remains an R06-specific working dialect rather than a generalized multi-device profile system.

Reference implementations / protocol research:

- https://github.com/tahnok/colmi_r02_client
- https://github.com/robinojw/openring

The bridge reimplements the required protocol surface in C# rather than embedding either project.

## Reliability hardening status

Implemented:

- Transient HR poll-write failures no longer terminate the polling task permanently.
- HR value + timestamp are stored as one atomic immutable snapshot.
- HR freshness is independent from Bluetooth connection state.
- BLE callback / UI-thread coupling uses non-blocking dispatch only.
- Battery history persistence is queued off the BLE notification callback path.
- Raw notification / valid packet / valid HR are separate diagnostics stages.
- Internal telemetry states distinguish `Disconnected / Initializing / Streaming / Stale`.
- Reconnect coordinator no longer uses `ConnectButton.IsEnabled` as internal state.
- Reconnect OFF -> ON lifecycle waits for the previous loop to stop before starting a new loop.
- R06 protocol/session logic is testable through a fake transport.

Still pending R06 hardware validation:

- Automatic HR measurement-session re-arm after stale telemetry.
- Exact production stale threshold; current threshold is a provisional isolated value.
- Escalation from HR re-arm to CCCD/GATT reinitialization to BLE device reopen.
- Reconnect exponential backoff policy.
- BLE/GATT hard-timeout behavior on target Windows versions.

Still pending product/API decision:

- VRChat OSC stale-source contract (`stop sending`, `send 0`, validity parameter, or combination).

See `docs/adversarial-review-action-list.md` for the promotion gate and R06 validation procedure.

## Branch policy

- `main`: stable
- `dev`: integration
- `feature/*`: active development

Current PoC baseline: `feature/initial-wpf-poc`.

Current technical-debt / reliability branch: `feature/telemetry-liveness-hardening`.

Do not promote this branch to `dev` until the documented promotion gate is satisfied.
