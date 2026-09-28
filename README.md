# ColmiRingVRCBridge

Windows WPF bridge that reads realtime heart-rate telemetry from QRing-compatible COLMI smart rings over BLE and sends it directly to VRChat OSC.

## Target

Initial hardware target: **COLMI R06**.

The protocol reference used for the initial implementation documents **R02 / R06 / R10** as fully compatible. Other QRing-compatible models may work, but are not claimed as supported until tested.

The R06 path has been exercised on real hardware. Current development is focused on improving telemetry liveness and recovery behavior around transient GATT failures and measurement-session stalls.

## Current PoC features

The UI is intentionally arranged in operation order:

1. Scan nearby BLE rings, select one, connect.
2. Check ring telemetry and device identification.
3. Configure VRChat OSC parameter, type, scaling, target and output interval.
4. Optionally replace the measured BPM with fixed dummy data for avatar/gimmick testing.
5. Start/stop OSC output.

Implemented telemetry and diagnostics:

- Realtime BPM
- Heart-rate minimum / maximum since reset
- Last 60 seconds heart-rate graph
- Heart-rate min/max/history reset
- Battery percentage
- Charging state
- Persistent per-ring 24 hour battery history shown from the battery-value tooltip
- Charging intervals highlighted in battery history
- Bluetooth address
- Optional GATT model / serial / hardware revision / firmware revision
- BLE/HR liveness diagnostics available from the connection-status tooltip
  - last valid BLE notification age
  - last valid HR age
  - HR poll TX count
  - HR poll write failure count
  - consecutive HR poll failures

Connection behavior:

- Manual scan and connect
- Last successful ring address is persisted locally
- Automatic reconnect can be enabled/disabled from the connection row
- Automatic reconnect defaults to enabled
- The current reconnect loop targets the last successful Bluetooth address directly rather than requiring a fresh scan

Implemented OSC output:

- Default endpoint: `127.0.0.1:9000`
- Parameter name is expanded to `/avatar/parameters/<name>`
- Float or Int OSC value
- Float default scaling: `BPM / 255.0` => `0.0 .. 1.0`
- Raw BPM mode
- Integer output is always raw BPM; normalized integer output is intentionally not supported
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

The currently validated R06 PoC uses the dedicated realtime HR type selected from hardware testing plus periodic realtime-HR polling. This is intentionally still treated as an R06-specific working dialect rather than a generalized protocol abstraction.

Reference implementations / protocol research:

- https://github.com/tahnok/colmi_r02_client
- https://github.com/robinojw/openring

The bridge reimplements the required protocol surface in C# rather than embedding either project.

## Branch policy

- `main`: stable
- `dev`: integration
- `feature/*`: active development

Current PoC baseline: `feature/initial-wpf-poc`.

Current liveness/recovery hardening work: `feature/telemetry-liveness-hardening`.

## Current hardening status

Implemented without changing the empirically selected R06 measurement sequence:

- A single transient HR poll-write failure no longer terminates the HR polling task permanently.
- HR polling failures are counted and exposed for diagnostics.
- Valid BLE-notification time and valid-HR time are tracked independently from Bluetooth connection state.
- BLE event callbacks no longer synchronously block on WPF UI dispatch after the window is rendered.
- HR graph redraw remains timer-driven rather than notification-driven.

Still intentionally pending hardware validation or product-policy decisions:

- Automatic HR measurement-session re-arm after telemetry becomes stale.
- Exact stale-HR threshold.
- Escalation from HR re-arm to GATT reinitialization to BLE device reopen.
- BLE/GATT operation timeout strategy.
- Reconnect backoff policy beyond the current ~1 second retry behavior.
- VRChat OSC behavior when the HR source becomes stale (`0`, validity parameter, or preserve-last-value policy).
- Generalized protocol-profile abstraction for additional ring models / firmware dialects.

See `docs/adversarial-review-action-list.md` for the current review-derived action list and promotion gate.

## PoC limitations

- R06 has been tested, but long-duration and recovery-path behavior is still under active validation.
- Tray-only operation is not implemented.
- Device scan intentionally filters likely COLMI/QRing devices to keep the selector usable.
- Automatic reconnect exists, but retry/backoff policy is still PoC-level.
- Heart-rate liveness is now observable, but automatic stale-session recovery is not enabled until the R06 recovery sequence is validated on hardware.
