# ColmiRingVRCBridge telemetry-liveness hardening action list

Branch: `feature/telemetry-liveness-hardening`

Original review baseline: `feature/initial-wpf-poc` at `80758c21cab57a044c71e886d61221abad88c706`.

Second implementation review baseline: `feature/telemetry-liveness-hardening` at `73b66569141377d27cdbc7ba714e2ba7c9062a55`.

This branch is intentionally used as a technical-debt / reliability hardening branch. Product-facing feature additions should remain secondary until the promotion gate at the end of this document is satisfied.

## Implemented on this branch

### Heart-rate validity is independent from BLE connection

Implemented:

- Immutable `HeartRateSnapshot` containing BPM + timestamp.
- Atomic snapshot replacement between BLE callback, OSC task and UI.
- Explicit `NoSample / Fresh / Stale` freshness states.
- Provisional freshness threshold isolated as `HeartRateFreshnessThreshold` (currently 5 seconds).
- `GetEffectiveBpm()` returns only a fresh sample.
- BLE link remaining connected no longer makes an old BPM implicitly valid forever.

Important external-policy note:

When HR becomes stale, the bridge currently stops producing new BPM values to the OSC output loop. VRChat may still retain the last parameter value already received. Whether the bridge should additionally send `0`, a validity parameter, or another stale marker remains a product/API decision.

### One telemetry UI handler path

Implemented:

- Removed runtime unsubscribe/resubscribe handler replacement.
- Constructor subscribes the final telemetry handlers directly.
- BLE callbacks use non-blocking `Dispatcher.BeginInvoke()` only for UI mutation.
- HR graph redraw remains timer-driven rather than notification-driven.
- The obsolete tooltip runtime workaround was removed; the tooltip is created with a valid WPF configuration directly.

### Reconnect control no longer depends on button state

Implemented:

- Explicit `ConnectionOperation` state:
  - `None`
  - `ManualConnect`
  - `ManualDisconnect`
  - `AutoReconnect`
- Reconnect decisions no longer inspect `ConnectButton.IsEnabled`.
- Button enabled/disabled state is now an output of internal connection-operation state.

### Auto-reconnect enable/disable race fixed

Implemented:

- Auto-reconnect enable/disable transitions are serialized.
- Stopping reconnect cancels and awaits the previous loop.
- Re-enabling reconnect starts a new loop only after the old loop has actually ended.
- Rapid `ON -> OFF -> ON -> OFF -> ON` cannot leave the UI enabled while no reconnect loop can be created because of a stale task reference.
- Connection-attempt cancellation is propagated into connect/session initialization where possible without making the established HR session depend on the reconnect token lifetime.

### Reconnect retry cadence is intentionally fixed at 1 second

This is now an operational requirement rather than a temporary PoC value.

The bridge is expected to maintain a live connection to its upstream ring whenever possible and continue supplying telemetry to the downstream consumer. Therefore, after a failed reconnect attempt, the next retry uses a fixed approximately one-second delay.

Do not replace this with exponential backoff as a general optimization. Any future retry-policy change must preserve the product invariant:

> connection recovery takes priority over reducing retry activity while Auto reconnect is enabled.

The actual wall-clock interval between attempts may exceed one second because a Windows BLE/GATT connection attempt itself can take time before failing. `ReconnectInterval` defines the delay before the next retry, not a one-second hard timeout for the connect operation.

### Link state and HR telemetry state are separate

Implemented internal states:

- `Disconnected`
- `Initializing`
- `Streaming`
- `Stale`

`BluetoothLEDevice.ConnectionStatus` remains the link-level truth. HR session started/freshness is tracked separately. The normal UI remains compact; detailed state is exposed in the connection tooltip and stale state is visible in the connection text.

### Telemetry diagnostics split into three observation levels

Implemented timestamps:

- `LastRawNotificationAt`
- `LastValidPacketAt`
- `LastValidHeartRateAt`

Implemented counters:

- raw notifications
- invalid packets
- valid packets
- valid HR packets
- HR poll TX
- HR poll write failures
- consecutive HR poll failures

This distinguishes:

1. GATT notifications stopped.
2. GATT notifications continue but packets are malformed/invalid.
3. Valid protocol traffic continues while HR packets stop.

### Battery history disk I/O removed from BLE callback path

Implemented:

- `BatteryHistoryStore.Add()` updates memory and snapshots the history only.
- Persistence is queued to one background writer through `BatteryHistoryPersistenceQueue`.
- File failures remain isolated from live telemetry.
- Battery sampling/persistence behavior is otherwise unchanged.

### Invalid OSC model state rejected

Implemented:

- `OscOutputOptions` rejects `Int + Normalize255` at construction.
- `OscOutputService` no longer silently changes an invalid configuration into another meaning.
- Integer OSC output is explicitly raw BPM only.

### Minimal transport/session seam added

Implemented without turning the project into a generic COLMI framework:

- `IColmiTransport`
- `GattColmiTransport` for Windows GATT packet transport
- `R06Protocol` for R06 packet construction/parsing
- `R06Session` for HR session start/stop, HR polling, battery polling, parsing, liveness diagnostics and polling lifecycle
- `ColmiRingBleService` remains responsible for scan, Windows device open, GATT discovery, device information and high-level connection lifecycle

This provides the test seam required for later watchdog/recovery work while keeping the product responsibility limited to:

`COLMI Ring -> BLE -> Windows WPF Bridge -> OSC -> VRChat`

### Hardware-independent tests added

Added an xUnit test project covering:

