using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

// Research only. No reference from the production solution or release workflow.
var options = Options.Parse(args);
if (options.Mode == "self-test") { Presets.SelfTest(); return; }
using var settings = JsonDocument.Parse(File.ReadAllText(options.Settings));
if (settings.RootElement.GetProperty("DeviceName").GetString()?.StartsWith("R06_", StringComparison.OrdinalIgnoreCase) != true)
    throw new InvalidOperationException("Research target must be the existing R06 device; other models are rejected.");
var address = settings.RootElement.GetProperty("BluetoothAddress").GetUInt64();
using var capture = new Capture(options.Output, options.Label);
capture.Event("manual_intervention", note: options.Intervention);
await using var probe = new Probe(address, capture);
await probe.ScanAsync(10);
if (options.Mode == "passive") { await probe.ObserveAsync(options.Duration); return; }
for (var run = 1; run <= options.Runs; run++)
{
    capture.Event("trial_start", note: $"run={run}; sequence={options.Sequence}; delay={options.Delay}; stop={options.Stop}");
    await probe.ConnectAsync();
    await probe.StartAsync(options);
    await probe.StreamAsync(options.Duration);
    capture.Summary($"run {run}");
    if (options.Mode == "observe08")
    {
        if (!probe.IsConnected || !capture.Streaming || !capture.BatterySeen || probe.AdvertisementCount == 0)
            throw new InvalidOperationException("0x08 refused: require connected, sustained HR, battery and pre-command advertisement evidence.");
        await probe.SendCommand08Async(options.Enable08);
        // No HR/battery/stop/CCCD writes, no disconnect initiated by us during observation.
        // Reconnect only if a fresh advertisement follows a native disconnect.
        await probe.ObserveCommandAsync(180, options);
        capture.Summary("post-command");
        await probe.CloseAsync(sendStop: false);
    }
    else { await probe.CloseAsync(sendStop: true); }
    capture.Event("trial_end", note: $"run={run}");
    if (run < options.Runs) await Task.Delay(3000);
}

sealed record Options(string Mode, string Sequence, string Label, string Intervention, string Output,
    string Settings, int Duration, int Runs, int Delay, string Stop, bool Enable08)
{
    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--enable-command08") { values.Add(args[i], "true"); continue; }
            if (!args[i].StartsWith("--") || i + 1 == args.Length) throw new ArgumentException("Use named, allowlisted options.");
            values.Add(args[i], args[++i]);
        }
        string Read(string name, string fallback) => values.GetValueOrDefault(name, fallback);
        string Choice(string name, string fallback, params string[] allowed)
        {
            var v = Read(name, fallback);
            return allowed.Contains(v) ? v : throw new ArgumentException($"Invalid {name}");
        }
        var known = new[] { "--mode", "--sequence", "--label", "--intervention", "--output", "--settings", "--duration", "--runs", "--delay", "--stop", "--enable-command08" };
        if (values.Keys.Except(known).Any()) throw new ArgumentException("Unknown option; arbitrary packet input is prohibited.");
        var mode = Choice("--mode", "capture", "capture", "observe08", "passive", "self-test");
        var sequence = Choice("--sequence", "S0", "S0", "S1", "S2", "S3", "S4");
        var label = Choice("--label", "worn", "worn", "unworn", "just-worn", "post-wake", "baseline-A", "baseline-B", "baseline-C", "post-command");
        var intervention = Choice("--intervention", "none", "none", "user-awake-worn", "remove", "wear", "charger-attach", "charger-detach", "attach-detach", "motion", "app-restart");
        var duration = int.Parse(Read("--duration", "45"));
        var runs = int.Parse(Read("--runs", "1"));
        var delay = int.Parse(Read("--delay", "500"));
        if (duration is < 10 or > 180 || runs is < 1 or > 10 || !new[] { 0, 250, 500, 1000, 2000 }.Contains(delay)) throw new ArgumentOutOfRangeException("Timing/run bounds");
        var enabled = Read("--enable-command08", "false") == "true";
        if (mode == "observe08" && (!enabled || runs != 1)) throw new ArgumentException("observe08 requires explicit --enable-command08 and exactly one trial.");
        if (enabled && mode != "observe08") throw new ArgumentException("0x08 is permitted only in observe08.");
        var output = Path.GetFullPath(Read("--output", "artifacts/issue8/captures/" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + ".jsonl"));
        // All raw captures must stay in ignored artifacts, never alongside publishable research docs.
        if (!output.Replace('\\', '/').Contains("/artifacts/issue8/")) throw new ArgumentException("Raw output must be inside artifacts/issue8.");
        return new(mode, sequence, label, intervention, output,
            Read("--settings", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ColmiRingVRCBridge", "connection.json")), duration, runs, delay,
            Choice("--stop", "6A", "6A", "action4"), enabled);
    }
}

