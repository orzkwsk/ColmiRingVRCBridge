# Issue #10 production-app HR recovery baseline

Issue: https://github.com/orzkwsk/ColmiRingVRCBridge/issues/10

Date: 2026-10-10, Asia/Tokyo. Source of truth: main/dev `be983f062bcf89889f7216ab19fb54e65155495b`, release `v0.1.0-preview.2`. This branch contains baseline documentation only. No automatic HR restart/escalation, protocol or runtime change is implemented. Issue8 research branch remains separate and must not be merged into dev.

## App identity and observability

Existing self-contained **production WPF Release publish** at the unchanged source commit, `0.1.0-preview.2+be983f062bcf89889f7216ab19fb54e65155495b`.

- Executable SHA256: `7B9D29A3517F588139A8C6BDE9CC96EAD841290A7947684C100F92443A1888E9`.
- Application DLL SHA256: `C2C66855803DE64A0E14E4A4B19FAF1B3E21C831D554DEDC9D4661A8EEE1D6B1`.
- Initial observed settings: Auto reconnect OFF, Dummy BPM OFF, disconnected.
- Existing HR freshness threshold: 5 seconds; battery freshness: 3 minutes.
- Source/protocol S0 unchanged: start `69 06 01`, 500 ms, continue `69 06 03`, poll `1E 33` every second, cleanup `6A 06 00 00`.

The initial local `published/` apphost could not launch because its companion DLL was missing; the complete existing `main-win-x64/` publish is used instead. This is a local artifact condition, not evidence of a release-package/runtime defect. The app is not the Issue8 probe. Release has no Debug raw-packet logger: screenshots/UI/tooltips cannot establish every native event or exact UART status. Do not invent raw-packet counts from graph pixels.

Private intervention timeline in ignored `artifacts/issue10/`. Individual HR values, address and serial are not published. User confirmation receipt is not exact physical-action onset. No command08, factory reset, OTA, HR restart or added instrumentation is used.

## Planned baseline

1. Awake/worn, near PC and off charger: production Scan/Connect, battery and at least 60 seconds normal HR reception.
2. User removal without application controls; observe at least 60 seconds beyond freshness expiry, preferably 120 seconds, and record connection/telemetry/polling evidence available in Release.
3. User re-wear without charger/application controls; observe up to 180 seconds for spontaneous sustained HR recovery. Initially target three complete wear transitions.
4. Classify actual disconnect/reconnect separately from connected-but-stale measurement behavior. If Release observability is inadequate, label it and consider a separate unchanged-production Debug diagnostic run.
5. Only after an untouched no-recovery window, measure a labeled manual Disconnect/Connect control. Normal app restart and shutdown are separate controls.

## Current observations

User reported awake/worn, off charger near PC. Two six-second production Scans were observed completing with **zero candidates**. A subsequent UI observation showed one selected R06 candidate; no third Scan action was recorded by the agent, so intervening setup-input timing/origin is unspecified. A specific powered-charger attach/detach and re-wear was requested after the zero-candidate scans; completion is pending user confirmation. The unchanged Auto reconnect checkbox remains OFF.

Manual Connect requested at **12:07:32.207 JST**. At **12:07:57.162 JST**, the UI reported `ManualConnect failed: COLMI UART GATT service was not found on the selected device.` Connection remained Disconnected and Scan/Connect were operable again. This is a setup/connect failure, not a connected-but-stale measurement-session observation, nor proof that firmware lacks the UART service. No normal HR baseline has yet been established.

An agent-initiated fresh six-second Scan after this failure again completed with zero candidates. Thus three agent Scan results were observed as zero, alongside the intervening one-candidate UI observation. The service error is thrown for either a non-success GATT result or an empty service list; Release UI does not distinguish those causes. Charger attach/detach and re-wear confirmation is still pending. The app remains disconnected; the candidate was cleared by the fresh Scan.

| Condition | Status |
| --- | --- |
| Normal sustained HR | NOT MEASURED |
| Removal / prolonged stale | NOT MEASURED |
| Re-wear automatic recovery | NOT MEASURED |
| Manual recovery control | NOT MEASURED |

## Decision

**BASELINE PENDING / NO IMPLEMENTATION DECISION.** Issue10 remains OPEN. Prior probe worn/unworn results and unknown status02 semantics do not substitute for this production-app baseline. main/dev remain at the released source commit.

Documentation-only change; `git diff --check` passed. No runtime, project, dependency or test files changed, so no build/test rerun was needed for this evidence document. HIL is explicitly incomplete and cannot authorize automatic restart/escalation implementation.
