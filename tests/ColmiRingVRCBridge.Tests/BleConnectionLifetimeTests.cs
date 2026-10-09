using ColmiRingVRCBridge.Models;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge.Tests;

public sealed class BleConnectionLifetimeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly RingDeviceCandidate Candidate = new("R06 test", 1, -40);

    [Theory]
    [InlineData(0)] // Device open
    [InlineData(1)] // Service discovery
    [InlineData(2)] // Characteristic discovery
    [InlineData(3)] // Device-info reads
    [InlineData(4)] // CCCD enable
    [InlineData(5)] // Protocol-session startup
    public async Task ShutdownDuringManualConnect_AtEachStage_JoinsAndCannotPublishLateCompletion(int stage)
    {
        var connection = new FakeConnection(stage);
        await using var service = new ColmiRingBleService(() => connection, TimeSpan.FromSeconds(15));
        var coordinator = new ConnectionOperationCoordinator();
        var connected = 0;
        var heartRateEvents = 0;
        service.ConnectionChanged += value => { if (value) connected++; };
        service.HeartRateUpdated += _ => heartRateEvents++;
        var connect = coordinator.RunAsync(ConnectionOperation.ManualConnect, async token =>
        {
            await service.ConnectAsync(Candidate, token);
            token.ThrowIfCancellationRequested();
        });
        await connection.Entered.Task.WaitAsync(Timeout);
        await coordinator.ShutdownAsync().WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Timeout));
        await service.DisposeAsync().AsTask().WaitAsync(Timeout);
        Assert.Equal(ConnectionOperation.None, coordinator.CurrentOperation);
        Assert.Equal(0, connected);
        Assert.False(service.IsConnected);
        Assert.Equal(1, connection.DisposeCount);
        Assert.Equal(0, connection.OutstandingResources);
        Assert.Equal(0, connection.SessionStarts);

        // The simulated driver ignored cancellation. Complete it after shutdown.
        var late = new Resource();
        connection.Pending.SetResult(late);
        Assert.Equal(1, late.DisposeCount);
        Assert.Equal(stage + 1, connection.Stages.Count);
        connection.EmitHeartRate();
        Assert.Equal(0, heartRateEvents);
        Assert.Equal(0, connection.SubscriptionCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task DeadlineDuringEachStage_RecoversForRetry_AndLateResultCannotReplaceNewSession(int stage)
    {
        var first = new FakeConnection(stage);
        var second = new FakeConnection();
        var attempts = 0;
        await using var service = new ColmiRingBleService(() => ++attempts == 1 ? first : second,
            TimeSpan.FromMilliseconds(75));
        var coordinator = new ConnectionOperationCoordinator();
        var attempt = coordinator.RunAsync(ConnectionOperation.ManualConnect,
            async token => { await service.ConnectAsync(Candidate, token); });
        await first.Entered.Task.WaitAsync(Timeout);
        await Assert.ThrowsAsync<TimeoutException>(() => attempt.WaitAsync(Timeout));
        Assert.Equal(ConnectionOperation.None, coordinator.CurrentOperation);
        Assert.False(service.IsConnected);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, first.OutstandingResources);
        await coordinator.RunAsync(ConnectionOperation.ManualConnect,
            async token => { await service.ConnectAsync(Candidate, token); });
        Assert.True(service.IsConnected);
        Assert.Equal(1, second.SessionStarts);
        var late = new Resource();
        first.Pending.SetResult(late);
        Assert.Equal(1, late.DisposeCount);
        Assert.Equal(0, first.SessionStarts);
        first.EmitConnectionChanged(false);
        Assert.True(service.IsConnected);
        Assert.Equal(0, first.SubscriptionCount);
        await service.DisconnectAsync();
        await service.DisconnectAsync();
        Assert.Equal(1, second.DisposeCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task DriverFailureAtEachStage_DisposesAttempt_AndAllowsRetry(int stage)
    {
        var first = new FakeConnection(stage);
        var second = new FakeConnection();
        var attempts = 0;
        await using var service = new ColmiRingBleService(() => ++attempts == 1 ? first : second,
            TimeSpan.FromSeconds(15));
        var coordinator = new ConnectionOperationCoordinator();
        var connecting = coordinator.RunAsync(ConnectionOperation.ManualConnect,
            async token => { await service.ConnectAsync(Candidate, token); });
        await first.Entered.Task.WaitAsync(Timeout);
        first.Pending.SetException(new InvalidOperationException("Injected driver failure."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => connecting.WaitAsync(Timeout));
        Assert.Equal(ConnectionOperation.None, coordinator.CurrentOperation);
        Assert.False(service.IsConnected);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(0, first.OutstandingResources);
        Assert.Equal(0, first.SubscriptionCount);
        Assert.Equal(0, first.SessionStarts);
        await coordinator.RunAsync(ConnectionOperation.ManualConnect,
            async token => { await service.ConnectAsync(Candidate, token); });
        Assert.True(service.IsConnected);
        await service.DisconnectAsync();
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task ImmediateManualConnectShutdown_Repeated100Times_NoCrashStaleSessionOrResources()
    {
        for (var run = 0; run < 100; run++)
        {
            var connection = new FakeConnection(run % 6);
            await using var service = new ColmiRingBleService(() => connection, TimeSpan.FromSeconds(15));
            var coordinator = new ConnectionOperationCoordinator();
            var published = 0;
            service.ConnectionChanged += value => { if (value) published++; };
            var connect = coordinator.RunAsync(ConnectionOperation.ManualConnect,
                async token => { await service.ConnectAsync(Candidate, token); });
            await coordinator.ShutdownAsync().WaitAsync(Timeout);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Timeout));
            await service.DisposeAsync();
            Assert.True(connect.IsCompleted);
            Assert.Equal(0, published);
            Assert.Equal(0, connection.SessionStarts);
            Assert.Equal(1, connection.DisposeCount);
            Assert.Equal(0, connection.OutstandingResources);
            var late = new Resource();
            connection.Pending.SetResult(late);
            Assert.Equal(1, late.DisposeCount);
            Assert.Equal(0, connection.SubscriptionCount);
        }
    }

    [Fact]
    public async Task ServiceDisposeDuringConnect_CancelsJoins_AndRepeatedDisposeSharesCleanup()
    {
        var connection = new FakeConnection(0);
        var service = new ColmiRingBleService(() => connection, TimeSpan.FromSeconds(15));
        var connect = service.ConnectAsync(Candidate);
        await connection.Entered.Task.WaitAsync(Timeout);
        await Task.WhenAll(service.DisposeAsync().AsTask(), service.DisposeAsync().AsTask()).WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Timeout));
        Assert.Equal(1, connection.DisposeCount);
        var late = new Resource();
        connection.Pending.SetResult(late);
        Assert.Equal(1, late.DisposeCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ConnectAsync(Candidate));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationVersusSuccess_HasOneOwner_AndNoSessionAfterCancelledAttempt(bool cancelFirst)
    {
        var connection = new FakeConnection(5);
        await using var service = new ColmiRingBleService(() => connection, TimeSpan.FromSeconds(15));
        using var cts = new CancellationTokenSource();
        var connect = service.ConnectAsync(Candidate, cts.Token);
        await connection.Entered.Task.WaitAsync(Timeout);
        var resource = new Resource();
        if (cancelFirst)
        {
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect.WaitAsync(Timeout));
            connection.Pending.SetResult(resource);
            Assert.False(service.IsConnected);
            Assert.Equal(0, connection.SessionStarts);
        }
        else
        {
            connection.Pending.SetResult(resource);
            await connect.WaitAsync(Timeout);
            cts.Cancel(); // Attempt cancellation after publication must not stop telemetry lifetime.
            Assert.True(service.IsConnected);
            Assert.Equal(1, connection.SessionStarts);
            await service.DisconnectAsync();
        }
        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(1, connection.DisposeCount);
    }

    [Fact]
    public async Task RebootDuringShutdown_IsCancelled_ThenLocalConnectionIsDisposed()
    {
        var connection = new FakeConnection(6);
        await using var service = new ColmiRingBleService(() => connection, TimeSpan.FromSeconds(15));
        await service.ConnectAsync(Candidate);
        var coordinator = new ConnectionOperationCoordinator();
        var reboot = coordinator.RunAsync(ConnectionOperation.ManualReboot, async token =>
        {
            try { await service.RebootAsync(token); }
            finally { await service.DisconnectAsync(); }
        });
        await connection.Entered.Task.WaitAsync(Timeout);
        await coordinator.ShutdownAsync().WaitAsync(Timeout);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reboot.WaitAsync(Timeout));
        Assert.False(service.IsConnected);
        Assert.Equal(1, connection.DisposeCount);
        var late = new Resource();
        connection.Pending.SetResult(late);
        Assert.Equal(1, late.DisposeCount);
    }

    [Fact]
    public async Task CleanupFailure_ClearsOwnership_Unsubscribes_AndAllowsAnotherAttempt()
    {
        var first = new FakeConnection { FailDispose = true };
        var second = new FakeConnection();
        var attempts = 0;
        await using var service = new ColmiRingBleService(() => ++attempts == 1 ? first : second,
            TimeSpan.FromSeconds(15));
        var coordinator = new ConnectionOperationCoordinator();
        await service.ConnectAsync(Candidate);
        var disconnect = coordinator.RunAsync(ConnectionOperation.ManualDisconnect, _ => service.DisconnectAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => disconnect);
        Assert.False(service.IsConnected);
        Assert.Equal(ConnectionOperation.None, coordinator.CurrentOperation);
        Assert.Equal(0, first.SubscriptionCount);
        await service.ConnectAsync(Candidate);
        Assert.True(service.IsConnected);
        await service.DisconnectAsync();
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task CancelledNativeTaskThatLaterFaults_IsObservedWithoutApplicationCallback()
    {
        var source = new TaskCompletionSource<Resource>();
        var pendingBefore = BoundedBleOperation.PendingCompletions;
        using var cts = new CancellationTokenSource();
        var waiting = BoundedBleOperation.AwaitAsync(source.Task, cts.Token, resource => resource.Dispose());
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(pendingBefore + 1, BoundedBleOperation.PendingCompletions);
        source.SetException(new InvalidOperationException("Late driver failure."));
        Assert.True(source.Task.IsFaulted);
        Assert.Equal(pendingBefore, BoundedBleOperation.PendingCompletions);
        // ObserveLate consumes the exception synchronously on completion; no new app task is launched.
    }

    private sealed class Resource : IDisposable
    {
        public int DisposeCount;
        public void Dispose() => Assert.Equal(1, Interlocked.Increment(ref DisposeCount));
    }

    private sealed class FakeConnection(int holdStage = -1) : IBleConnection
    {
        public event Action<IBleConnection, int>? HeartRateUpdated;
        public event Action<IBleConnection, BatteryState>? BatteryUpdated;
        public event Action<IBleConnection, bool>? ConnectionChanged;
        public event Action<IBleConnection, string>? ProtocolWarning;
        public event Action<IBleConnection, HeartRateProtocolProbePacket>? ProbeObserved;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Synchronous completion makes late-result cleanup assertions deterministic.
        public readonly TaskCompletionSource<Resource> Pending = new();
        public readonly List<int> Stages = new();
        private readonly List<Resource> _resources = new();
        public int SessionStarts;
        public int DisposeCount;
        public bool FailDispose;
        public bool IsConnected => SessionStarts == 1 && DisposeCount == 0;
        public int OutstandingResources => _resources.Count(resource => resource.DisposeCount == 0);
        public int SubscriptionCount => (HeartRateUpdated?.GetInvocationList().Length ?? 0)
            + (BatteryUpdated?.GetInvocationList().Length ?? 0) + (ConnectionChanged?.GetInvocationList().Length ?? 0)
            + (ProtocolWarning?.GetInvocationList().Length ?? 0) + (ProbeObserved?.GetInvocationList().Length ?? 0);
        public BleTelemetryDiagnostics GetDiagnostics() => BleTelemetryDiagnostics.Empty;
        public HeartRateTelemetryState GetTelemetryState(DateTimeOffset now, TimeSpan staleAfter) => HeartRateTelemetryState.Streaming;
        private async Task Stage(int stage, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Stages.Add(stage);
            if (stage == holdStage)
            {
                Entered.TrySetResult();
                var resource = await BoundedBleOperation.AwaitAsync(Pending.Task, token, r => r.Dispose());
                _resources.Add(resource);
            }
            else _resources.Add(new Resource());
            token.ThrowIfCancellationRequested();
        }
        public Task OpenAsync(ulong address, CancellationToken token) => Stage(0, token);
        public Task DiscoverServiceAsync(CancellationToken token) => Stage(1, token);
        public Task DiscoverCharacteristicsAsync(CancellationToken token) => Stage(2, token);
        public async Task<RingDeviceInfo> ReadDeviceInfoAsync(CancellationToken token)
        {
            await Stage(3, token);
            return new RingDeviceInfo("R06", "test", "1", "1");
        }
        public Task EnableNotificationsAsync(CancellationToken token) => Stage(4, token);
        public async Task StartSessionAsync(CancellationToken token)
        {
            await Stage(5, token);
            SessionStarts++;
        }
        public Task RebootAsync(CancellationToken token) => Stage(6, token);
        public void EmitHeartRate() => HeartRateUpdated?.Invoke(this, 72);
        public void EmitConnectionChanged(bool connected) => ConnectionChanged?.Invoke(this, connected);
        public ValueTask DisposeAsync()
        {
            Assert.Equal(1, Interlocked.Increment(ref DisposeCount));
            foreach (var resource in _resources) resource.Dispose();
            return FailDispose ? new ValueTask(Task.FromException(new InvalidOperationException("Injected cleanup failure.")))
                : ValueTask.CompletedTask;
        }
    }
}
