namespace ColmiRingVRCBridge.Services;

internal static class BoundedBleOperation
{
    private static int _pendingCompletions;
    internal static int PendingCompletions => Volatile.Read(ref _pendingCompletions);

    // AsTask(token) requests WinRT cancellation; WaitAsync(token) bounds our wait
    // even when the driver ignores that request. Late results never enter a session.
    public static async Task<T> AwaitAsync<T>(Task<T> task, CancellationToken token,
        Action<T>? disposeLateResult = null)
    {
        T result;
        try { result = await task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ObserveLate(task, disposeLateResult);
            throw;
        }
        if (token.IsCancellationRequested)
        {
            disposeLateResult?.Invoke(result);
            token.ThrowIfCancellationRequested();
        }
        return result;
    }

    public static async Task AwaitAsync(Task task, CancellationToken token)
    {
        try { await task.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ObserveLate(task, null);
            throw;
        }
        token.ThrowIfCancellationRequested();
    }

    private static void ObserveLate<T>(Task<T> task, Action<T>? cleanup)
    {
        Interlocked.Increment(ref _pendingCompletions);
        _ = task.ContinueWith(completed =>
        {
            try
            {
                if (completed.IsCompletedSuccessfully) cleanup?.Invoke(completed.Result);
                else _ = completed.Exception; // Observe faults without calling application/UI handlers.
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Late BLE result cleanup failed: {ex}"); }
            finally { Interlocked.Decrement(ref _pendingCompletions); }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static void ObserveLate(Task task, Action? cleanup)
    {
        Interlocked.Increment(ref _pendingCompletions);
        _ = task.ContinueWith(completed =>
        {
            try { _ = completed.Exception; cleanup?.Invoke(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Late BLE completion failed: {ex}"); }
            finally { Interlocked.Decrement(ref _pendingCompletions); }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