static class Presets
{
    public static byte[] Packet(byte command, params byte[] payload)
    {
        var p = new byte[16]; p[0] = command; payload.CopyTo(p, 1);
        p[15] = (byte)p.Take(15).Sum(x => (int)x); return p;
    }
    public static byte Type(Options o) => o.Sequence == "S2" ? (byte)1 : (byte)6;
    public static byte[] Stop(byte type, string method) => method == "6A" ? Packet(0x6A, type, 0, 0) : Packet(0x69, type, 4);
    // Deny by default: reset (0xFF), all settings/time writes, raw sensor controls,
    // firmware/OTA interfaces and every non-allowlisted command are inaccessible.
    public static void Validate(byte[] packet, bool command08)
    {
        var ordinary = new[] { Packet(3), Packet(0x1E, 0x33), Packet(0x69, 6, 1), Packet(0x69, 6, 3),
            Packet(0x69, 1, 1), Packet(0x69, 1, 3), Stop(6, "6A"), Stop(1, "6A"), Stop(6, "action4"), Stop(1, "action4") };
        if (ordinary.Any(p => p.SequenceEqual(packet))) return;
        if (command08 && Packet(8, 1).SequenceEqual(packet)) return;
        throw new InvalidOperationException("Packet not allowed by research preset policy.");
    }
    public static void SelfTest()
    {
        static void Denied(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            throw new Exception("Expected policy rejection.");
        }
        foreach (var command in Enumerable.Range(0, 256))
        {
            var p = Packet((byte)command);
            var allowed = true; try { Validate(p, false); } catch (InvalidOperationException) { allowed = false; }
            if (allowed != (command == 3)) throw new Exception("Deny-default policy failed.");
        }
        try { Validate(Packet(8, 1), false); throw new Exception("0x08 gate failed."); } catch (InvalidOperationException) { }
        Validate(Packet(8, 1), true);
        Denied(() => Validate(Packet(0xFF, 0x66, 0x66), true));
        Denied(() => Validate(Packet(8, 2), true));
        Denied(() => Validate(Packet(3, 1), false));
        Denied(() => Options.Parse(["--mode", "observe08"]));
        Denied(() => Options.Parse(["--mode", "capture", "--enable-command08"]));
        Denied(() => Options.Parse(["--mode", "observe08", "--runs", "2", "--enable-command08"]));
        Denied(() => Options.Parse(["--packet", "FF6666"]));
        if (Packet(0x69, 6, 1)[15] != 0x70 || Packet(0x69, 6, 3)[15] != 0x72 || Packet(0x1E, 0x33)[15] != 0x51 || Stop(6, "6A")[15] != 0x70) throw new Exception("Checksum failed.");
        Console.WriteLine("Probe self-test PASS: default deny, 0x08 opt-in, checksums.");
    }
}

