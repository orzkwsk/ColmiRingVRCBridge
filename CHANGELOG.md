# Changelog

All notable public changes to ColmiRingVRCBridge are documented here.

The project follows Semantic Versioning while the public API is still experimental. Pre-release identifiers are used for builds that intentionally expose known hardware-validation limitations.

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
