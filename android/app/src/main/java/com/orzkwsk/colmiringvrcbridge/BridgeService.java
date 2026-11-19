package com.orzkwsk.colmiringvrcbridge;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothManager;
import android.bluetooth.le.BluetoothLeScanner;
import android.bluetooth.le.ScanCallback;
import android.bluetooth.le.ScanResult;
import android.bluetooth.le.ScanSettings;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.os.Binder;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.ParcelUuid;
import android.os.SystemClock;

import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.UUID;
import java.util.regex.Pattern;

@SuppressLint("MissingPermission") // Operations also handle revocation after the permission check.
public final class BridgeService extends Service implements RingConnection.Listener {
    static final String SCAN = "scan";
    static final String CONNECT = "connect";
    static final String RESUME = "resume";
    static final String RECONNECT_ON = "reconnect-on";
    static final String OUTPUT = "output";
    private static final String CHANNEL = "bridge";
    private static final ParcelUuid UART_ADVERTISEMENT = new ParcelUuid(RingProtocol.UART);
    static final long HEART_FRESH_MS = 5_000;
    private static final Pattern RING_NAME = Pattern.compile("R(02|03|06|09|10|12).*");

    enum Link { IDLE, SCANNING, CONNECTING, RECONNECTING, CONNECTED, DISCONNECTING }
    enum Output { IDLE, SENDING, STOPPING }

    record Candidate(String address, String name, int rssi, boolean likelyRing) {
        String label() {
            String signal = rssi == Integer.MIN_VALUE ? "" : "  ·  " + rssi + " dBm";
            return name + "  ·  " + address + signal;
        }
    }

    record Snapshot(Link link, Output output, boolean autoReconnect,
                    String ringStatus, String oscError, String lastOutput, List<Candidate> candidates,
                    Candidate ring, String deviceInfo, int bpm, long heartAt, long heartWallTime,
                    int min, int max, int battery, boolean charging, long batteryAt,
                    List<TelemetryHistory.Point> heartHistory, List<TelemetryHistory.Point> batteryHistory,
                    String diagnostics) {}

    final class LocalBinder extends Binder {
        Snapshot snapshot() { return snapshot; }
        void disconnect() { worker.post(() -> disconnectRing()); }
        void stopOutput() { worker.post(() -> stopOsc()); }
        void resetHeartHistory() { worker.post(() -> { clearHeartHistory(); publish(); }); }
        void setAutoReconnect(boolean value) { worker.post(() -> changeAutoReconnect(value)); }
        void updateValues() { worker.post(() -> outputValues = BridgeSettings.load(prefs).values()); }
    }

    private final LocalBinder binder = new LocalBinder();
    private final Handler main = new Handler(android.os.Looper.getMainLooper());
    private final OscOutput osc = new OscOutput();
    private final java.text.DateFormat outputClock = new java.text.SimpleDateFormat("HH:mm:ss", Locale.ROOT);
    private final Map<String, Candidate> discoveries = new HashMap<>();
    private final TelemetryHistory heartHistory = new TelemetryHistory(false);
    private final Map<String, TelemetryHistory> batteryHistories = new HashMap<>();
    private final Runnable retry = () -> connect(this.remembered, true);
    private final Runnable endScan = () -> finishScan(null);

    private volatile Snapshot snapshot;
    private volatile BridgeSettings.Values outputValues;
    private int bpm;
    private long heartAt;
    private HandlerThread thread;
    private Handler worker;
    private SharedPreferences prefs;
    private BluetoothAdapter adapter;

