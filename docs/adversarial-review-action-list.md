# ColmiRingVRCBridge adversarial review action list

Target baseline: `feature/initial-wpf-poc` at `80758c21cab57a044c71e886d61221abad88c706`.

This document separates findings into changes that can be implemented immediately without changing the empirical R06 protocol assumptions, changes that require hardware validation, and changes that require an explicit product/API decision.

## Implement now

### 1. Make the HR polling loop resilient to transient failures

Current behavior allows a single `RequestRealtimeHeartRateAsync()` failure to terminate the polling task permanently while the BLE link may remain connected.

Implement:

- Catch transient failures per polling iteration instead of around the whole loop.
- Continue polling after an isolated failure.
- Track consecutive poll failures for diagnostics.
- Reset the failure count after a successful poll write.
- Keep cancellation behavior deterministic.

This does not decide how or when to restart the R06 measurement session; it only prevents a single transient write failure from permanently killing the acquisition task.

### 2. Track telemetry liveness independently from the BLE link

Implement timestamps/counters for valid HR reception independently from `BluetoothLEDevice.ConnectionStatus`.

Track at minimum:

- Last valid HR timestamp.
- Last valid BLE notification timestamp.
- HR poll TX count.
- HR poll write failure count / consecutive failure count.

These values provide the evidence needed to distinguish:

- BLE link alive / HR session dead.
- GATT notification path dead.
- Poll write failing.
- Whole BLE connection gone.

Do not automatically re-arm or reconnect from these timestamps yet; thresholds and recovery ordering require hardware validation.

### 3. Remove synchronous WPF dispatch from BLE callbacks

BLE callbacks should not block on the UI thread.

Implement:

- Replace synchronous `Dispatcher.Invoke()` with asynchronous dispatch where UI mutation is still required.
- Stop redrawing the HR graph directly from every HR callback; the existing dispatcher timer owns periodic graph redraw.

This reduces coupling without changing protocol behavior.

### 4. Prevent the meaningless Int + Normalize255 configuration

`round(BPM / 255)` produces effectively only 0 or 1.

Implement:

- Keep `Int` mapped to raw BPM only.
- Reject or automatically correct `Int + Normalize255` at option construction.

### 5. Update README to match the current PoC

Update stale statements:

- R06 has been exercised on real hardware.
- Automatic reconnect exists.
- Heart-rate and battery telemetry UI has expanded beyond the initial feature list.
- Document that liveness/recovery hardening is still under active development.

## Hardware validation required before implementation

### 6. HR watchdog and measurement-session re-arm

Observed hardware behavior includes cases where the BLE link remains connected but HR stops updating, including after removing and re-wearing the ring.

Candidate recovery sequence:

1. Detect stale HR.
2. Re-arm only the HR measurement session (`STOP -> START/CONTINUE` or another R06-specific sequence).
3. If not recovered, reinitialize GATT notification/session state.
4. If still not recovered, reopen the BLE device.

Do not hard-code the stale threshold or exact soft-recovery packet sequence until raw TX/RX behavior is verified on R06.

Validation needed:

- Remove ring, wait for HR to stop, re-wear it without disconnecting.
- Record whether poll responses continue while valid HR stops.
- Verify the minimum packet sequence that restores HR.
- Measure normal first-lock latency after re-wear so the watchdog does not thrash during normal acquisition.

### 7. Reconnect retry policy / backoff

Current requirement uses approximately one-second reconnect attempts. A fixed one-second loop may be overly aggressive when the Windows BLE stack is stale or the ring is powered off.

Before changing it, measure:

- Normal reconnect latency after a transient range loss.
- Behavior while the ring is not advertising.
- Behavior after app restart while Windows still retains BLE state.

Candidate policy after validation:

`1 s -> 1 s -> 1 s -> 2 s -> 4 s -> 8 s -> 10-15 s cap`.

Keep HR soft recovery separate from BLE-device reconnect backoff.

### 8. BLE/GATT operation timeouts and cancellation

Windows BLE operations can stall, but timeout handling must avoid leaving uncontrolled WinRT operations running behind an apparent timeout.

Before implementing generic wrappers, verify which WinRT operations used by this project provide cancellable async operations and how cancellation behaves on the target Windows versions.

Operations to cover eventually:

- Device open.
- Service discovery.
- Characteristic discovery.
- CCCD writes.
- Packet writes.

## Product/API decision required before implementation

### 9. OSC stale-source behavior

Current behavior stops sending when no valid BPM is available, which can leave the last VRChat parameter value latched.

Choose one policy before implementation:

- Send `0` once when the source becomes stale.
- Send a separate `HeartRateValid` boolean parameter.
- Send both.
- Preserve the last value intentionally.

This is a public bridge behavior decision, not only an implementation detail.

### 10. Public connection/telemetry state model

Internally, link state and telemetry readiness should be distinct. Candidate states are:

`Disconnected -> Connecting -> LinkConnected -> Initializing -> Streaming -> Stale -> Recovering`.

Before exposing all states to the normal UI, decide whether the user-facing UI should show the full state machine or only a compact status while detailed states remain diagnostics-only.

### 11. Protocol profile abstraction

The current R06 PoC intentionally uses the empirically selected realtime dialect. A profile abstraction becomes useful before adding more ring models or alternate firmware dialects.

Candidate shape:

- Start measurement packet(s).
- Stop packet.
- Poll packet.
- Notification parser.
- Session re-arm strategy.

Do not generalize prematurely while only one validated R06 dialect is in active use.

## Testing work after transport seam exists

The project currently has no automated protocol/recovery tests. Add a BLE transport abstraction before attempting meaningful recovery-state tests.

Priority test vectors:

- `ColmiPacket.Build` and checksum validation.
- Valid/invalid `0x03`, `0x69`, and `0x1E` packets.
- Malformed checksum handling.
- Transient poll-write failure without polling-task death.
- Stale HR detection.
- Recovery state transitions once the hardware-validated recovery sequence is fixed.

## Promotion gate for `dev`

Before promoting this work toward `dev`, the minimum expected conditions are:

- Build succeeds locally in Release configuration.
- Transient HR poll failure does not permanently stop the polling loop.
- Telemetry liveness can be observed independently from BLE connection state.
- Removing/re-wearing the R06 has a documented measured behavior and a validated recovery plan.
- README reflects actual implemented behavior.
- Any OSC stale-source policy is explicitly decided before it becomes part of the bridge contract.
