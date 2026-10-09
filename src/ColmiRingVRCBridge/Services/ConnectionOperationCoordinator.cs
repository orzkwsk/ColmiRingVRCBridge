namespace ColmiRingVRCBridge.Services;

internal enum ConnectionOperation
{
    None,
    AutoReconnect,
    ManualScan,
    ManualConnect,
    ManualDisconnect
}

// Owns accepted operations (including operations waiting for preempted work).
// A successor cannot enter its body until its predecessor has fully unwound.
internal sealed class ConnectionOperationCoordinator
{
    private readonly object _gate = new();
    private Operation? _current;
    private bool _shutdown;

    public ConnectionOperation CurrentOperation
    {
        get { lock (_gate) return _current?.Kind ?? ConnectionOperation.None; }
    }

    public Task RunAsync(ConnectionOperation kind, Func<CancellationToken, Task> body,
        CancellationToken cancellationToken = default)
    {
        Operation operation;
        Operation? predecessor;
        lock (_gate)
        {
            if (_shutdown) throw new OperationCanceledException("Connection operations are shutting down.");
            if (kind == ConnectionOperation.None) throw new ArgumentOutOfRangeException(nameof(kind));
            predecessor = _current;
            if (predecessor is not null && Priority(kind) <= Priority(predecessor.Kind))
                throw new InvalidOperationException("A connection operation of equal or higher priority is active.");

            operation = new Operation(kind, cancellationToken);
            _current = operation;
        }

        predecessor?.Cancel();
        return RunOwnedAsync(operation, predecessor, body);
    }

    private async Task RunOwnedAsync(Operation operation, Operation? predecessor,
        Func<CancellationToken, Task> body)
    {
        try
        {
            if (predecessor is not null) await predecessor.Completion.Task;
            operation.Token.ThrowIfCancellationRequested();
            await body(operation.Token);
        }
        finally
        {
            operation.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_current, operation)) _current = null;
            }
            // Joiners need completion of cleanup, not propagation of the old action's error.
            // The action's caller observes the returned RunOwnedAsync task's exception.
            operation.Completion.TrySetResult();
        }
    }

    public async Task ShutdownAsync()
    {
        Operation? operation;
        lock (_gate)
        {
            _shutdown = true;
            operation = _current;
        }
        operation?.Cancel();
        if (operation is not null) await operation.Completion.Task;
    }

    private static int Priority(ConnectionOperation kind) => kind switch
    {
        ConnectionOperation.ManualDisconnect => 3,
        ConnectionOperation.ManualScan or ConnectionOperation.ManualConnect => 2,
        ConnectionOperation.AutoReconnect => 1,
        _ => 0
    };

    private sealed class Operation : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        public ConnectionOperation Kind { get; }
        public CancellationToken Token { get; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Operation(ConnectionOperation kind, CancellationToken token)
        {
            Kind = kind;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Token = _cts.Token;
        }
        public void Cancel()
        {
            // Completion may race a preemption request; a finished lifetime needs no cancellation.
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException ex) { System.Diagnostics.Debug.WriteLine($"Connection cancellation callback failed: {ex}"); }
        }
        public void Dispose() => _cts.Dispose();
    }
}