    // Notification lifecycle runs on the main thread. BLE and bridge state belong to worker.
    private int mainStartId;
    private int workerStartId;
    private boolean foreground;
    private boolean destroyed;
    private boolean autoReconnect;
    private boolean paused;
    private boolean resumed;
    private Runnable afterReconnectCancellation;
    private Link link = Link.IDLE;
    private Output output = Output.IDLE;
    private String ringStatus = "未接続";
    private String oscError;
    private String lastOutput = "";
    private String model = "—";
    private String serial = "—";
    private String hardware = "—";
    private String firmware = "—";
    private String warning = "";
    private String historyAddress;
    private List<Candidate> candidates = Collections.emptyList();
    private Candidate remembered;
    private Candidate selected;
    private RingConnection connection;
    private BluetoothLeScanner scanner;
    private ScanCallback scanCallback;
    private TelemetryHistory batteryHistory;
    private long heartWallTime;
    private long linkStarted;
    private long firstHeartDelay;
    private long heartInterval;
    private int min;
    private int max;
    private int battery = -1;
    private boolean charging;
    private long batteryAt;
    private int validCount;
    private int invalidCount;
    private int heartCount;
    private int batteryCount;
    private int heartPollCount;
    private int batteryPollCount;
    private int heartPollFailures;
    private int batteryPollFailures;

    static SharedPreferences preferences(Context context) {
        return context.getSharedPreferences("bridge", MODE_PRIVATE);
    }

