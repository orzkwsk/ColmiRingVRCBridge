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

## Earlier setup before confirmed charger wake

User reported awake/worn, off charger near PC. Two six-second production Scans were observed completing with **zero candidates**. A subsequent UI observation showed one selected R06 candidate; no third Scan action was recorded by the agent, so intervening setup-input timing/origin is unspecified. A specific powered-charger attach/detach and re-wear was requested after the zero-candidate scans; at that time completion had not been confirmed. The unchanged Auto reconnect checkbox remained OFF.

Manual Connect requested at **12:07:32.207 JST**. At **12:07:57.162 JST**, the UI reported `ManualConnect failed: COLMI UART GATT service was not found on the selected device.` Connection remained Disconnected and Scan/Connect were operable again. This is a setup/connect failure, not a connected-but-stale measurement-session observation, nor proof that firmware lacks the UART service. At that point no normal HR baseline had been established.

An agent-initiated fresh six-second Scan after this failure again completed with zero candidates. Thus three agent Scan results were observed as zero, alongside the intervening one-candidate UI observation. The service error is thrown for either a non-success GATT result or an empty service list; Release UI does not distinguish those causes. At the end of this earlier setup series, charger attach/detach and re-wear had not been confirmed, the app was disconnected, and the fresh Scan had cleared the candidate.

## Confirmed charger wake: production Release series

The user subsequently confirmed powered-charger attachment, holding for several seconds, detachment, re-wear and return near the PC. Receipt was recorded at **12:32:45.346 JST**; this is not an exact physical-action timestamp. The following is a separate series, with Auto reconnect and Dummy BPM still OFF.

| Fresh Scan | Start requested (JST) | Completion first observed (JST) | Candidates | R06 present |
| --- | --- | --- | --- | --- |
| 1 | 12:34:32.030 | 12:34:51.886 | 0 | No |
| 2 | 12:34:59.386 | 12:35:19.108 | 0 | No |
| 3 | 12:35:29.828 | 12:35:51.687 | 1 | Yes |

Each production Scan has a nominal six-second window. Completion times above are UI observation times, not native Scan end times; they must not be interpreted as 20-second Scan durations. All three actions and completed results were observed. Discovery eventually passed; this series is not a three-zero advertisement/discovery baseline failure. No receive-only watcher or Debug diagnostic was needed for this branch of the test.

### Worn session 1: interrupted by disconnect

Manual Connect requested **12:35:58.167 JST**; Connected / HR init and battery were observed at **12:36:18.941**. Fresh HR and Connected were first observed at **12:36:38.440**, an observed latency upper bound of **40.273 seconds** from the request, not an exact first-packet latency. Connected with recent HR was still observed at **12:37:28.372**.

At **12:37:56.338**, before a full 60-second window from the first fresh-HR observation could be confirmed, the app showed Disconnected and `Heart-rate poll failed (... consecutive): Cannot access a disposed object.` No wear intervention or connection-changing app action occurred in this observation window. Existing Release accessibility descriptions exposed 66 HR packets, HR poll TX 90, and 17 consecutive HR poll failures; at **12:38:29.961**, failures had increased to 48 while still Disconnected, with one battery poll failure. UI text, tooltip and screenshot sampling are not atomic native-event logs.

Classify this as **B: disconnect-side observation**, not a connected-but-stale Issue10 measurement-session failure. Exact native disconnect time, BLE/GATT cause and why polling encountered a disposed object are not established by Release UI. Record the continuing failure counter separately as a reconnect/lifetime follow-up candidate; no Issue1 implementation is changed here. Session 1 does not receive a normal sustained-baseline PASS.

### Worn session 2: normal production baseline PASS

A single, separately labeled baseline-acquisition reconnect was requested at **12:38:37.480 JST**. This is not a wear-transition no-recovery control. Connected, battery and fresh HR were first observed at **12:39:10.264** (observed first-HR latency upper bound **32.784 seconds**). Normal worn observation continued without a connection-changing app action through **12:40:32.645**, spanning **82.381 seconds**.

Existing Release diagnostics showed Streaming / Fresh, HR poll TX increasing from 20 to 110, HR packets increasing from 9 to 99, two battery packets, and zero HR or battery poll failures. The graph and recent Last HR continued updating and the app remained Connected. **NORMAL PRODUCTION HR BASELINE = PASS.** Packet counts here are existing app counters, not counts inferred from graph pixels or raw packet captures.

Wear trial 1 removal was requested only after this PASS.

## Wear-transition HIL

