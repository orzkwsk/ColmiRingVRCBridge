# Issue #8 R06 protocol / HIL investigation

Research date: 2026-10-10 JST. Stable baseline: `be983f062bcf89889f7216ab19fb54e65155495b` / `v0.1.0-preview.2`. Research branch is not a merge candidate. Production source, solution, release code, main and dev are unchanged.

## Questions, not assumptions

Hypotheses: A firmware reboot; B power-off/deep sleep; C model/firmware-dependent semantics. Windows write success and local GATT disposal prove neither A nor B. Earlier manual wake followed command08, so that historical result is not autonomous reboot evidence. Treat `0x69 byte[2]=0x02` as a raw status fact, not an established protocol error.

## External primary-source comparison

Sources were read locally at the pinned commits below; none of their executables, firmware or OTA paths were run.

| Source / device scope | `0x08` meaning / payload | HR start | HR continue / poll | HR stop | Status interpretation |
| --- | --- | --- | --- | --- | --- |
| [Puxtril docs](https://github.com/Puxtril/colmi-docs/tree/97f118d6ccf91e9f78c9f682054103d4c9f557e6), tested R03, QRing reverse engineering | Reboot, `08 01` | `69 type 01`, type 1 ordinary / 6 realtime | `69 type 03`; `1E` type described as numeric 3 | `69 type 04` enum and separate `6A type 00 00` documented; purpose distinction uncertain | No specific mapping of status byte 2 |
| [tahnok](https://github.com/tahnok/colmi_r02_client/tree/19e70aa502749b87d57e3dba0156dfd67ddc0d4a), R02; README claims R06/R10 compatibility | Reboot, `08 01` | `69 01 01` for HR | Defines `69 type 03` and ASCII `1E 33`, but current client `_poll_real_time_reading` sends START and consumes notifications without either Continue/poll | `6A type 00 00` | Nonzero byte[2] yields ReadingError; no semantic enum for 2 |
| [openring](https://github.com/robinojw/openring/tree/c406e2bb6e5a9c429c73b7593d8baaa92579f0b1), R02 family incl. R06 named, observed firmware not identified as this device | Reboot, `08 01`, ported from tahnok | `69 01 01` | Defines action-3 Continue, but RingClient startRealtime sends START only; separate computed-HR API sends **`1E 03`**, not `1E 33` | `6A type 00 00` | Nonzero byte[2] is failure; source says 69 byte[3] was raw/zero on its observed firmware, which differs from this R06 capture |
| [Gadgetbridge](https://codeberg.org/Freeyourgadget/Gadgetbridge/src/commit/9a72ae39ebbcb7841aa65e0441ff46963cd4a7ab/app/src/main/java/nodomain/freeyourgadget/gadgetbridge/devices/yawell/ring/YawellRingConstants.java), explicit ColmiR06Coordinator | Constant named POWER_OFF; this inspected support path does not establish a transmitted `08 01` action | Manual `69 01` (action byte zero); realtime `1E 01` | Realtime `1E 03` every 30 packets (~60 s session timeout) | Realtime **`1E 02`** | Manual parser: 0 valid, 1 worn incorrectly, 2 temporary error/missing data. These are implementation interpretations |
| [CitizenOneX R06 Flutter](https://github.com/CitizenOneX/colmi_r06_fbp/tree/f492bbb1800b5267c49867b42dedd931e9f38742), explicitly R02–R06 | Reboot, **`08` only**, zero-padded + checksum 08; different from `08 01` | **`69 01` only**, action byte zero | No comparable continuous-HR loop established in inspected handlers | No explicit HR stop found in inspected command enum/handler | Tentative comments on byte[2] 0/1 and wear; handler uses nonzero byte[3] without interpreting 2 |
| [ATC RF03 writer](https://atc1441.github.io/ATC_RF03_Writer.html), RF03/R02-family, not explicit proof for this R06 firmware | UI example Reboot **`08`**; sendDataArray zero-pads, checksum 08 | Example `69 01 01` | Not specified by inspected tool | Not specified | Any nonzero byte[2] displays ring-not-on-finger; does not distinguish 1 vs 2 |
| [RingCLI](https://github.com/smittytone/RingCLI/tree/3cc884d943c4b4052d20ad6bb8697d75cf713060), R02 | **Shutdown `08 01`**; README says charger is needed to restart | `69 type 01`; types 1 batch / 6 continuous | Builder `69 type 03` | `6A type` zero-padded | Accepts ordinary-HR response when byte[2]=0 and value nonzero; no meaning for 2 |

ATC repository snapshot: `7b2e78e0e9f42b7cec95dd1e733670f533089bbb`; writer HTML downloaded separately on this date (not asserted to match that repository commit). Puxtril explicitly limits its physical testing to R03. Model-family compatibility statements are not on-device proof of command08 behavior.

Contradictions retained: Reboot vs Power Off/Shutdown names; `08` vs `08 01`; ordinary type 1 vs realtime type 6; numeric `1E 03` vs ASCII `1E 33`; action-based `69` vs `1E` state control; `69` status 2 wear vs temporary/missing-data interpretations. No external label is selected as authoritative for this R06.

## Exact production UART sequence

16 bytes, byte[15] = sum of bytes[0..14] modulo 256:

| Operation | Packet |
| --- | --- |
| Start | `69 06 01 00 00 00 00 00 00 00 00 00 00 00 00 70` |
| Continue | `69 06 03 00 00 00 00 00 00 00 00 00 00 00 00 72` |
| Poll | `1E 33 00 00 00 00 00 00 00 00 00 00 00 00 00 51` |
| Stop | `6A 06 00 00 00 00 00 00 00 00 00 00 00 00 00 70` |
| Battery | `03 00 00 00 00 00 00 00 00 00 00 00 00 00 00 03` |
| command08 (opt-in research only) | `08 01 00 00 00 00 00 00 00 00 00 00 00 00 00 09` |

Production starts with START → 500 ms → CONTINUE → battery. HR polling starts immediately then repeats every 1 s; battery repeats every 60 s. Stop uses `6A`, **not** `69 action=4`. The production parser names nonzero byte[2] ErrorCode, but this research does not adopt a semantic interpretation from that field name.

## Capture safety and source of truth

Independent `tools/Issue8R06Probe`, excluded from the solution/package. Exact allowlist; reset/OTA/DFU/raw sensor/settings commands denied. Command08 requires an explicit option plus pre-command HR/battery/connection/advertisement evidence. No arbitrary hex CLI.

Private captures in ignored `artifacts/issue8/captures/`; no addresses, serial or individual HR values published. Publishable summaries contain counts/statuses/latencies only. HR response example form: `69 06 00 <HR> <payload redacted> <checksum redacted>`; checksum also hidden to avoid reconstructing an HR byte. Unknown notifications are not published verbatim.

Normal baseline packet order is the on-device source of truth; other implementations are comparison material. Native disconnect events are distinguished from locally initiated cleanup. Windows watcher absence is an observation limit, not a controller/HCI-level proof of radio silence.

## HIL sessions (in progress)

| Session | Conditions | First valid HR after START | Samples / span | 69 byte[2] | Result |
| --- | --- | --- | --- | --- | --- |
| A | User-reported awake/worn; wake method unspecified | 16.961 s | 30 / 28.681 s | 00 × 32 | Sustained HR + battery |
| B | Normal stop/local disconnect/reconnect; same process | 16.951 s | 30 / 28.921 s | 00 × 32 | Sustained HR + battery |
| C | Probe process restart/reconnect; no new wake claimed | 16.947 s | 30 / 28.921 s | 00 × 32 | Sustained HR + battery |
| Non-worn 1–3 | User removed ring; nearby, off charger; unchanged S0, 20 s per trial | No valid HR | 0 each | Each: 00 × 2, 02 × 1 | Battery received; no HR |
| Re-worn | User re-wore ring; same S0 | 16.976 s | 30 / 28.801 s | 00 × 32 | Sustained HR + battery restored |

A/B are trials 1/2 in `baseline-ab.jsonl` (capture-wide label baseline-A); C is `baseline-c.jsonl`. This is process restart of the UART-equivalent probe, not a new WPF application restart trial. No true cold battery cycle or controlled range-loss was performed.

Non-worn capture: `unworn-s0.jsonl`. User-confirmed removal is recorded explicitly; no charger intervention occurred. This is a same-sequence wear comparison, not a command08 test. `0x02` is associated with non-worn/no-valid-measurement in 3/3 trials, but the exact firmware meaning is not identified; zero status packets alone did not imply a usable HR reading.

## Current decision

Command08: **INCONCLUSIVE** pending this investigation's quiet 180 s trial. Status byte 02: **UNKNOWN** pending wear/wake comparison. Issue #8 remains OPEN. Production Reboot functionality is not reinstated.
