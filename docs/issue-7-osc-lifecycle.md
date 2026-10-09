# Issue #7: OSC lifetime fix — 2026-10-09

[Issue #7](https://github.com/orzkwsk/ColmiRingVRCBridge/issues/7) is fixed by
`6014fa125e981f2640eb38419569ad40e5880c56` (`fix: prevent OSC start-stop lifetime race`).
The dedicated branch `fix/issue-7-osc-stop-crash` was created from freshly fetched
`origin/dev` at `47639ad864211c8736b4ad56a0ef01a9cf96d7fd`, then integrated into dev
with `git merge --ff-only`. It does not reuse the refactor branch or modify Issue #1,
manual connection, BLE cancellation, GATT timeout or reconnect priority.

## Root cause and reproduction

Before editing code, the unchanged shared audit probe reproduced **99 crashes / 100**
on latest dev. The earlier dev `49fe23b` and refactor-feature comparison also
reproduced 99/100 each with the same logical stack. This was a **BASELINE DEFECT /
UNCHANGED**, not a defect introduced by the refactor.

The field that becomes null is `_cts`, not the sender:

1. `Start` allocates CTS and schedules a Task.Run delegate that reads `_cts.Token`.
2. Immediate `StopAsync` copies CTS/task locally, clears both fields, then cancels.
3. The queued start delegate resumes and dereferences the now-null `_cts`.
4. The worker faults before entering `RunAsync`, so `OutputError` is not invoked.
5. Stop's await propagates the fault and skips CTS disposal. IsRunning is already
   false despite incomplete cleanup.

Recorded exception type: `System.NullReferenceException`. Representative frames on
the fresh baseline (full traces retained in the local JSON artifact):

```text
OscOutputService.<>c__DisplayClass10_0.<Start>b__0()  OscOutputService.cs:25
Task`1.InnerInvoke()
ExecutionContext.RunFromThreadPoolDispatchLoop(...)
Task.ExecuteWithThreadLocal(...)
OscOutputService.StopAsync()                       OscOutputService.cs:45
OscBaselineComparison.ImmediateStartStopComparison() SharedOscProbe.cs:26
```

The failed iterations all recorded CTS undisposed, task field cleared,
IsRunning=false and OutputError=0. The first failure was iteration 1, 0.3738 ms.
The probe disposes the detached CTS after recording it.

The before/after probe is identical: .NET SDK 8.0.424, Release, 100 sequential
iterations, new service each time, `127.0.0.1:9000`, HeartRate, Int/RawBpm,
three-second interval, null BPM provider, synchronous `Start` immediately followed
by awaited `StopAsync`. No BPM UDP packets are emitted.

| Run | Iterations | Crashes | CTS disposed after Stop | Task field cleared |
| --- | ---: | ---: | ---: | ---: |
| Fresh dev before edits | 100 | 99 | 1 | 100 |
| Fixed feature | 100 | 0 | 100 | 100 |
| Fixed dev after integration | 100 | 0 | 100 | 100 |

Local artifacts: `artifacts/review/issue-7-before.json`, `issue-7-after.json`,
`SharedOscProbe.cs`, `osc-feature/Probe.csproj` and `TestResults/issue-7-*.trx`.
These ignored audit artifacts are supplemented by committed permanent tests.
Counts describe these runs, not a statistical incidence claim.

## Ownership and completion contract

The existing API is synchronous `Start`, asynchronous `StopAsync` and
`DisposeAsync`; no StartAsync or public API change is introduced. The output
worker performs sender construction asynchronously with respect to its caller.
There is no separate OSC-session resource: the service owns one CTS and worker
per lifetime, and the worker owns its sender/UDP transport through `using`.

- Start captures the token before scheduling; worker continuations never read the
  mutable CTS field. A scheduling failure disposes the just-created CTS.
- A short gate coordinates Start/Stop/Dispose references. It contains no await,
  cancellation, sender construction, network I/O or event invocation.
- The first Stop caller owns cancellation and cleanup. Repeated Stop/Dispose
  calls join a shared completion task and observe its failure if the worker faults.
- Stop cancels, then awaits the worker even if cancellation throws. The worker
  finishes any in-flight send/callback and disposes its sender before exiting.
  CTS disposal and field clearing happen after the join, including on failure.
- IsRunning remains true during cleanup; another Start is rejected until the old
  worker and transport are gone. After Stop returns, no worker remains to emit events.
- Dispose prevents new starts and joins existing cleanup. Repeated Dispose does
  not dispose a transport/CTS twice.
- The service adds no event subscriptions. Existing UI handlers dispatch with
  `Dispatcher.BeginInvoke`, so they do not synchronously wait on their own worker.
  Already queued UI updates retain their existing behavior.
- `OscSender` disposes its UDP client if Connect fails during construction.

## State transitions

States are the lifetime model below; an enum is unnecessary. Failed denotes a
worker that reported OutputError and terminated, whose CTS still belongs to the
service until Stop. IsRunning means an owned lifetime, including Starting,
Stopping and Failed, rather than a guarantee that packets are being sent.

| State / operation | Defined result |
| --- | --- |
| Stopped → Start | Allocate/capture CTS token, schedule worker → Starting. |
| Starting → successful sender creation | Worker enters output loop → Running. |
| Starting → Stop, including before worker is scheduled to run | Cancel token; await worker. Pre-start cancellation skips construction; an already-entered factory must return before cleanup completes → Stopped. |
| Running → Stop | Cancel; await in-flight send/callback and worker-owned sender disposal; dispose CTS and clear state → Stopped. |
| Starting/Running/Stopping/Failed → Start | InvalidOperationException; existing lifetime is retained. |
| Starting/Running → sender/provider/send failure | OutputError, worker exits/disposes sender → Failed; Stop joins and disposes CTS. |
| Scheduling failure | Start throws; CTS is disposed immediately → Stopped; Stop remains safe. |
| Error handler itself throws | Worker faults; Stop observes the fault but still cleans up → Stopped. |
| Stopping → Stop or Dispose | Join the first Stop's cleanup; one disposal per resource. |
| Stopped → Stop / Stop after cancelled start | Successful no-op. |
| Stop completed → Start | Create a new independent lifetime; output resumes. |
| Dispose requested → Start | ObjectDisposedException; shutdown never launches a new worker. |

Start during Stopping is rejected rather than queued; callers can await Stop then
Start. Start does not return a task; cancellation is requested and joined by Stop,
and its worker completes normally for its own cancellation.

## Regression and verification

`OscOutputServiceTests` adds 11 cases (theory rows included): two 100-cycle
immediate-stop tests (real sender with exact baseline conditions, and counted fake
sender), deterministic queued-start cancellation, cancellation during held sender
construction, concurrent Stop/Stop/Dispose during held send, scheduling failure,
sender creation failure with restart, throwing error callback with both Stops
observing the fault, and three real loopback UDP normal-start/stop/restart cases.

Held workers/factories/sends use explicit release signals, not timing-dependent
sleeps. Tests verify completed workers, disposed CTS, cleared lifetime references,
one sender disposal, and no premature transport disposal. Loopback tests assert
destination host/port, padded OSC address/type tags and big-endian values for
Float/Normalize255, Float/RawBpm and Int/RawBpm. Existing named-argument,
deconstruction, with, option-validation and JSON/CSV compatibility tests remain.

| Check | Before integration | After integration on dev |
| --- | --- | --- |
| Debug build | PASS | PASS |
| Release build | PASS | PASS |
| Debug tests | 41/41 PASS | 41/41 PASS |
| Release tests | 40/40 PASS | 40/40 PASS |
| Issue #7 regressions | 11/11 PASS | 11/11 PASS |
| Failure paths | PASS | PASS |
| Serialization / options compatibility | PASS | PASS |
| Identical Release 100-run audit probe | 0/100 crashes | 0/100 crashes |
| Normal start/stop and restart, actual UDP | PASS | PASS |

Builds use .NET SDK 8.0.424 and retain the pre-existing xUnit2020 warning in
R06SessionTests. No warnings are suppressed and no existing tests are removed.
The post-integration checks ran on dev at `6014fa1` before push. The subsequent
documentation commit changes only Markdown, so no further build/test run is
needed: source, tests, project settings and dependencies match the verified tree.

## Review, decision and remaining risks

**MERGE APPROVED: Blocker 0 / Major 0.** Self-review confirms bounded reference
locks, no lock across await/I/O, no UI-thread wait, tracked/observed worker tasks,
CTS cleanup on cancellation/fault, sender-before-CTS disposal, double-stop joining,
and no null suppression. Public OSC options/signatures, packet encoding, persisted
configuration and BLE/reconnect code are unchanged.

Synchronous hostname resolution/Connect and an in-flight UDP send retain their
existing timeout behavior. Stop waits for them rather than abandoning a live
worker; this change introduces no new hard-timeout policy. The delayed-operation
tests verify ownership/order, not arbitrary network timing bounds.

GUI/R06 HIL is not executed; the Issue #7 race and acceptance conditions require
neither. Application-shutdown service disposal is covered by concurrent/repeated
Dispose testing and the existing awaited shutdown call site is unchanged. The
independent [Issue #1](https://github.com/orzkwsk/ColmiRingVRCBridge/issues/1)
manual-connect/BLE lifetime risk remains outside this fix.
