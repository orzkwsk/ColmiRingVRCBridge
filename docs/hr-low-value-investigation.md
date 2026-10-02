# R06 low heart-rate value investigation

Status: active hardware validation

## Observed symptom

Long-running Debug telemetry has produced occasional computed HR candidates in the `2..4 BPM` range while the BLE link, packet checksum validation, HR polling, and battery polling remained active.

These values are not being treated here as physiological evidence. They are protocol candidates that require raw-packet classification.

## Current bridge dialect

The bridge currently uses:

- realtime session start type: `0x06`
- realtime session start: `0x69`
- computed-HR poll: `0x1E, 0x33` (`0x33` = ASCII `3`)
- computed-HR parser: `0x1E` response, BPM candidate in byte `[1]`
- realtime-session parser: `0x69`, error in byte `[2]`, BPM candidate in byte `[3]`
- any BPM candidate greater than zero is currently forwarded unchanged

## External implementation comparison

### tahnok/colmi_r02_client

Reference commit inspected: `19e70aa502749b87d57e3dba0156dfd67ddc0d4a`.

`real_time.py` uses:

- ordinary heart-rate realtime type `0x01`
- cmd `0x69` / `0x6A` for realtime start/stop
- cmd `0x1E`
- legacy continue/poll payload `bytearray(b"3")`, i.e. ASCII `3` = `0x33`

Its generic realtime enum comments type `0x06` out as redundant.

### robinojw/openring

Reference commit inspected: `c406e2bb6e5a9c429c73b7593d8baaa92579f0b1`.

Current `realTimeHr.ts` documents the computed-HR flow as:

- poll packet `[0x1E, 0x03]`
- BPM candidate in byte `[1]`
- `0` means not ready yet / keep polling

Its generic realtime implementation uses heart-rate type `0x01` for cmd `0x69`.

### lukr-99/ring-set

Reference commit inspected: `0c9b1a9976281659477b8f5abaa233337164bf5b`.

Its R0x live-HR implementation documents:

- start type `0x01`
- poll `[0x1E, 0x03]`
- poll responses accepted as `0x1E` and `0x9E`
- `0x9E` described as `0x1E | 0x80`
- `0xEE` described as a warming-up value
- accepted live-HR range `30..220`

It also periodically re-arms the live measurement session.

## Current hypotheses

### H1 — transient non-BPM/status values are being accepted as BPM

Highest current priority.

The bridge accepts every positive byte `[1]` value from parsed cmd `0x1E` packets. Other current R0x code explicitly applies a `30..220` live-HR validity range. The observed `2..4` values may therefore be sensor warm-up/contact-loss/status-like values rather than computed BPM.

### H2 — poll dialect mismatch (`0x33` vs `0x03`)

Plausible and requires R06 capture.

The older Python client uses ASCII `3` (`0x33`), while current OpenRing/ring-set implementations use numeric `0x03`. R06 currently returns useful computed HR with `0x33`, so this must not be changed purely from cross-device assumptions. Raw packets around low candidates will determine whether the legacy poll dialect correlates with abnormal responses.

### H3 — alternate `0x9E` response dialect exists on R06

Plausible but not yet observed directly in this bridge.

Current ring-set code accepts both `0x1E` and `0x9E` poll replies. The bridge currently parses only `0x1E`. Debug probing now records every valid `0x9E` notification verbatim.

### H4 — realtime session type `0x06` contributes to the low-value behavior

Possible but lower confidence.

Most referenced R0x code uses type `0x01`; the bridge uses `0x06` because R06 hardware testing previously showed better continuous behavior with that path. Do not change the session type until raw capture demonstrates a reason.

## Instrumentation added

Debug builds now emit an `hr_protocol_probe` JSONL event when either:

1. a parsed HR candidate is outside the observed `30..220` range, or
2. a valid packet uses command `0x9E`.

Each probe records:

- UTC timestamp
- command byte
- candidate BPM
- reason
- complete 16-byte packet as hex
- link state and relevant HR diagnostics

For now the probe is observational: suspicious low values are still forwarded exactly as before. This preserves evidence until the R06 packet semantics are confirmed.

## Decision gate

After the next low-value event, inspect the raw packet before changing the production parser.

Recommended decisions by capture result:

- `0x1E 02/03/04 ...`: determine whether R06 uses low byte `[1]` values as not-ready/contact-loss status; likely add a parser validity gate.
- `0x9E <value> ...`: add explicit `0x9E` response parsing and classify sentinel values such as `0xEE`.
- low values correlate only with `0x33` polling: A/B test `0x03` vs `0x33` on R06.
- low values come from `0x69` byte `[3]`: investigate session type `0x06` vs `0x01` separately from computed-HR polling.
