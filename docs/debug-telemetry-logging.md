# Debug telemetry logging

`Debug` builds write diagnostic telemetry as JSON Lines (`.jsonl`). `Release` builds do not compile the logger or its MainWindow integration because both are guarded by `#if DEBUG`.

## Output

Directory:

```text
%LocalAppData%\ColmiRingVRCBridge\debug-logs
```

File per application session:

```text
telemetry-YYYYMMDD-HHmmss-fff.jsonl
```

Logs older than 7 days are pruned when a new debug logger starts.

## Periodic snapshot

A `telemetry_snapshot` record is written immediately when logging starts and then every 60 seconds.

Each snapshot records:

- BLE link connected state
- current manual/automatic connection operation
- Auto reconnect enabled state
- HR telemetry state
- latest HR BPM, timestamp, freshness and age
- latest battery percentage / charging state, timestamp, freshness and age
- last raw notification timestamp
- last valid protocol-packet timestamp
- last valid HR timestamp
- last battery poll timestamp
- last battery packet timestamp
- raw / invalid / valid packet counters
- HR / battery packet counters
- HR poll TX / write failure / consecutive failure counters
- battery poll TX / write failure / consecutive failure counters
- HR session-started state

## Event records

The debug logger also records low-frequency events that are useful when diagnosing an unattended run:

- `connection_changed`
- `battery_updated`
- `protocol_warning`

Heart-rate updates are intentionally not logged individually because they can arrive approximately once per second and would create unnecessary log volume. HR liveness is represented by the periodic snapshot counters/timestamps instead.

## Intended use

The logger is diagnostic-only and must not affect BLE or telemetry processing. Logging uses a single background writer and an in-memory channel; BLE callbacks only enqueue low-frequency event records. The JSONL file is opened with read sharing so it can be inspected while the application is running.