    static boolean hasBluetoothPermission(Context context) {
        return Build.VERSION.SDK_INT < 31
                ? context.checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) == PackageManager.PERMISSION_GRANTED
                : context.checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN) == PackageManager.PERMISSION_GRANTED
                && context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        BluetoothManager manager = getSystemService(BluetoothManager.class);
        adapter = manager == null ? null : manager.getAdapter();
        prefs = preferences(this);
        autoReconnect = prefs.getBoolean("autoReconnect", true);
        String address = prefs.getString("lastAddress", "");
        if (BluetoothAdapter.checkBluetoothAddress(address)) {
            remembered = new Candidate(address, prefs.getString("lastName", "前回のリング"), Integer.MIN_VALUE, true);
            candidates = Collections.singletonList(remembered);
        }
        outputValues = BridgeSettings.load(prefs).values();
        thread = new HandlerThread("ColmiRing BLE");
        thread.start();
        worker = new Handler(thread.getLooper());
        publish();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return binder;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) {
            stopSelfResult(startId);
            return START_NOT_STICKY;
        }
        String action = intent.getAction();
        if (!foreground) {
            if (!hasBluetoothPermission(this)) {
                stopSelfResult(startId);
                return START_NOT_STICKY;
            }
            try {
                promote();
            } catch (IllegalStateException | SecurityException ex) {
                worker.post(() -> {
                    ringStatus = "バックグラウンド動作を開始できません · Bluetooth の権限を確認してください";
                    publish();
                });
                stopSelfResult(startId);
                return START_NOT_STICKY;
            }
        }

        mainStartId = startId;
        worker.post(() -> {
            workerStartId = startId;
            if (action != null) {
                switch (action) {
                    case SCAN:
                        scan();
                        break;
                    case CONNECT:
                        String address = intent.getStringExtra("address");
                        candidates.stream().filter(candidate -> candidate.address().equals(address)).findFirst()
                                .ifPresent(candidate -> connect(candidate, false));
                        break;
                    case RESUME:
                        if (!resumed) {
                            resumed = true;
                            scheduleRetry();
                        }
                        break;
                    case RECONNECT_ON:
                        changeAutoReconnect(true);
                        break;
                    case OUTPUT:
                        startOsc();
                        break;
                    default:
                        break;
                }
            }
            publish();
            idleIfDone();
        });
        return START_NOT_STICKY;
    }

    private void promote() {
        NotificationManager notifications = getSystemService(NotificationManager.class);
        notifications.createNotificationChannel(new NotificationChannel(CHANNEL, "リング・OSC 接続",
                NotificationManager.IMPORTANCE_LOW));
        int flags = PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT;
        PendingIntent open = PendingIntent.getActivity(this, 0, new Intent(this, MainActivity.class)
                .addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_SINGLE_TOP), flags);
        Notification notification = new Notification.Builder(this, CHANNEL)
                .setSmallIcon(R.drawable.ic_ring)
                .setContentTitle(getString(R.string.app_name))
                .setContentText("リング接続・OSC 送信をバックグラウンドで維持")
                .setContentIntent(open)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .build();
        startForeground(1, notification);
        foreground = true;
    }

    private void idleIfDone() {
        boolean retryScheduled = worker.hasCallbacks(retry);
        if (link != Link.IDLE || output != Output.IDLE || retryScheduled) {
            return;
        }
        int idleStartId = workerStartId;
        if (idleStartId == 0) {
            return;
        }
        main.post(() -> {
            if (foreground && !destroyed && mainStartId == idleStartId) {
                stopForeground(STOP_FOREGROUND_REMOVE);
                foreground = false;
                stopSelfResult(idleStartId);
            }
        });
    }

    private void scan() {
        if (link == Link.RECONNECTING) {
            // A manual search takes over from the retry, as on Windows.
            cancelReconnectFor(this::scan);
            return;
        }
        if (link != Link.IDLE) {
            return;
        }
        worker.removeCallbacks(retry);
        stopScan();
        paused = true;
        discoveries.clear();
        scanCallback = new ScanCallback() {
            @Override
            public void onScanResult(int type, ScanResult result) {
                worker.post(() -> acceptScan(this, result));
            }

            @Override
            public void onScanFailed(int code) {
                worker.post(() -> {
                    if (scanCallback == this) {
                        finishScan("BLE 検索に失敗しました (" + code + ")");
                    }
                });
            }
        };
        try {
            scanner = adapter == null ? null : adapter.getBluetoothLeScanner();
            if (scanner == null) {
                finishScan("PICO の Bluetooth をオンにしてください");
                return;
            }
            scanner.startScan(null, new ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build(),
                    scanCallback);
            link = Link.SCANNING;
            ringStatus = "周辺の BLE デバイスを検索中…";
            worker.postDelayed(endScan, 6_000);
        } catch (SecurityException | IllegalStateException ex) {
            finishScan("Bluetooth の状態または権限を確認してください");
        }
    }

    private void acceptScan(ScanCallback source, ScanResult result) {
        if (source != scanCallback) {
            return;
        }
        BluetoothDevice device = result.getDevice();
        String address = device.getAddress();
        String name = null;
        boolean uart = false;
        if (result.getScanRecord() != null) {
            name = result.getScanRecord().getDeviceName();
            List<ParcelUuid> uuids = result.getScanRecord().getServiceUuids();
            uart = uuids != null && uuids.contains(UART_ADVERTISEMENT);
        }
        if (name == null || name.trim().isEmpty()) {
            try {
                name = device.getName();
            } catch (SecurityException ignored) {
                // The advertisement is still usable by address.
            }
        }
        Candidate previous = discoveries.get(address);
        if (name == null || name.trim().isEmpty()) {
            name = previous == null ? "名前なし" : previous.name();
        }
        String upper = name.toUpperCase(Locale.ROOT);
        boolean likely = uart || upper.contains("COLMI") || upper.contains("RING")
                || RING_NAME.matcher(upper).matches() || (previous != null && previous.likelyRing());
        discoveries.put(address, new Candidate(address, name, result.getRssi(), likely));
    }

    private void finishScan(String error) {
        stopScan();
        List<Candidate> found = new ArrayList<>();
        for (Candidate candidate : discoveries.values()) {
            if (candidate.likelyRing()) {
                found.add(candidate);
            }
        }
        found.sort(Comparator.comparing(Candidate::likelyRing).reversed()
                .thenComparing(Comparator.comparingInt(Candidate::rssi).reversed())
                .thenComparing(Candidate::address));
        boolean foundDevices = !found.isEmpty();
        if (remembered != null && found.stream().noneMatch(item -> item.address().equals(remembered.address()))) {
            found.add(remembered);
        }
        candidates = Collections.unmodifiableList(found);
        link = Link.IDLE;
        ringStatus = error != null ? error : !foundDevices
                ? "リングの候補が見つかりません"
                : "検索完了 · リングを選択してください";
        paused = foundDevices;
        scheduleRetry();
        publish();
        idleIfDone();
    }

    private void stopScan() {
        worker.removeCallbacks(endScan);
        if (scanner != null && scanCallback != null) {
            try {
                scanner.stopScan(scanCallback);
            } catch (SecurityException | IllegalStateException ignored) {
                // The adapter may have been disabled during the scan.
            }
        }
        scanner = null;
        scanCallback = null;
    }

    private void changeAutoReconnect(boolean value) {
        autoReconnect = value;
        prefs.edit().putBoolean("autoReconnect", value).apply();
        if (value) {
            paused = false;
            scheduleRetry();
        } else {
            worker.removeCallbacks(retry);
            if (link == Link.RECONNECTING) {
                disconnectRing();
            }
        }
        publish();
        idleIfDone();
    }

    private void scheduleRetry() {
        worker.removeCallbacks(retry);
        if (autoReconnect && !paused && remembered != null && link == Link.IDLE) {
            worker.postDelayed(retry, 1_000);
        }
    }

    private void connect(Candidate candidate, boolean automatic) {
        if (!automatic && link == Link.RECONNECTING) {
            cancelReconnectFor(() -> connect(candidate, false));
            return;
        }
        if (candidate == null || link != Link.IDLE) {
            return;
        }
        worker.removeCallbacks(retry);
        paused = false;
        try {
            if (!hasBluetoothPermission(this) || adapter == null || !adapter.isEnabled()) {
                ringStatus = "Bluetooth をオンにし、付近のデバイスへのアクセスを許可してください";
                scheduleRetry();
                publish();
                idleIfDone();
                return;
            }
            BluetoothDevice device = adapter.getRemoteDevice(candidate.address());
            selected = candidate;
            link = automatic ? Link.RECONNECTING : Link.CONNECTING;
            ringStatus = automatic ? "再接続中…" : "接続中…";
            warning = "";
            model = serial = hardware = firmware = "—";
            validCount = invalidCount = heartCount = batteryCount = 0;
            heartPollCount = batteryPollCount = heartPollFailures = batteryPollFailures = 0;
            heartInterval = firstHeartDelay = 0;
            linkStarted = SystemClock.elapsedRealtime();
            if (!candidate.address().equals(historyAddress)) {
                historyAddress = candidate.address();
                clearHeartHistory();
                batteryHistory = batteryHistories.computeIfAbsent(historyAddress, key -> new TelemetryHistory(true));
            }
            connection = new RingConnection(this, worker, this);
            publish();
            connection.connect(device);
        } catch (SecurityException | IllegalArgumentException ex) {
            ringStatus = "リングの Bluetooth アドレスと権限を確認してください";
            scheduleRetry();
            publish();
            idleIfDone();
        }
    }

    private void cancelReconnectFor(Runnable next) {
        afterReconnectCancellation = next;
        disconnectRing();
    }

    private void disconnectRing() {
        paused = true;
        worker.removeCallbacks(retry);
        if (link == Link.SCANNING) {
            stopScan();
            link = Link.IDLE;
            ringStatus = "検索を停止しました";
        }
        if (connection != null) {
            link = Link.DISCONNECTING;
            ringStatus = "切断中…";
            connection.disconnect();
        }
        publish();
        idleIfDone();
    }

    @Override
    public void onStage(String text) {
        ringStatus = text;
        publish();
    }

    @Override
    public void onReady() {
        link = Link.CONNECTED;
        ringStatus = "接続済み";
        remembered = selected;
        prefs.edit().putString("lastAddress", remembered.address()).putString("lastName", remembered.name()).apply();
        publish();
    }

    @Override
    public void onData(byte[] data) {
        RingProtocol.Sample sample = RingProtocol.decode(data);
        long now = SystemClock.elapsedRealtime();
        if (sample == null) {
            invalidCount++;
            warning = "通知の長さまたはチェックサムが不正です";
        } else {
            validCount++;
            if (sample.error() != 0) {
                warning = "リングの計測ステータス " + sample.error();
            }
            if (sample.bpm() > 0) {
                firstHeartDelay = heartCount == 0 ? now - linkStarted : firstHeartDelay;
                heartInterval = heartAt == 0 ? 0 : now - heartAt;
                heartAt = now;
                bpm = sample.bpm();
                heartWallTime = System.currentTimeMillis();
                heartCount++;
                min = min == 0 ? bpm : Math.min(min, bpm);
                max = Math.max(max, bpm);
                heartHistory.add(now, bpm, false);
            }
            if (sample.battery() >= 0) {
                battery = sample.battery();
                charging = sample.charging();
                batteryAt = now;
                batteryCount++;
                batteryHistory.add(now, battery, charging);
            }
        }
        publish();
    }

    @Override
    public void onDeviceInfo(UUID uuid, String value) {
        if (uuid.equals(RingProtocol.MODEL)) model = value;
        if (uuid.equals(RingProtocol.SERIAL)) serial = value;
        if (uuid.equals(RingProtocol.HARDWARE)) hardware = value;
        if (uuid.equals(RingProtocol.FIRMWARE)) firmware = value;
        publish();
    }

    @Override
    public void onPoll(boolean battery, int status) {
        if (status != 0) {
            warning = "GATT 書き込みに失敗しました (" + status + ")";
            if (battery) batteryPollFailures++;
            else heartPollFailures++;
        } else if (battery) {
            batteryPollCount++;
        } else {
            heartPollCount++;
        }
        publish();
    }

    @Override
    public void onClosed(String reason, boolean manual) {
        connection = null;
        link = Link.IDLE;
        ringStatus = reason;
        bpm = 0;
        heartAt = 0;
        heartWallTime = 0;
        battery = -1;
        selected = null;
        Runnable next = afterReconnectCancellation;
        afterReconnectCancellation = null;
        if (next != null) {
            next.run();
            publish();
            idleIfDone();
            return;
        }
        if (!manual) {
            scheduleRetry();
        }
        publish();
        idleIfDone();
    }

    private void clearHeartHistory() {
        min = max = 0;
        heartHistory.clear();
    }

    private int freshBpm() {
        Snapshot current = snapshot;
        long at = current.heartAt();
        return at != 0 && SystemClock.elapsedRealtime() - at <= HEART_FRESH_MS ? current.bpm() : 0;
    }

    private void startOsc() {
        if (output != Output.IDLE) {
            return;
        }
        try {
            BridgeSettings.Output settings = BridgeSettings.load(prefs);
            BridgeSettings.Endpoint endpoint = settings.endpoint();
            outputValues = settings.values();
            output = Output.SENDING;
            oscError = null;
            lastOutput = "";
            osc.start(endpoint, () -> outputValues, this::freshBpm, new OscOutput.Listener() {
                @Override
                public void onSent(Number value) {
                    worker.post(() -> {
                        lastOutput = value + " · " + outputClock.format(new java.util.Date());
                        publish();
                    });
                }

                @Override
                public void onStopped(String error) {
                    worker.post(() -> {
                        output = Output.IDLE;
                        oscError = error;
                        publish();
                        idleIfDone();
                    });
                }
            });
        } catch (IllegalArgumentException ex) {
            oscError = ex.getMessage();
        }
    }

    private void stopOsc() {
        if (output == Output.SENDING) {
            output = Output.STOPPING;
            osc.stop();
            publish();
        }
    }

    private void publish() {
        String info = "Serial: " + serial + "    Model: " + model + "\nHW: " + hardware + "    FW: " + firmware;
        String diagnostics = "通知  有効 " + validCount + " / 無効 " + invalidCount
                + "\n心拍 " + heartCount + "  ·  バッテリー " + batteryCount
                + "\n心拍ポーリング " + heartPollCount + " / 失敗 " + heartPollFailures
                + "  ·  バッテリー " + batteryPollCount + " / 失敗 " + batteryPollFailures
                + (heartCount > 0 ? "\n初回 BPM " + firstHeartDelay + " ms" : "")
                + (heartInterval > 0 ? "  ·  直近の受信間隔 " + heartInterval + " ms" : "")
                + (warning.isEmpty() ? "" : "\n" + warning);
        snapshot = new Snapshot(link, output, autoReconnect, ringStatus, oscError, lastOutput, candidates,
                selected, info, bpm, heartAt, heartWallTime, min, max, battery, charging, batteryAt,
                heartHistory.snapshot(), batteryHistory == null ? Collections.emptyList() : batteryHistory.snapshot(),
                diagnostics);
    }

    @Override
    public void onDestroy() {
        destroyed = true;
        worker.post(() -> {
            worker.removeCallbacks(retry);
            afterReconnectCancellation = null;
            stopScan();
            if (connection != null) {
                connection.closeNow();
            }
            osc.shutdown();
            thread.quitSafely();
        });
        super.onDestroy();
    }
}
