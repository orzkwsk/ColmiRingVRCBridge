# Issue #1 / PR #6 integration and lifetime review

Reviewed 2026-10-09–10 (JST). Software decision: **MERGE APPROVED / HIL PENDING**.
The existing [PR #6](https://github.com/orzkwsk/ColmiRingVRCBridge/pull/6) stays Draft;
[Issue #1](https://github.com/orzkwsk/ColmiRingVRCBridge/issues/1) stays open until
the remaining hardware observations and final CI gate are complete.

## Integration

- Old remote feature: `0b9ddcd9a1ad38a085f24c464c6a58622a8d6593`.
- Integrated dev: `4cee02e9bd7f8b11ffe2a501913746cb5461f526`.
- Existing local normal merge: `7ee9906f0e323da0005a765438ec48e31de7355c`.
  Another paused task had already made this merge, with no implementation or push.
  Its clean checkout and two parents were verified and reused without rewriting history.
- Conflict files: `Services/ColmiRingBleService.cs`, `tests/R06SessionTests.cs`.
  Keep current dev session cleanup and tests; adapt reconnect/reboot behavior above them.
  Normalize the merged test file to dev's line endings, preserving all existing tests.
- Lifetime fix/tests: `100416e356dbd7204d4c48a76468671dea70abf3`.
- OSC service/options, OSC regressions, serialization tests, persistence queue and
  logger implementation are unchanged from dev. Current shutdown still awaits
  persistence and Debug logging drain before the final Close.

## Root cause and ownership

The baseline manual Connect handler awaited BLE initialization without a tracked
operation Task/CTS. Shutdown joined AutoReconnect only, so service disposal could
race the manual continuation and publish a session or update a closing window.
An attempt-wide token alone also did not bound a driver that ignores WinRT cancel.

`ConnectionOperationCoordinator` owns every accepted manual/automatic operation,
its CTS and cleanup-completion Task. Disconnect/Reboot have priority 3,
Scan/Connect 2, AutoReconnect 1. A higher-priority request first cancels its
predecessor and awaits its entire unwind before entering its body. Equal/lower
requests are rejected. Errors belong to each action's awaiting caller; joiners
await successful cleanup completion. Shutdown closes admission, cancels the
accepted chain and joins it. CTS disposal precedes completion publication.

The BLE service serializes commands. `WinRtBleConnection` owns one attempt's
device, service, transport and protocol session locally. Only fully initialized,
uncancelled attempts can transfer ownership to the service. Failure/cancellation
disposes the unpublished attempt; published events are filtered by owner identity.
Dispose cancels accepted service commands, joins them, detaches the current owner,
unsubscribes events, joins polling, then releases transport/service/device resources.
Repeated service Dispose shares a completion Task; repeated Disconnect cannot
dispose a detached connection twice.

Manual event handlers await coordinator Tasks, and guard UI continuations with
cancellation and `_closing`. Shutdown remains asynchronous with no `.Wait()` or
`.Result` on the UI thread. Manual suppression does not change persistent Auto
ON/OFF. Explicit OFF→ON resumes suppressed reconnect; retries retain the fixed
one-second delay. A predecessor's error cannot disconnect a successor's session.

## Bounds and native cancellation

- Connection initialization: unchanged 15 seconds, covering device open, UART
  services, RX/TX characteristics, device-info reads, CCCD enable and session start.
- WinRT calls receive `AsTask(token)` and the app awaits through `WaitAsync(token)`.
  A late native result is observed and owned device/service results are disposed;
  it cannot resume initialization, publish a session or reach UI callbacks.
- Individual writes: 3 seconds, including write-gate acquisition. This bounds
  background polling even if a native write never acknowledges cancellation.
- Cleanup STOP: independent 1-second token; CCCD disable: independent 2-second token.
  Polling tasks are joined before transport teardown. Write waiters recheck Dispose.
- Scan: six-second advertisement collection, then best-effort name resolution
  bounded to two seconds per unknown device. The total scan is therefore not a
  global six-second deadline. Shutdown cancels collection/resolution and joins it.
- Native cancellation cannot promise that Windows stops an underlying request.
  Late-completion observers may remain pending until the OS completes it; they
  observe faults and clean results without retaining the current session or UI.
  `pendingLateBleOperations` is logged to distinguish this from application work.

## Verification

.NET SDK 8.0.424, final source/test commit above:

| Check | Result |
| --- | --- |
| Debug build | PASS, zero errors |
| Release build | PASS, zero errors |
| Debug tests | 78/78 PASS |
| Release tests | 77/77 PASS |
| Issue #7 OSC regressions | 11/11 PASS in both |
| BLE lifetime regressions | 25/25 PASS in both |
| Operation priority/join regressions | 10/10 PASS in both |
| Serialization/API compatibility | 2/2 PASS in both |
| Reboot cases | 3/3 PASS in both, including shutdown cancellation |
| Manual connect → shutdown loop | 100 runs, 0 crash, 0 published stale session; all fake resources disposed and subscriptions removed |
| Diff/whitespace review | PASS |

The six BLE stages are held independently while shutdown, deadline or a driver
fault is injected. Tests prove cleanup, idle-state recovery and retry, including
completion after cancellation, completion before cancellation, late driver faults,
cleanup failure, queued-operation preemption and repeated service disposal.
The tests exercise actual service/coordinator orchestration with fake native
connections; they do not execute Windows drivers or WPF control enablement.
The existing xUnit2020 warning at `R06SessionTests` remains unsuppressed.

## Hardware observations

R06 on Windows, final application source; timestamps are JST. Raw logs and settings
stay local under `artifacts/review` / the user's application data; device address,
serial and heart-rate values are excluded from this report.

| Scenario | Observation |
| --- | --- |
| Normal Scan/Connect | PASS. Scan found one R06; 23:55:02 session ready, battery and live heart-rate UI subsequently updated |
| AutoReconnect → Scan | PASS. Auto UART discovery pending; 23:56:55.225 cancel completed, 23:56:55.226 Scan began, 23:57:01.401 Scan completed and buttons recovered |
| AutoReconnect → Manual Connect | PASS for priority/recovery. Auto device-info read pending; 00:02:48.621 cancel completed, 00:02:48.625 manual device open began. Manual device-info reached the 15-second deadline at 00:03:03.637; Auto later restored the session at 00:03:10.307 |
| Manual Disconnect with Auto ON | PASS. 23:55:45.491 cleanup completed; checkbox remained ON, no auto attempt for over 50 seconds, and Connect/Scan were enabled. Only explicit OFF→ON resumed retries |
| Native link loss / Auto recovery | Observed. Published session lost its link before Auto cleanup at 00:03:23; subsequent attempts stayed manually preemptible and session/telemetry recovered at 00:05:41.726. Controlled physical trigger confirmation remains pending |
| Experimental Reboot | GATT write returned successfully at 00:07:00; local cleanup 00:07:00.817–.844, then normal Auto attempts from 00:07:01.859. No ready session initially; after the user woke R06 again, Auto restored the session at 00:10:27.742 and battery notifications resumed. Actual firmware restart remains unconfirmed |
| Application Close | Normal asynchronous exit and final `logger_stopping` observed on the first GUI run. Close landed after Scan completion, so this is not claimed as a close-during-Scan HIL pass |

At the manual device-info deadline, one native completion was temporarily pending;
it returned to zero before the next ready session. Stage telemetry reports the
currently accepted coordinator operation: cancellation of an old Auto attempt
can therefore be labeled ManualScan/ManualConnect after a successor is accepted.
Read the preceding stage sequence to identify the canceled attempt.

## Review and remaining gate

Branch Blocker 0 / Major 0 / Minor 0 found in software review. Short locks cover
ownership only; no await, I/O or cancellation callback runs under them. No new
unowned application worker, current-CTS lookup race, stale attempt publication,
double ownership or double cleanup is present in reviewed paths. Existing dev
polling lifetime, logger drain, persisted formats and OSC lifetime fix are retained.

The user cannot perform a distance-induced disconnect, so the controlled physical
loss test is NOT TESTED. Naturally occurring native link loss/recovery is recorded
above; it is not relabeled as that controlled test.

After Reboot/wake, the connected ring also reported realtime HR error code 2;
the UI remained Connected/HR init. Session creation and battery notification
recovery are confirmed, but sustained post-Reboot heart-rate recovery is not
claimed. Initial normal connection did deliver live heart-rate samples.

Remaining hardware notes: controlled physical loss/recovery and the complete
Reboot outcome must be recorded before marking PR ready, merging dev or closing
Issue #1. A successful GATT write proves command acceptance by Windows, not that
R06 firmware rebooted. The command remains explicitly experimental and is never
sent by automatic recovery.

After those observations pass: verify the latest PR head's CI, mark Ready,
integrate dev without force push, rerun both builds/tests on dev, append results
to the existing Issue #1 body and close it as completed. Until then dev stays at
`4cee02e` and no post-merge result is claimed.