- COLMI 16-byte packet construction/checksum validation
- invalid checksum / wrong-length rejection
- R06 battery packet parsing
- realtime HR poll packet parsing
- realtime session packet parsing / error code parsing
- HR snapshot Fresh/Stale semantics
- invalid `Int + Normalize255` OSC configuration
- transient HR poll write failure without polling-task death
- successful poll after a transient failure resetting the consecutive failure count
- raw / invalid / valid / HR diagnostic separation
- `Initializing -> Streaming -> Stale` telemetry-state transition

Tests intentionally use a fake `IColmiTransport`; no physical ring is required for these cases.

## Hardware validation required before implementation

### HR watchdog and measurement-session re-arm

Do not hard-code a recovery packet sequence yet.

Observed behavior to validate on R06:

1. Wear ring and obtain stable HR.
2. Remove ring while BLE remains connected.
3. Record whether raw notifications continue.
4. Record whether valid packets continue.
5. Record whether only valid HR stops.
6. Re-wear without sending recovery traffic and measure natural recovery latency.
7. If it does not recover, test the smallest required sequence:
   - HR `STOP`
   - HR `START`
   - HR `CONTINUE`
8. Only if session re-arm is insufficient, test:
   - CCCD reconfiguration
   - GATT rediscovery
   - BLE device reopen

The recovery implementation should escalate only as far as required by measured R06 behavior.

### BLE/GATT operation timeout policy

Cancellation now propagates through project-owned async boundaries where possible, but WinRT operations are not wrapped in arbitrary `Task.WhenAny()` timeouts.

Before adding hard timeouts, verify target-Windows behavior for:

- device open
- service discovery
- characteristic discovery
- CCCD write
- packet write

Avoid a timeout implementation that merely abandons a still-running WinRT operation.

## Product/API status

### OSC behavior when HR becomes stale

The `0.1.x` preview contract is selected in the release README: stop emitting new
BPM values while HR is stale and resume when a fresh sample is available. VRChat
may retain the last received value. The preview does not send an automatic zero
or a separate validity parameter. This review does not change that contract.

Zero/validity output is a future product/API extension, not an unresolved gate
for integrating the post-review fixes.

### User-facing telemetry-state detail

Internal state separation is complete. The current normal UI remains compact and exposes detailed state through diagnostics.

Only add more visible state-machine UI if actual use shows that `Connected / HR init / HR stale` is insufficient.

## Hardware validation checklist

### Test A: normal acquisition

Confirm:

- link connected
- session started
- raw notification timestamp advances
- valid packet timestamp advances
- valid HR timestamp advances
- HR poll TX increases
- poll failure counters remain stable

### Test B: remove ring

Confirm separately:

- BLE link state
- raw notification progression
- valid packet progression
- HR packet progression
- poll write success/failure

### Test C: re-wear

Measure natural HR reacquisition time with no recovery command.

If it does not recover, identify the minimum session-level command sequence that restores HR.

### Test D: recovery escalation

Determine the minimum successful recovery level:

1. HR session re-arm
2. CCCD reconfiguration
3. GATT rediscovery
4. BLE device reopen

## Promotion gate for `dev`

Updated 2026-10-09 after comparing the post-review feature with `origin/dev` at
`49fe23be84c8700d21d626a0bd9044cdf1722814`. The original refactor is already an
ancestor of dev. Gate the additional fixes on newly introduced or worsened
Blocker/Major findings, and track unchanged baseline defects separately.

### Software automated gate

- [x] HR freshness/stale state exists independently from BLE connection.
- [x] Stale BPM is not unconditionally treated as live OSC input.
- [x] Telemetry handler duplication/runtime swapping is removed.
- [x] Reconnect logic no longer uses UI control state as coordinator state.
- [x] Reconnect cancellation/re-enable race is fixed structurally.
- [x] Reconnect retry delay is intentionally fixed at approximately 1 second by operational requirement.
- [x] Raw notification / valid packet / valid HR are distinguishable.
- [x] Battery persistence disk I/O is outside the BLE callback path.
- [x] Invalid Int + Normalize255 configuration is rejected by the model.
- [x] Protocol/session logic has a transport test seam.
- [x] Hardware-independent packet/polling/freshness tests exist.
- [x] Release and Debug builds succeed locally with .NET 8.0.424.
- [x] Release tests pass: 29/29; Debug tests pass: 30/30.
- [x] Additional failure-path, record API and serialization regression tests pass.
- [x] The dev baseline and feature reproduce the same existing OSC stop crash.
- [x] Manual-connect shutdown lifetime has no newly introduced race in code-path comparison.
- [x] Branch Blocker = 0 and Branch Major = 0 after preserving both named-argument APIs.
- [x] OSC stale-source preview behavior is explicitly selected in the release README.
- [x] The target dev README describes the current guarantees and pending hardware limits.

The existing xUnit2020 warning occurs on both baseline and feature; it is recorded
without suppression. Baseline OSC and manual-connect shutdown issues are tracked
in `baseline-issues.md` and do not independently block these fixes.

### GUI gate

- [ ] GUI smoke: scan/connect, tooltip/graphs, rapid reconnect toggles and close.

Not performed in this review. GUI status must remain explicit in the merge report.

### HIL gate

- [ ] R06 remove/re-wear behavior measured with the new diagnostics.
- [ ] Minimum soft HR recovery sequence validated on R06.
- [ ] Windows BLE/GATT timeout and close-during-connect behavior validated.

HIL is pending. The soft recovery sequence remains explicitly unresolved; no
recovery traffic or guessed protocol change is introduced by these fixes.
GUI/HIL pending alone does not block a software-only integration under the current
review instructions. See `post-refactor-review.md` for the comparison and decision.
