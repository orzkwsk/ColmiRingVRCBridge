# Changelog

All notable public changes to ColmiRingVRCBridge are documented here.

The project follows Semantic Versioning while the public API is still experimental. Pre-release identifiers are used for builds that intentionally expose known hardware-validation limitations.

## [0.1.0-preview.2] - 2026-10-10

### Fixed

- OSC crash when output is stopped immediately after starting (Issue #7).
- Race between a manual BLE connection and application shutdown (Issue #1).
- Reconnect operations that could leave the UI permanently busy.
- Unbounded waits in BLE/GATT operations.
- Late native BLE completions corrupting the current connection/session state.
- Persistence and diagnostic-log shutdown ordering.

### Improved

- Manual Scan/Connect priority over AutoReconnect and suppression after Manual Disconnect.
- Cancellation, task ownership and resource cleanup during connection/output shutdown.
- Connection-stage diagnostics.
- OSC regression, BLE lifetime, priority, shutdown and serialization/API compatibility tests.

### Known limitations

- Only COLMI R06 is validated; release packages target Windows x64.
- BLE/GATT behavior still depends partly on Windows/WinRT and the Bluetooth driver.
- Controlled distance-induced link-loss recovery is not tested (non-blocking hardware-validation note).
- Experimental R06 reboot and post-reboot HR recovery continue in [Issue #8](https://github.com/orzkwsk/ColmiRingVRCBridge/issues/8); reboot UI and commands are excluded from this release.
- Automatic HR measurement-session recovery after remove/re-wear is not yet guaranteed.
- Stale HR stops new OSC BPM transmissions; VRChat may retain the last received value.

## [0.1.0-preview.1] - 2026-10-02

### Added

- First public preview targeting COLMI R06.
- BLE scan, connect and persisted auto-reconnect.
- Realtime heart-rate telemetry with explicit fresh/stale state.
- Battery percentage, charging state and local 24-hour battery history.
- Heart-rate min/max and rolling graph.
- VRChat OSC output as normalized/raw Float or raw Int.
- Fixed dummy BPM mode for avatar/gimmick testing.
- BLE/GATT/protocol telemetry diagnostics.
- Hardware-independent protocol/session tests using `IColmiTransport`.
- Windows CI and GitHub Release packaging workflow.

### Reliability hardening

- Poll-write failures no longer permanently terminate the HR polling task.
- HR snapshot value/timestamp publication is atomic.
- BLE link state and HR freshness are separated.
- BLE callbacks avoid blocking UI dispatch.
- Battery-history disk I/O is moved off the BLE callback path.
- Reconnect coordination no longer depends on WPF control state.
- Auto-reconnect cancellation/re-enable races are serialized.

### Known limitations

- Only COLMI R06 is currently validated.
- Automatic recovery from an HR measurement-session stall after remove/re-wear is not yet guaranteed.
- Stale HR stops new OSC BPM transmissions; VRChat may retain the last received parameter value.
- Release packages currently target Windows x64 only.
