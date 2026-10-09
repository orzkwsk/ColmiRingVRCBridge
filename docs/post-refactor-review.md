# Post-refactor review: baseline comparison and merge re-evaluation — 2026-10-09

Issue #7 follow-up: the separate OSC lifetime fix is now integrated into dev by
`6014fa1`. See [Issue #7 fix and verification](issue-7-osc-lifecycle.md) for fresh
99/100 → 0/100 reproduction and post-integration checks. The original comparison
and refactor verification below remain historical evidence.

Issue #1 / PR #6 follow-up: `feature/reconnect-recovery` now includes current dev
and the tracked manual/automatic BLE operation lifetime fix `100416e`.
After separating experimental Reboot, Debug 73/73 and Release 72/72 PASS,
including all 11 OSC regressions. See [Issue #1 final reconnect review and HIL
scope](issue-1-reconnect-review.md). Natural link loss/recovery PASS; controlled
distance loss is a non-blocking coverage Note. Reboot observations and unknown
post-reboot HR error code 2 move to independent [Issue #8](https://github.com/orzkwsk/ColmiRingVRCBridge/issues/8).
Final CI/merge and post-merge verification are recorded on PR #6 / Issue #1.

PR #6 is now merged normally into dev at `ba9c079`; post-merge Debug/Release
builds and tests PASS (73/73 and 72/72), and Issue #1 is completed. Experimental
Reboot remains open in Issue #8. See [completed integration](issue-1-reconnect-review.md#completed-integration--2026-10-10-jst).

This report supersedes the first decision committed as `8c618a3`. That decision
counted unchanged repository defects against the feature gate. Under the follow-up
instructions, the gate covers newly introduced/worsened defects and inconsistent
integration diffs; baseline defects are tracked independently.

## Baseline comparison

### OSC stop crash

Identical shared probe source, .NET 8.0.424, Release, 100 iterations, localhost
9000, Int/RawBpm, three-second interval, null BPM provider, immediate Start/Stop.
No BPM UDP packets are emitted. The probe records exception, stack, timing and
post-stop ownership state, then cleans up its detached CTS.

| Observation | origin/dev `49fe23b` | Feature with review fixes |
| --- | --- | --- |
| Reproduction | 99/100 | 99/100 |
| Exception | System.NullReferenceException | System.NullReferenceException |
| Throw site | Start Task.Run delegate, line 23 | Same delegate, line 25 |
| Propagation | StopAsync await loopTask, line 43 | Same await, line 45 |
| First failing iteration | 1, 0.3466 ms | 1, 0.3417 ms |
| State after failed Stop | IsRunning=false; task field=null; CTS undisposed; OutputError=0 | Identical in all 99 failures |

**Classification: BASELINE DEFECT / UNCHANGED.** Both delegates read the mutable
`_cts.Token` after Stop can clear the field. Feature adds validation before Start;
the two-line shift does not change this task/stop sequence. Timings/counts are
reproduction evidence from one run, not statistical performance or incidence claims.
The intentional diagnostic FAIL on both sides is excluded from the branch software
gate and is explicitly recorded, not hidden or declared PASS.

Local comparison artifacts: `artifacts/review/SharedOscProbe.cs`,
`osc-baseline/Probe.csproj`, `osc-feature/Probe.csproj`; each probe's output directory
contains `comparison.json` with all exception stacks and post-stop states.

### Manual connection shutdown race

**Method: code-path comparison; hardware reproduction NOT PERFORMED.**

- On both sides, manual `ConnectButton_Click` is async void and calls
  `ConnectAsync(selected)` without a tracked manual-operation CTS/Task.
- `MainWindow.Reconnect.cs` is identical between dev and feature. Shutdown joins
  the auto reconnect loop, not the manual connection task.
- On both sides, device open/discovery and Dispose can touch the same service
  fields while a manual operation is outstanding. The missing cancellation/join
  and the affected connect/dispose windows are unchanged.
- Feature adds finally-based resource cleanup and waits for persistence/log drain
  after BLE disposal. It prevents a second Close from bypassing teardown. It does
  not launch another manual operation or remove an existing cancellation/await.

**Classification: BASELINE DEFECT / UNCHANGED (code-path comparison).** Resource
cleanup is improved. Real Windows scheduling and close-during-connect symptoms
remain GUI/HIL pending; no runtime PASS is claimed.

## Branch findings

- **Blocker: 0.**
- **Major: 0 after correction.** Dev comparison found one additional source API
  regression: `554f1be` restored PascalCase record parameter names but dropped the
  lowercase named arguments supported by dev. `503ba0e` adds the necessary
  forwarding overload and marks the JSON constructor explicitly. Both naming
  styles, positional calls, deconstruction, with, invalid-input rejection and JSON
  round-trip now pass. No baseline OSC/manual-lifetime fix is included.
- **Minor: 0 outstanding branch findings.** Promotion-gate documentation is
  updated to separate Software / GUI / HIL and to reflect the selected preview
  stale-source contract.
- **Note:** Full MVVM, bounded/coalescing queues and WinRT hard-timeout policy remain
  separate design work. The existing xUnit2020 warning is baseline and unsuppressed.

Focused review confirms session/logger tokens are captured before scheduling;
both polling tasks are awaited and transport cleanup runs on faults; battery
snapshot/enqueue order uses the same short memory lock; flush barriers run after
BLE teardown; Debug logger drain is awaited before Close; invalid copied OSC
settings are rejected before background output starts. Protocol commands, OSC
encoding, persisted fields, stale thresholds and the fixed reconnect cadence
are unchanged. Runtime GUI/WinRT ordering remains explicitly unverified.

## Baseline findings

- **Original Blocker: 1, now resolved:** [OSC start/immediate-stop crash — Issue #7](https://github.com/orzkwsk/ColmiRingVRCBridge/issues/7), originally reproduced on dev and feature; fixed independently by `6014fa1` ([verification](issue-7-osc-lifecycle.md)).
- **Original Major: 1, now resolved:** [Manual connection shutdown lifetime — Issue #1](https://github.com/orzkwsk/ColmiRingVRCBridge/issues/1), statically shared by both original baselines. Fixed by merged [PR #6](https://github.com/orzkwsk/ColmiRingVRCBridge/pull/6) at `ba9c079`; see [final reconnect verification](issue-1-reconnect-review.md).
- **Minor:** xUnit2020 warning in R06SessionTests, emitted by both baselines.

See `baseline-issues.md` for reproduction, expected/actual, probable cause,
affected code and acceptance checks. With explicit user approval on 2026-10-09,
the manual-lifetime finding was appended to Issue #1 while retaining its original
body, and the OSC finding was published separately as Issue #7. Both are
BASELINE DEFECT / UNCHANGED; neither is introduced by this refactor.
The separate reconnect-recovery PR/branch is not merged or modified here.

## Remaining risks

- [Issue #1](https://github.com/orzkwsk/ColmiRingVRCBridge/issues/1): resolved by
  merged [PR #6](https://github.com/orzkwsk/ColmiRingVRCBridge/pull/6). Post-merge
  delayed-stage, timeout/fault, late-result and 100-run shutdown tests PASS.
  Principal reconnect HIL paths PASS; [controlled distance loss](issue-1-reconnect-review.md)
  is a non-blocking Note. Experimental reboot and post-reboot HR investigation
  remain open separately in [Issue #8](https://github.com/orzkwsk/ColmiRingVRCBridge/issues/8).
- [Issue #7](https://github.com/orzkwsk/ColmiRingVRCBridge/issues/7): resolved separately
  by `6014fa1`. Fresh baseline 99/100 crashes → fixed dev 0/100; every CTS disposed
  and worker joined. Debug 41/41 and Release 40/40 tests PASS after integration.
  See [OSC lifetime contract and remaining network timing limits](issue-7-osc-lifecycle.md).
- Principal Issue #1 GUI/R06 reconnect paths PASS in the follow-up review.
  Controlled distance loss remains a non-blocking Note; experimental reboot HIL
  remains separate in Issue #8. Issue #7's real UDP and lifetime tests still PASS.

## Git topology

Fetched integration baseline: `49fe23be84c8700d21d626a0bd9044cdf1722814`.

- Merge-base: `ecce742937d8ef28b182a21ca14e80e957fb4be4`.
- Initial dev-only: 13 commits for README, license, changelog, preview version,
  release notes, CI/publish workflows and release merges.
- Initial feature-only: `554f1be` code/tests and `8c618a3` first review report.
- `8c618a341e05ecf3ba5bcb6f734441f68d601986` has the single parent
  `554f1be56edd3be586d4e267419eaf2a12047e02`; there are no intervening commits.
  It adds only the 117-line first review report, so HEAD and code-fix hash differ.
- Patch equivalence: `git cherry origin/dev HEAD` marks the initial two feature
  commits `+`, and left/right cherry-pick comparison removes none as equivalent.
  Original refactor commits are shared ancestors, not duplicate feature-only work.
- Continuation adds `503ba0e` for named-argument compatibility and a documentation
  commit for this comparison, promotion-gate update and separate baseline drafts.

## Verification

Pre-integration, .NET 8.0.424:

| Gate/check | origin/dev snapshot | Feature |
| --- | --- | --- |
| Release build | PASS, xUnit2020 baseline warning | PASS, same baseline warning |
| Debug build | PASS, same warning | PASS, same warning |
| Release tests | 19/19 PASS | 29/29 PASS |
| Debug tests | 19/19 PASS | 30/30 PASS |
| Added regressions | N/A | 10 Release + 1 Debug PASS |
| Failure paths | Existing suite PASS; OSC probe FAIL | Session dispose/fault/cancel, failed persistence continuation, invalid copy validation and logger drain PASS; OSC probe FAIL |
| Serialization | Existing source format | CSV, connection JSON/defaults and OSC options round-trip PASS |
| Software automated gate | Baseline defects separately tracked | PASS |
| GUI gate | NOT TESTED | NOT TESTED |
| HIL gate | NOT TESTED | NOT TESTED |

No warning suppression or existing-test deletion. Final diff and merge-tree must
preserve dev's README/version/license/CI files while adding only review fixes,
tests and documentation. Post-merge build/test results will be recorded after
executing the required checks on local dev; pre-merge results do not substitute.

## Integration strategy

**normal merge (Case A).** All feature-only commits contain required review fixes
or review documentation. There is no equivalent refactor patch on an unrelated
feature-only history, no revert of dev release work and no unwanted source change.
The common refactor is already the merge-base. Normal merge preserves both sets
of commits; selective cherry-pick would add no filtering benefit here.

Before execution, commit the completed feature work, check a clean tree, fetch dev,
inspect the simulated merge tree against dev, create local tracking dev, then use
`git merge --no-ff feature/telemetry-liveness-hardening`. No rebase or force push.
After integration, rerun both builds/tests on dev and only then push origin dev.

## Merge decision

**MERGE APPROVED / BASELINE ISSUES REMAIN.**

Branch Blocker=0 and Branch Major=0 after the named-argument correction; software
automated gate PASS. The unchanged baseline OSC/manual shutdown defects remain
separate. GUI and HIL are pending and not reported as PASS.

## Integration status

Normal merge completed on local tracking dev at
`360f6d8e2d4caa34016ebc2163d260b75c422290` (parents: fetched dev `49fe23b`
and feature `3f7a225`). No conflict. The merged tree exactly matches the inspected
simulation, and dev README/version/license/CI/release files are unchanged.

Post-merge .NET 8.0.424 checks on dev:

- Release build: PASS (0 errors; existing xUnit2020 warning).
- Release tests: 29/29 PASS.
- Debug build: PASS (0 errors; same baseline warning).
- Debug tests: 30/30 PASS.
- Regression, failure-path and serialization cases: PASS as included above.
- GUI / HIL: NOT TESTED.

The verification follow-up was normally pushed at `af36c4c`; local dev and
origin/dev equality was confirmed. The 2026-10-09 issue-publication follow-up changes
only Markdown documentation and tracker links. Build/test are not rerun because
application code, tests, project/build configuration and dependencies are unchanged.
Documentation diff and whitespace checks are performed before its docs commit/push.
