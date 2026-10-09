using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge.Tests;

public sealed class ConnectionOperationCoordinatorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData((int)ConnectionOperation.ManualScan)]
    [InlineData((int)ConnectionOperation.ManualConnect)]
    [InlineData((int)ConnectionOperation.ManualDisconnect)]
    public async Task ManualAction_CancelsAutoReconnect_ThenJoinsItsCleanupBeforeStarting(int kindValue)
    {
        var kind = (ConnectionOperation)kindValue;
        var coordinator = new ConnectionOperationCoordinator();
        var cancelled = Signal();
        var releaseCleanup = Signal();
        var manualEntered = false;
        CancellationToken ownedToken = default;
        var auto = coordinator.RunAsync(ConnectionOperation.AutoReconnect, async token =>
        {
            ownedToken = token;
            try { await Task.Delay(System.Threading.Timeout.Infinite, token); }
            finally { cancelled.TrySetResult(); await releaseCleanup.Task; }
        });
        var manual = coordinator.RunAsync(kind, _ =>
        {
            manualEntered = true;
            return Task.CompletedTask;
        });
        try
        {
            await cancelled.Task.WaitAsync(Timeout);
            Assert.Equal(kind, coordinator.CurrentOperation);
            Assert.False(manualEntered);
            Assert.False(manual.IsCompleted);
        }
        finally { releaseCleanup.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auto.WaitAsync(Timeout));
        await manual.WaitAsync(Timeout);
        Assert.True(manualEntered);
        Assert.Equal(ConnectionOperation.None, coordinator.CurrentOperation);
        Assert.Throws<ObjectDisposedException>(() => ownedToken.WaitHandle);
    }

    [Fact]
    public async Task ManualDisconnect_CancelsQueuedManualConnect_WithoutConcurrentBodies()
    {
        var coordinator = new ConnectionOperationCoordinator();
        var cleanupEntered = Signal();
        var release = Signal();
        var connectEntered = false;
        var disconnectEntered = false;
        var auto = coordinator.RunAsync(ConnectionOperation.AutoReconnect, async token =>
        {
            try { await Task.Delay(System.Threading.Timeout.Infinite, token); }
            finally { cleanupEntered.TrySetResult(); await release.Task; }
        });
        var connect = coordinator.RunAsync(ConnectionOperation.ManualConnect, _ =>
        {
            connectEntered = true;
            return Task.CompletedTask;
        });
        var disconnect = coordinator.RunAsync(ConnectionOperation.ManualDisconnect, _ =>
        {
            disconnectEntered = true;
            return Task.CompletedTask;
        });
        try
        {
            await cleanupEntered.Task.WaitAsync(Timeout);
            Assert.False(connectEntered);
            Assert.False(disconnectEntered);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RunAsync(
                ConnectionOperation.AutoReconnect, _ => Task.CompletedTask));
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auto.WaitAsync(Timeout));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Timeout));
        await disconnect.WaitAsync(Timeout);
        Assert.False(connectEntered);
        Assert.True(disconnectEntered);
        Assert.Equal(ConnectionOperation.None, coordinator.CurrentOperation);
    }

    [Theory]
    [InlineData((int)ConnectionOperation.ManualScan)]
    [InlineData((int)ConnectionOperation.ManualConnect)]
    [InlineData((int)ConnectionOperation.ManualDisconnect)]
    public async Task Shutdown_RejectsNewActions_CancelsAndJoinsAcceptedAction(int kindValue)
    {
        var kind = (ConnectionOperation)kindValue;
        var coordinator = new ConnectionOperationCoordinator();
        var cancelling = Signal();
        var release = Signal();
        var action = coordinator.RunAsync(kind, async token =>
        {
            try { await Task.Delay(System.Threading.Timeout.Infinite, token); }
            finally { cancelling.TrySetResult(); await release.Task; }
        });
        var shutdown = coordinator.ShutdownAsync();
        var secondShutdown = coordinator.ShutdownAsync();
        try
        {
            await cancelling.Task.WaitAsync(Timeout);
            Assert.False(shutdown.IsCompleted);
            Assert.False(secondShutdown.IsCompleted);
            await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.RunAsync(
                ConnectionOperation.ManualConnect, _ => Task.CompletedTask));
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(shutdown, secondShutdown).WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => action.WaitAsync(Timeout));
        Assert.Equal(ConnectionOperation.None, coordinator.CurrentOperation);
    }

    [Fact]
    public async Task FailedOperation_RecoversToIdle_AllowsRetry_AndShutdownObservesCompletion()
    {
        var coordinator = new ConnectionOperationCoordinator();
        var failure = new TimeoutException("Injected deadline.");
        var attempt = coordinator.RunAsync(ConnectionOperation.ManualConnect, _ => Task.FromException(failure));
        Assert.Same(failure, await Assert.ThrowsAsync<TimeoutException>(() => attempt));
        Assert.Equal(ConnectionOperation.None, coordinator.CurrentOperation);
        await coordinator.RunAsync(ConnectionOperation.ManualConnect, _ => Task.CompletedTask);
        await coordinator.ShutdownAsync();
    }
}