All times below are JST. User confirmation receipts are not exact physical intervention times. No charger, command08, reset, OTA, automatic restart, or connection/measurement/output control was used during removal and untouched re-wear windows. Bringing the app to the foreground and focusing the static Connection text to refresh existing diagnostic descriptions were inspection only; those actions do not send commands or reinitialize a session. Cached accessibility descriptions that disagreed with the current screenshot were excluded. This is Release-app HIL, not raw-packet or native-event tracing.

### Trial 1

**COMPLETE NO-RECOVERY TRIAL / FAIL.**

- Removal confirmed **12:41:28.385**. The current screenshot already showed Connected / HR stale, Last HR **12:41:08.322**, and existing status `Realtime HR error code: 2`. Exact status02 semantics remain unknown.
- At **12:42:47.873**, existing diagnostics showed Stale, HR packets 137, HR poll TX 244, and zero write failures. At **12:43:51.014**, HR packets remained 137 while HR poll TX reached 306, battery packets reached 6, and both poll failure counts remained zero. The non-wear screenshot window through **12:43:44.788** spanned **136.403 seconds** after confirmation.
- Re-wear confirmed **12:44:19.493**, with no charger or recovery control. At intermediate observations the app stayed Connected / HR stale. At **12:46:10.907**, HR packets were still 137, HR poll TX 446, battery packets 8, and poll failures zero.
- Untouched window ended **12:47:28.928**, **189.435 seconds** after re-wear confirmation, with **no natural HR recovery**. End diagnostics at **12:47:34.851**: Connected / HR stale, Last HR unchanged, HR packets 137, HR poll TX 529, battery packets 9, HR/battery poll failures zero. **A: Connected + HR stale + polling continues.** The result was saved before the following control.
- Separately labeled Manual Disconnect requested **12:47:49.124**, complete first observed **12:48:01.509**. Manual Connect requested **12:48:05.772**. Connected, battery and fresh HR were first observed **12:48:44.551**, an observed latency upper bound of **38.779 seconds**. **Session/reinitialization recovers telemetry in this control.** This result does not overwrite the untouched no-recovery outcome.

The manual-control follow-up stayed Connected with fresh HR through **12:49:46.537**, **61.986 seconds** after the first restored-HR observation. Diagnostics at **12:49:53.826** showed Streaming / Fresh, HR packets 85, HR poll TX 102, two battery packets and zero HR/battery poll failures. This provided the normal worn baseline for trial 2.

### Trial 2: interrupted at user request

**INTERRUPTED / EXCLUDED.** This is not a 180-second failure trial.

- Removal confirmed **12:50:18.153**. The screenshot showed Connected / HR stale, Last HR **12:50:05.752**, and status2.
- At **12:52:27.807**, **129.654 seconds** after removal confirmation, the app was still Connected / HR stale. Existing diagnostics showed HR packets 99, HR poll TX 255, five battery packets and zero HR/battery poll failures.
- Re-wear confirmed **12:52:48.525**. The last observation at **12:53:54.693**, **66.168 seconds** after confirmation, still showed Connected / HR stale with Last HR unchanged.
- The user then requested a pause and said they would remove the ring. **INTERRUPTED**, not a complete 180-second no-recovery result. No manual recovery control was run for this trial; physical removal at pause was not timed. The existing Release app was left running, with no further HIL input.

Trial 3 was not run before the pause. Only trial 1 is a completed wear cycle. One reproduced condition and a successful manual control do not authorize implementation or establish an automatic retry policy.

| Condition | Status |
| --- | --- |
| Normal sustained HR | PASS (session 2, 82.381 s observed) |
| Removal / prolonged stale | Trials 1/2: connected stale, polling continues |
| Re-wear automatic recovery | Trial 1: none in 189.435 s; trial 2 interrupted; trial 3 not run |
| Manual recovery control | Trial 1: HR restored and >60 s sustained follow-up PASS; trial 2 not run |

## Decision

**BASELINE DEFECT OBSERVED / COMPLETE FAILURES = 1 / IMPLEMENTATION DECISION = PENDING.** Issue10 remains OPEN. The user paused HIL during trial 2 and subsequently requested continuation with two new complete trials, numbered 3 and 4. Re-establish a normal worn baseline in the same production Release process where possible before each new trial; do not extend trial 2 across the unobserved pause or count it as a completed no-recovery trial. Prior probe worn/unworn results and unknown status02 semantics do not substitute for this production-app baseline. main/dev and Issue8 research branch remain unchanged.

The evidence through the pause is checkpointed on the docs-only branch at the user's explicit resume request. No publication or further HIL input occurred after the pause request and before that resume. This checkpoint is not a main/dev merge or an implementation decision.

Documentation-only change; `git diff --check` passed. No runtime, project, dependency or test files changed, so no build/test rerun was needed for this evidence document. HIL is explicitly incomplete and cannot authorize automatic restart/escalation implementation.