sealed class Capture : IDisposable
{
    readonly object gate = new(); readonly StreamWriter writer; readonly Stopwatch clock = Stopwatch.StartNew(); readonly string label;
    public string Connection = "disconnected", Measurement = "idle";
    int hrCount; long firstHr = -1, lastHr = -1; bool batterySeen, closed;
    readonly ConcurrentDictionary<byte, int> statuses = new();
    public Capture(string path, string label)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        this.label = label; Event("capture_start", note: "Local private raw capture; label=" + label);
        Console.WriteLine("Capture: " + path);
    }
    public bool BatterySeen { get { lock (gate) return batterySeen; } }
    public bool Streaming { get { lock (gate) return hrCount >= 10 && lastHr - firstHr >= 10000 && clock.ElapsedMilliseconds - lastHr < 5000; } }
    public void Reset()
    {
        lock (gate) { hrCount = 0; firstHr = lastHr = -1; batterySeen = false; statuses.Clear(); }
    }
    public void Event(string evt, string direction = "", byte[]? packet = null, string note = "")
    {
        lock (gate)
        {
            if (closed) return;
            writer.WriteLine(JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, relative_ms = clock.Elapsed.TotalMilliseconds,
                @event = evt, direction, packet_hex = packet is null ? "" : string.Join(" ", packet.Select(b => b.ToString("X2"))),
                command = packet?.Length > 0 ? $"0x{packet[0]:X2}" : "", connection_state = Connection, measurement_state = Measurement, label, note }));
        }
    }
    public void Rx(byte[] p)
    {
        lock (gate)
        {
            Event("RX", "RX", p);
            if (p.Length != 16 || (byte)p.Take(15).Sum(x => (int)x) != p[15]) return;
            if (p[0] == 3) batterySeen = true;
            if (p[0] == 0x69) statuses.AddOrUpdate(p[2], 1, (_, n) => n + 1);
            var hr = p[0] == 0x1E ? p[1] : p[0] == 0x69 && p[2] == 0 ? p[3] : 0;
            if (hr is >= 30 and <= 220)
            {
                if (firstHr < 0) firstHr = clock.ElapsedMilliseconds;
                lastHr = clock.ElapsedMilliseconds; hrCount++; Measurement = "HR-observed";
            }
        }
    }
    public void Summary(string stage)
    {
        lock (gate)
        {
            var summary = $"{stage}; samples={hrCount}; HR_span_ms={lastHr-firstHr}; sustained={Streaming}; battery={batterySeen}; status_counts="
                + string.Join(",", statuses.OrderBy(x => x.Key).Select(x => $"0x{x.Key:X2}:{x.Value}"));
            Event("summary", note: summary); Console.WriteLine(summary);
        }
    }
    public void Dispose() { lock (gate) { closed = true; writer.Dispose(); } }
}

