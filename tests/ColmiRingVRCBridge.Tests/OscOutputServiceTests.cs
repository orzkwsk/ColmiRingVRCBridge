using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using ColmiRingVRCBridge.Models;
using ColmiRingVRCBridge.Services;

namespace ColmiRingVRCBridge.Tests;

public sealed class OscOutputServiceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly OscOutputOptions Options = new(
        "127.0.0.1", 9000, "HeartRate", OscValueType.Float,
        ScalingMode.Normalize255, TimeSpan.FromMinutes(1));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImmediateStop_Repeated100Times_JoinsWorkerAndDisposesEveryLifetime(bool useRealSender)
    {
        var created = 0;
        var disposed = 0;
        var errors = new List<Exception>();
        await using var service = useRealSender ? new OscOutputService() : new OscOutputService(
            (_, _) =>
            {
                Interlocked.Increment(ref created);
                return new TestSender(onDispose: () => Interlocked.Increment(ref disposed));
            }, Task.Run);
        service.OutputError += errors.Add;

        for (var run = 0; run < 100; run++)
        {
            // The real-sender case retains Issue #7's exact baseline conditions.
            var options = useRealSender
                ? Options with { ValueType = OscValueType.Int, Scaling = ScalingMode.RawBpm,
                    Interval = TimeSpan.FromSeconds(3) }
                : Options;
            service.Start(options, () => useRealSender ? null : 72);
            var cts = GetCts(service);
            var worker = GetWorker(service);
            await service.StopAsync().WaitAsync(Timeout);
            await service.StopAsync().WaitAsync(Timeout);
            AssertStopped(service, cts, worker);
        }

        Assert.Empty(errors);
        Assert.Equal(created, disposed);
    }

    [Fact]
    public async Task StopBeforeScheduledWorkerStarts_CancelsAndJoinsBeforeClearingState()
    {
        var release = Signal();
        var factoryCalls = 0;
        await using var service = new OscOutputService(
            (_, _) =>
            {
                Interlocked.Increment(ref factoryCalls);
                return new TestSender();
            }, work => Task.Run(async () =>
            {
                await release.Task;
                await work();
            }));
        service.Start(Options, () => 72);
        var cts = GetCts(service);
        var worker = GetWorker(service);
        var stop = service.StopAsync();
        var secondStop = service.StopAsync();
        try
        {
            Assert.True(cts.IsCancellationRequested);
            Assert.False(stop.IsCompleted);
            Assert.False(secondStop.IsCompleted);
            Assert.True(service.IsRunning);
            Assert.Throws<InvalidOperationException>(() => service.Start(Options, () => 73));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(stop, secondStop).WaitAsync(Timeout);
        }

        AssertStopped(service, cts, worker);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task StopDuringSenderConstruction_WaitsForLateResourceAndDisposesIt()
    {
        var entered = Signal();
        using var release = new ManualResetEventSlim();
        var sender = new TestSender();
        await using var service = new OscOutputService((_, _) =>
        {
            entered.TrySetResult();
            if (!release.Wait(Timeout)) throw new TimeoutException("Sender construction was not released.");
            return sender;
        }, Task.Run);
        service.Start(Options, () => 72);
        var cts = GetCts(service);
        var worker = GetWorker(service);
        Task? stop = null;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            stop = service.StopAsync();
            Assert.False(stop.IsCompleted);
            Assert.Equal(0, sender.DisposeCount);
            Assert.True(cts.IsCancellationRequested);
        }
        finally
        {
            release.Set();
            await (stop ?? service.StopAsync()).WaitAsync(Timeout);
        }

        AssertStopped(service, cts, worker);
        Assert.Equal(0, sender.SendCount);
        Assert.Equal(1, sender.DisposeCount);
    }

    [Fact]
    public async Task DoubleStopAndDisposeDuringSend_JoinSameCleanup_ThenRejectDisposedStart()
    {
        var entered = Signal();
        var release = Signal();
        var sender = new TestSender(() =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        await using var service = new OscOutputService((_, _) => sender, Task.Run);
        var outputs = 0;
        service.OutputSent += (_, _) => Interlocked.Increment(ref outputs);
        service.Start(Options, () => 72);
        var cts = GetCts(service);
        var worker = GetWorker(service);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            Assert.Throws<InvalidOperationException>(() => service.Start(Options, () => 73));
            var stop = service.StopAsync();
            var secondStop = service.StopAsync();
            var dispose = service.DisposeAsync().AsTask();
            Assert.False(stop.IsCompleted);
            Assert.False(secondStop.IsCompleted);
            Assert.False(dispose.IsCompleted);
            Assert.Equal(0, sender.DisposeCount);
            release.TrySetResult();
            await Task.WhenAll(stop, secondStop, dispose).WaitAsync(Timeout);
        }
        finally
        {
            release.TrySetResult();
            await service.StopAsync().WaitAsync(Timeout);
        }

        AssertStopped(service, cts, worker);
        Assert.Equal(1, sender.DisposeCount);
        Assert.Equal(1, outputs); // The in-flight callback completes before Stop returns.
        Assert.Throws<ObjectDisposedException>(() => service.Start(Options, () => 73));
    }

    [Fact]
    public async Task SchedulingFailure_DisposesCts_AndAllowsStop()
    {
        CancellationTokenSource? allocated = null;
        OscOutputService? service = null;
        service = new OscOutputService((_, _) => new TestSender(), _ =>
        {
            allocated = GetCts(service!);
            throw new TaskSchedulerException("Injected scheduling failure.");
        });
        await using var ownedService = service;

        Assert.Throws<TaskSchedulerException>(() => service.Start(Options, () => 72));
        Assert.NotNull(allocated);
        Assert.Throws<ObjectDisposedException>(() => allocated.Token);
        await service.StopAsync().WaitAsync(Timeout);
        Assert.False(service.IsRunning);
        Assert.Null(ReadField(service, "_loopTask"));
    }

    [Fact]
    public async Task SenderCreationFailure_ReportsError_CleansUp_AndCanRestart()
    {
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = Signal();
        var fail = true;
        var sender = new TestSender(() =>
        {
            sent.TrySetResult();
            return Task.CompletedTask;
        });
        var expected = new SocketException((int)SocketError.HostNotFound);
        await using var service = new OscOutputService((_, _) =>
        {
            if (fail) throw expected;
            return sender;
        }, Task.Run);
        service.OutputError += ex => error.TrySetResult(ex);
        service.Start(Options, () => 72);
        var cts = GetCts(service);
        var worker = GetWorker(service);
        Assert.Same(expected, await error.Task.WaitAsync(Timeout));
        Assert.True(service.IsRunning); // Failed worker still belongs to this lifetime until Stop.
        await service.StopAsync().WaitAsync(Timeout);
        AssertStopped(service, cts, worker);

        fail = false;
        service.Start(Options, () => 73);
        await sent.Task.WaitAsync(Timeout);
        await service.StopAsync().WaitAsync(Timeout);
        Assert.Equal(1, sender.DisposeCount);
    }

    [Fact]
    public async Task FaultedErrorCallback_IsObservedByBothStops_AndCleanupStillCompletes()
    {
        var entered = Signal();
        var release = Signal();
        var sender = new TestSender(async () =>
        {
            entered.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("Injected send failure.");
        });
        var callbackFailure = new ApplicationException("Injected error callback failure.");
        await using var service = new OscOutputService((_, _) => sender, Task.Run);
        service.OutputError += _ => throw callbackFailure;
        service.Start(Options, () => 72);
        var cts = GetCts(service);
        var worker = GetWorker(service);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            var stop = service.StopAsync();
            var secondStop = service.StopAsync();
            release.TrySetResult();
            Assert.Same(callbackFailure, await Assert.ThrowsAsync<ApplicationException>(
                () => stop.WaitAsync(Timeout)));
            Assert.Same(callbackFailure, await Assert.ThrowsAsync<ApplicationException>(
                () => secondStop.WaitAsync(Timeout)));
        }
        finally
        {
            release.TrySetResult();
            try { await service.StopAsync().WaitAsync(Timeout); }
            catch (ApplicationException) { }
        }

        AssertStopped(service, cts, worker);
        Assert.True(worker.IsFaulted);
        Assert.Equal(1, sender.DisposeCount);
        await service.StopAsync().WaitAsync(Timeout);
    }

    [Theory]
    [InlineData(OscValueType.Float, ScalingMode.Normalize255)]
    [InlineData(OscValueType.Float, ScalingMode.RawBpm)]
    [InlineData(OscValueType.Int, ScalingMode.RawBpm)]
    public async Task NormalStartStopAndRestart_PreservesDestinationPathAndWireValues(
        OscValueType type, ScalingMode scaling)
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        var options = Options with { Port = port, ValueType = type, Scaling = scaling };
        await using var service = new OscOutputService();
        await service.StopAsync(); // Already Stopped.
        foreach (var bpm in new[] { 72, 73 })
        {
            service.Start(options, () => bpm);
            var cts = GetCts(service);
            var worker = GetWorker(service);
            UdpReceiveResult packet;
            try
            {
                packet = await receiver.ReceiveAsync().WaitAsync(Timeout);
            }
            finally
            {
                await service.StopAsync().WaitAsync(Timeout);
            }

            AssertStopped(service, cts, worker);
            var offset = 0;
            Assert.Equal("/avatar/parameters/HeartRate", ReadOscString(packet.Buffer, ref offset));
            Assert.Equal(type == OscValueType.Int ? ",i" : ",f", ReadOscString(packet.Buffer, ref offset));
            Assert.Equal(offset + 4, packet.Buffer.Length);
            var expected = type == OscValueType.Int ? bpm : BitConverter.SingleToInt32Bits(
                scaling == ScalingMode.Normalize255 ? bpm / 255.0f : bpm);
            Assert.Equal(expected, BinaryPrimitives.ReadInt32BigEndian(packet.Buffer.AsSpan(offset)));
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static object? ReadField(OscOutputService service, string name) =>
        typeof(OscOutputService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);
    private static CancellationTokenSource GetCts(OscOutputService service) =>
        Assert.IsType<CancellationTokenSource>(ReadField(service, "_cts"));
    private static Task GetWorker(OscOutputService service) =>
        Assert.IsAssignableFrom<Task>(ReadField(service, "_loopTask"));

    private static void AssertStopped(OscOutputService service, CancellationTokenSource cts, Task worker)
    {
        Assert.False(service.IsRunning);
        Assert.True(worker.IsCompleted);
        Assert.Null(ReadField(service, "_cts"));
        Assert.Null(ReadField(service, "_loopTask"));
        Assert.Null(ReadField(service, "_stopTask"));
        Assert.Throws<ObjectDisposedException>(() => cts.Token);
    }

    private static string ReadOscString(byte[] bytes, ref int offset)
    {
        var end = Array.IndexOf(bytes, (byte)0, offset);
        Assert.InRange(end, offset, bytes.Length - 1);
        var value = Encoding.UTF8.GetString(bytes, offset, end - offset);
        var next = ((end + 1 + 3) / 4) * 4;
        Assert.All(bytes[(end + 1)..next], b => Assert.Equal((byte)0, b));
        offset = next;
        return value;
    }

    private sealed class TestSender(Func<Task>? send = null, Action? onDispose = null) : IOscSender
    {
        public int DisposeCount;
        public int SendCount;
        public Task SendFloatAsync(string address, float value) => Send();
        public Task SendIntAsync(string address, int value) => Send();
        private Task Send()
        {
            Assert.Equal(0, Volatile.Read(ref DisposeCount));
            Interlocked.Increment(ref SendCount);
            return send?.Invoke() ?? Task.CompletedTask;
        }
        public void Dispose()
        {
            Assert.Equal(1, Interlocked.Increment(ref DisposeCount));
            onDispose?.Invoke();
        }
    }
}
