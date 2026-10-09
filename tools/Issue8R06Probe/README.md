# Issue #8 R06 research probe

Research branch only; **do not merge this probe into dev/main**. No production project reference, solution entry, UI or release-workflow change. .NET 8 Windows console application, using the existing WinRT SDK.

Raw JSONL goes only to ignored `artifacts/issue8/`. It can contain personal HR values. Never commit/upload raw captures. Public reports must omit address, serial and HR values; mask HR and checksum bytes together in response examples because a checksum can reveal a masked byte.

## Presets and safety

- No raw-packet CLI input. Exact packet allowlist; unknown options/payloads rejected.
- Battery `03`, HR `69` type 1/6 action 1/3/4, `1E 33`, and `6A` type 1/6 only.
- `08 01` allowed only with `--mode observe08 --enable-command08`, one trial, and observed sustained HR + battery + connected + prior target advertisement.
- Factory reset `FF` (including Gadgetbridge `FF 66 66`), OTA/DFU service/writes, raw sensor controls, settings/time writes, and all other commands are blocked.
- Reboot is not assumed. The command is called **command08** throughout the capture.
- Target comes from the user's existing connection.json. Probe does not modify it.
- Capture logs UTC ISO timestamp, relative milliseconds, event, direction, packet, command, connection/measurement states, label and intervention.
- Native disconnect is logged separately from local cleanup. Advertisements are target-filtered; absence at the Windows watcher is not proof of absence on air.

| Sequence | TX behavior | Controlled comparison |
| --- | --- | --- |
| S0 | `69 06 01`, delay 500 ms, `69 06 03`, battery, immediate `1E 33` then every second; battery every 60 s; `6A 06 00 00` cleanup | Current production UART sequence at be983f0 |
| S1 | S0 without action-3 Continue | S0→S1 changes only Continue |
| S2 | S1 using type 1; stop uses type 1 | S1→S2 changes only type; R02-compatible Start + polling family, not complete tahnok client emulation |
| S3 | Same packets as S0 | Explicit action-based Continue; alias, not an independent alternative |
| S4 | S0 preceded by stop | Tests clean stop/start; compare to S0 |

`--stop action4` changes only stop encoding to `69 type 04`; default is `6A type 00 00`. Only after evidence warrants it, `--delay` accepts 0/250/500/1000/2000 ms. Do not change type, Continue, stop and delay simultaneously.

## Commands

From the repository root with .NET 8 SDK (this workspace pins it through `artifacts/review/global.json`):

```powershell
dotnet run --project tools/Issue8R06Probe -- --mode self-test
dotnet run --project tools/Issue8R06Probe -- --mode capture --sequence S0 --label worn --duration 45 --output artifacts/issue8/captures/example.jsonl
dotnet run --project tools/Issue8R06Probe -- --mode passive --duration 30 --intervention charger-attach --output artifacts/issue8/captures/wake-example.jsonl
```

Record a physical intervention **only after the user actually reports performing it**. `none` is the default; do not silently label charger/motion/cold boot.

## Quiet command08 observation

`observe08` first records a normal 45-second stream, then sends the one gated packet and records boundaries 10/30/60/180 seconds. It does not continue HR/battery writes or initiate local teardown during quiet observation. A reconnect is attempted only when a fresh target advertisement follows native disconnect, and it is explicitly logged as probe-initiated recovery. This avoids treating active device-open attempts as spontaneous wake.

Do not manually wake/reposition the device during that observation. A failed write is not execution proof. If the command is followed by sustained advertisement absence, test wake interventions separately under matched conditions. Physical firmware reboot/power-off cannot be proved by Windows write completion alone.