sealed class Probe(ulong address, Capture log) : IAsyncDisposable
{
    static readonly Guid Uart = Guid.Parse("6e40fff0-b5a3-f393-e0a9-e50e24dcca9e"), Write = Guid.Parse("6e400002-b5a3-f393-e0a9-e50e24dcca9e"), Notify = Guid.Parse("6e400003-b5a3-f393-e0a9-e50e24dcca9e");
    readonly BluetoothLEAdvertisementWatcher watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
    BluetoothLEDevice? device; GattDeviceService? service; GattCharacteristic? rx, tx;
    int ads; long lastAdTicks; bool quiet, notified; Options? current;
    public int AdvertisementCount => Volatile.Read(ref ads);
    public bool IsConnected => device?.ConnectionStatus == BluetoothConnectionStatus.Connected;
    public async Task ScanAsync(int seconds)
    {
        watcher.Received += Advertisement;
        watcher.Start(); log.Event("scan_start"); await Task.Delay(seconds * 1000); log.Event("scan_complete", note: $"target_advertisements={AdvertisementCount}");
    }
    void Advertisement(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (args.BluetoothAddress != address) return;
        Interlocked.Increment(ref ads); Interlocked.Exchange(ref lastAdTicks, Stopwatch.GetTimestamp());
        log.Event("advertisement_seen", note: $"RSSI={args.RawSignalStrengthInDBm}; native_timestamp={args.Timestamp:O}; kind={args.AdvertisementType}");
    }
    void ConnectionChanged(BluetoothLEDevice sender, object args)
    {
        log.Connection = sender.ConnectionStatus.ToString();
        log.Event(sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected ? "native_disconnect" : "native_connected");
    }
    static async Task<T> Bound<T>(Task<T> task, CancellationToken token, Action<T>? cleanup = null)
    {
        try { return await task.WaitAsync(token); }
        catch (OperationCanceledException)
        {
            _ = task.ContinueWith(t => { try { if (t.IsCompletedSuccessfully) cleanup?.Invoke(t.Result); else _ = t.Exception; } catch (Exception ex) { Debug.WriteLine(ex); } }, TaskScheduler.Default);
            throw;
        }
    }
    public async Task ConnectAsync()
    {
        log.Reset(); quiet = false; log.Connection = "connecting"; log.Event("connect_start");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var token = deadline.Token;
        try
        {
            device = await Bound(BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(token), token, d => d?.Dispose()) ?? throw new Exception("Device open failed.");
            device.ConnectionStatusChanged += ConnectionChanged;
            var services = await Bound(device.GetGattServicesForUuidAsync(Uart, BluetoothCacheMode.Uncached).AsTask(token), token, r => { foreach (var s in r.Services) s.Dispose(); });
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) { foreach(var s in services.Services) s.Dispose(); throw new Exception("UART discovery failed."); }
            service = services.Services[0]; foreach(var s in services.Services.Skip(1)) s.Dispose();
            var writes = await Bound(service.GetCharacteristicsForUuidAsync(Write, BluetoothCacheMode.Uncached).AsTask(token), token);
            var notifications = await Bound(service.GetCharacteristicsForUuidAsync(Notify, BluetoothCacheMode.Uncached).AsTask(token), token);
            if (writes.Status != GattCommunicationStatus.Success || notifications.Status != GattCommunicationStatus.Success || writes.Characteristics.Count == 0 || notifications.Characteristics.Count == 0) throw new Exception("Characteristics missing.");
            rx = writes.Characteristics[0]; tx = notifications.Characteristics[0]; tx.ValueChanged += Notification;
            log.Connection = "connected"; log.Event("connected");
            var result = await Bound(tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token), token);
            if (result != GattCommunicationStatus.Success) throw new Exception("Notify enable failed.");
            notified = true; log.Event("notify_enabled");
        }
        catch { await CloseAsync(false); throw; }
    }
    void Notification(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        using var reader = DataReader.FromBuffer(args.CharacteristicValue);
        var p = new byte[reader.UnconsumedBufferLength]; reader.ReadBytes(p); log.Rx(p);
    }
    async Task SendAsync(byte[] p, bool command08 = false)
    {
        Presets.Validate(p, command08);
        if (quiet && !command08) throw new InvalidOperationException("Quiet post-command phase prohibits protocol writes.");
        if (rx is null) throw new InvalidOperationException("No transport.");
        log.Event("TX", "TX", p);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3)); var token = deadline.Token;
        using var writer = new DataWriter(); writer.WriteBytes(p);
        var status = await Bound(rx.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse).AsTask(token), token);
        log.Event("write_complete", note: status.ToString());
        if (status != GattCommunicationStatus.Success) throw new Exception("GATT write failed: " + status);
    }
    public async Task StartAsync(Options o)
    {
        current = o; var type = Presets.Type(o); log.Measurement = "starting";
        if (o.Sequence == "S4") await SendAsync(Presets.Stop(type, o.Stop));
        await SendAsync(Presets.Packet(0x69, type, 1)); await Task.Delay(o.Delay);
        // S0/S3 are identical current action-continue. S1 removes only Continue.
        // S2 differs from S1 only in type (1). These packet builders are defined
        // by tahnok; its current client consumes 69 notifications without polling.
        // S2 is the explicitly labelled Start + 1E/33 family variant, not a claim
        // to reproduce the complete external client's runtime behavior.
        if (o.Sequence is "S0" or "S3" or "S4") await SendAsync(Presets.Packet(0x69, type, 3));
        await SendAsync(Presets.Packet(3));
    }
    public async Task StreamAsync(int seconds)
    {
        var clock = Stopwatch.StartNew(); var nextBattery = 60000L;
        while (clock.Elapsed.TotalSeconds < seconds && IsConnected)
        {
            await SendAsync(Presets.Packet(0x1E, 0x33));
            if(clock.ElapsedMilliseconds >= nextBattery) { await SendAsync(Presets.Packet(3)); nextBattery += 60000; }
            await Task.Delay(1000);
        }
    }
    public async Task SendCommand08Async(bool enabled)
    {
        if (!enabled) throw new InvalidOperationException("0x08 opt-in missing.");
        quiet = true; log.Event("command08_phase_start", note: "No further protocol writes unless advertisement-driven reconnect succeeds.");
        await SendAsync(Presets.Packet(8, 1), true); Console.WriteLine("0x08/0x01 write completed; observing 180 seconds without manual intervention.");
    }
    public async Task ObserveAsync(int seconds) { await Task.Delay(seconds * 1000); log.Event("passive_observation_end", note: $"target_advertisements={AdvertisementCount}"); }
    public async Task ObserveCommandAsync(int seconds, Options o)
    {
        var clock = Stopwatch.StartNew(); var attempted = false; long disconnectTick = 0;
        Task? recoveryStream = null;
        foreach (var boundary in new[] { 10, 30, 60, seconds })
        {
            while (clock.Elapsed.TotalSeconds < boundary)
            {
                if (!IsConnected && disconnectTick == 0) disconnectTick = Stopwatch.GetTimestamp();
                if (!attempted && disconnectTick != 0 && Interlocked.Read(ref lastAdTicks) > disconnectTick)
                {
                    attempted = true; log.Event("reconnect", note: "Triggered by fresh target advertisement after native disconnect; no manual wake.");
                    await CloseAsync(false);
                    try { await ConnectAsync(); await StartAsync(o); recoveryStream = StreamAsync(Math.Max(1, seconds - (int)clock.Elapsed.TotalSeconds)); }
                    catch (Exception ex) { log.Event("reconnect_failed", note: ex.GetType().Name); }
                }
                await Task.Delay(250);
            }
            log.Event("observation_boundary", note: $"post_command_seconds={boundary}; connected={IsConnected}; target_ads_total={AdvertisementCount}");
            Console.WriteLine($"Observed through {boundary}s: connected={IsConnected}, target_ads={AdvertisementCount}");
        }
        if (recoveryStream is not null)
            try { await recoveryStream; } catch(Exception ex) { log.Event("recovery_stream_failed", note: ex.GetType().Name); }
    }
    public async Task CloseAsync(bool sendStop)
    {
        if (device is null) return;
        log.Event("local_cleanup_start", note: "Explicit local cleanup; not native-disconnect evidence.");
        if (sendStop && !quiet && current is not null && IsConnected)
            try { await SendAsync(Presets.Stop(Presets.Type(current), current.Stop)); } catch(Exception ex) { log.Event("stop_failed", note: ex.GetType().Name); }
        if(tx is not null)
        {
            tx.ValueChanged -= Notification;
            if(notified) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); try { await Bound(tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None).AsTask(timeout.Token), timeout.Token); } catch { } }
        }
        device.ConnectionStatusChanged -= ConnectionChanged; service?.Dispose(); device.Dispose();
        service = null; device = null; tx = rx = null; notified = false; log.Connection = "locally-disposed"; log.Measurement = "idle";
        log.Event("local_cleanup_complete");
    }
    public async ValueTask DisposeAsync()
    {
        watcher.Stop(); watcher.Received -= Advertisement; await CloseAsync(false);
    }
}
