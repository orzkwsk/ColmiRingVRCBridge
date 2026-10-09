# Baseline issue follow-ups — 2026-10-09

These baseline findings are separate from the post-review feature merge gate.
Compared baseline: `origin/dev` at `49fe23be84c8700d21d626a0bd9044cdf1722814`.
No unrelated defect fix is included in this integration.

## Issue draft: OSC start/immediate-stop cancellation-source race

No matching existing issue was found in the repository issue search on 2026-10-09.
This is a ready-to-file draft; a new remote issue was not requested or created.

### Reproduction

Run the identical `SharedOscProbe.cs` harness against the dev snapshot and feature.
Use .NET 8.0.424 / Release, 100 sequential iterations, localhost port 9000,
Int + RawBpm, a three-second interval and a BPM provider returning null (no UDP BPM
packet is sent). Each iteration calls Start immediately followed by await StopAsync.

```csharp
var output = new OscOutputService();
output.Start(new OscOutputOptions("127.0.0.1", 9000, "HeartRate",
    OscValueType.Int, ScalingMode.RawBpm, TimeSpan.FromSeconds(3)), () => null);
await output.StopAsync();
```

### Expected

Stop is safe immediately after Start. The task terminates and the cancellation
source is disposed; no exception escapes into a WPF async void event handler.

### Actual

Both baseline and feature reproduced `System.NullReferenceException` in 99/100
iterations. Failure occurs in the Task.Run delegate reading `_cts.Token` after
StopAsync has cleared `_cts`. It propagates through await loopTask in StopAsync.
On failure, IsRunning is false and the stored loop task is null, but the detached
CTS was not disposed. OutputError is not raised because the failure precedes
entry into RunAsync. The harness disposes this detached CTS after recording it.

Representative frames:

- baseline: `OscOutputService.<Start>b__0`, line 23 -> StopAsync, line 43;
- feature: same delegate, line 25 -> StopAsync, line 45.

The two-line shift is Start validation. The cancellation-source access and stop
sequence are otherwise unchanged. Counts from one run are reproduction evidence,
not a statistical claim about failure probability.

### Probable cause / affected code

`src/ColmiRingVRCBridge/Services/OscOutputService.cs` captures a mutable CTS field
in Task.Run instead of capturing its token before scheduling. Stop clears the
field before awaiting the task, and CTS disposal is skipped on non-cancel faults.

### Verification requirements

Add a deterministic start/immediate-stop regression test, repeated stop and
fault-cleanup tests; verify CTS disposal, no OutputError callback after disposal,
no remaining output task and a subsequent successful start. Preserve existing
OSC encoding/scaling behavior. A separate fix should capture the token and
ensure fault cleanup; this review does not implement that fix.

## Existing Issue #1: manual connection shutdown lifetime

Destination: https://github.com/orzkwsk/ColmiRingVRCBridge/issues/1

The following text is the proposed append to its existing body. The original
body, state, labels and assignees would be preserved. Automatic approval review
rejected the body update; no remote change was made. Explicit approval for this
specific destination and text is pending.

---

## Post-refactor baseline comparison: manual connection shutdown (2026-10-09)

- **Classification:** BASELINE DEFECT / UNCHANGED by code-path comparison between dev `49fe23be84c8700d21d626a0bd9044cdf1722814` and the post-review telemetry-liveness feature. Hardware reproduction was not performed.
- **Reproduction / verification scenario:** Start manual Connect, hold or delay device open / GATT discovery, then close the application while the operation is in flight.
- **Expected:** Closing cancels and awaits the manual operation before disposing BLE resources; no continuation creates a new session or updates the closing View.
- **Actual code path:** `ConnectButton_Click` is async void and calls `ConnectAsync(selected)` without a tracked manual CTS/Task. `Window_Closing` waits for auto reconnect, then disposes the BLE service without joining the manual connection operation. Both paths can access the same mutable device/session fields.
- **Probable cause / affected code:** Missing ownership and cancellation/join of the manual-operation lifetime in `MainWindow.xaml.cs`; shared connection/disconnection state in `Services/ColmiRingBleService.cs`.
- **Verification requirements:** Exercise close-during-connect at device open, service discovery, characteristic discovery and session startup; check no new session appears after teardown, all resources are released, callbacks cease, and no unhandled UI exception occurs. Include deterministic delayed-operation tests and Windows/R06 HIL.
- **Scope:** The post-review fixes improve cleanup and await persistence/log drain but do not change the missing manual CTS/Task coordination. This unchanged baseline issue is tracked separately from their merge gate. The implementation on the separate reconnect-recovery PR has not been re-evaluated by this comparison.
