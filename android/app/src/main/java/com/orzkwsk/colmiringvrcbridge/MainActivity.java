package com.orzkwsk.colmiringvrcbridge;

import android.Manifest;
import android.app.Activity;
import android.content.ComponentName;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Insets;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.graphics.drawable.StateListDrawable;
import android.net.ConnectivityManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.SystemClock;
import android.text.InputType;
import android.text.SpannableString;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.text.style.ForegroundColorSpan;
import android.text.style.RelativeSizeSpan;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.view.inputmethod.InputMethodManager;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;

import java.text.DateFormat;
import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Date;
import java.util.List;
import java.util.Locale;

public final class MainActivity extends Activity {
    private static final int BG = 0xFF10191C;
    private static final int CARD = 0xFF192528;
    private static final int INSET = 0xFF121D21;
    private static final int INK = 0xFFEEF8F5;
    static final int MUTED = 0xFFA8BFBA;
    static final int LINE = 0xFF304347;
    static final int ACCENT = 0xFF79E3BA;
    private static final int ON_ACCENT = 0xFF10251D;
    private static final int ACCENT_SURFACE = 0xFF253E36;
    private static final int BUTTON_SURFACE = 0xFF2A3B3E;
    private static final int PLAIN_INPUT = InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS;
    private static final int PERMISSION_BLE = 11;
    private static final int PERMISSION_NOTIFICATION = 12;

    private final Handler ui = new Handler(Looper.getMainLooper());
    private final DateFormat clock = new SimpleDateFormat("HH:mm:ss.SSS", Locale.ROOT);
    private final Runnable refresh = new Runnable() {
        @Override public void run() {
            render();
            ui.postDelayed(this, 250);
        }
    };

    private SharedPreferences prefs;
    private BridgeService.LocalBinder bridge;
    private boolean bound;
    private boolean resumed;
    private boolean requestedResume;
    private boolean rendering;
    private Intent pending;
    private String selectedAddress;
    private List<BridgeService.Candidate> shownCandidates = Collections.emptyList();

    private TextView connectedRing, connectionStatus, measurementStatus, bpm, battery, lastSample, minimum, maximum;
    private TextView batterySummary, deviceDetails, ringMessage, oscStatus, settingsStatus, lastSent, outputMessage, addressPreview;
    private Button scan, connect, output, defaults, wifiPrefix, monitorTab, settingsTab;
    private Spinner devicePicker, type, scaling, loss;
    private EditText host, port, parameter, interval, fixedBpm, specified;
    private CheckBox useFixed, autoReconnect;
    private ScrollView scroll;
    private LinearLayout monitorPage, settingsPage, batteryHistory, specifiedRow;
    private TelemetryChart heartChart, batteryChart;
    private BridgeSettings.Output displayedSettings;
    private boolean showingSettings;

    private final ServiceConnection serviceConnection = new ServiceConnection() {
        @Override public void onServiceConnected(ComponentName name, IBinder service) {
            bridge = (BridgeService.LocalBinder) service;
            maybeResume();
            render();
        }

        @Override public void onServiceDisconnected(ComponentName name) {
            bridge = null;
            requestedResume = false;
            render();
        }
    };

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        prefs = BridgeService.preferences(this);
        if (state != null) {
            selectedAddress = state.getString("selectedAddress");
            showingSettings = state.getBoolean("showingSettings");
        }
        createUi();
        bound = bindService(new Intent(this, BridgeService.class), serviceConnection, BIND_AUTO_CREATE);
        render();
    }

    @Override
    protected void onResume() {
        super.onResume();
        resumed = true;
        maybeResume();
        refresh.run();
    }

    @Override
    protected void onPause() {
        resumed = false;
        ui.removeCallbacks(refresh);
        super.onPause();
    }

    @Override
    protected void onSaveInstanceState(Bundle state) {
        int index = devicePicker.getSelectedItemPosition() - 1;
        if (index >= 0 && index < shownCandidates.size()) {
            state.putString("selectedAddress", shownCandidates.get(index).address());
        }
        state.putBoolean("showingSettings", showingSettings);
        super.onSaveInstanceState(state);
    }

    @Override
    protected void onDestroy() {
        if (bound) {
            unbindService(serviceConnection);
        }
        super.onDestroy();
    }

    private void maybeResume() {
        if (resumed && !requestedResume && bridge != null && BridgeService.hasBluetoothPermission(this)
                && prefs.getBoolean("autoReconnect", true) && prefs.contains("lastAddress")) {
            requestedResume = true;
            startBridge(new Intent(this, BridgeService.class).setAction(BridgeService.RESUME));
        }
    }

    private void launch(String action) {
        launch(new Intent(this, BridgeService.class).setAction(action));
    }

    private void launch(Intent command) {
        if (pending != null) {
            return;
        }
        if (!BridgeService.hasBluetoothPermission(this)) {
            pending = command;
            String[] permissions = Build.VERSION.SDK_INT >= 31
                    ? new String[] { Manifest.permission.BLUETOOTH_SCAN, Manifest.permission.BLUETOOTH_CONNECT }
                    : new String[] { Manifest.permission.ACCESS_FINE_LOCATION };
            requestPermissions(permissions, PERMISSION_BLE);
            return;
        }
        notificationThenStart(command);
    }

    private void notificationThenStart(Intent command) {
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED && !prefs.getBoolean("askedNotification", false)) {
            prefs.edit().putBoolean("askedNotification", true).apply();
            pending = command;
            requestPermissions(new String[] { Manifest.permission.POST_NOTIFICATIONS }, PERMISSION_NOTIFICATION);
        } else {
            startBridge(command);
        }
    }

    private void startBridge(Intent command) {
        try {
            startForegroundService(command);
        } catch (IllegalStateException | SecurityException ex) {
            message("サービスを開始できません。アプリを表示した状態で権限を確認してください");
        }
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(requestCode, permissions, results);
        Intent command = pending;
        pending = null;
        if (command == null) {
            return;
        }
        if (requestCode == PERMISSION_BLE && !BridgeService.hasBluetoothPermission(this)) {
            message(Build.VERSION.SDK_INT >= 31 ? "設定で「付近のデバイス」を許可してください"
                    : "Android 10 / 11 では、BLE 検索に位置情報の許可が必要です");
        } else if (requestCode == PERMISSION_BLE) {
            notificationThenStart(command);
        } else if (requestCode == PERMISSION_NOTIFICATION) {
            startBridge(command);
        }
    }

    private void createUi() {
        LinearLayout window = new LinearLayout(this);
        window.setOrientation(LinearLayout.VERTICAL);
        window.setBackgroundColor(BG);
        window.setFocusableInTouchMode(true);
        window.setOnApplyWindowInsetsListener(this::applyInsets);

        LinearLayout chrome = column(window, new LinearLayout.LayoutParams(-1, -2));
        chrome.setPadding(dp(16), dp(12), dp(16), dp(10));
        createHeader(chrome);

        scroll = new ScrollView(this);
        window.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        LinearLayout content = new LinearLayout(this);
        content.setOrientation(LinearLayout.VERTICAL);
        content.setPadding(dp(16), 0, dp(16), dp(16));
        scroll.addView(content);

        BridgeSettings.Output settings = BridgeSettings.load(prefs);
        PanelColumns monitor = new PanelColumns(550, 1.62f, true);
        monitorPage = monitor;
        content.addView(monitor, -1, -2);
        createTelemetryPanel(monitor.left);
        createRingStatePanel(monitor.left);
        createConnectionPanel(monitor.right);
        createOscPanel(monitor.right, settings);

        PanelColumns configuration = new PanelColumns(550, 1, false);
        settingsPage = configuration;
        content.addView(configuration, -1, -2);
        createParameterPanel(configuration.left, settings);
        createFallbackPanel(configuration.right, settings);

        setContentView(window);
        showPage(showingSettings);

        for (EditText field : new EditText[] {host, port, parameter, interval, fixedBpm, specified}) {
            field.addTextChangedListener(new TextWatcher() {
                @Override public void beforeTextChanged(CharSequence s, int start, int count, int after) {}
                @Override public void onTextChanged(CharSequence s, int start, int before, int count) {
                    settingsChanged();
                }
                @Override public void afterTextChanged(android.text.Editable s) {}
            });
        }
        for (Spinner picker : new Spinner[] {type, scaling, loss}) {
            onSelection(picker, this::settingsChanged);
        }
        onSelection(devicePicker, this::render);
        useFixed.setOnCheckedChangeListener((view, value) -> settingsChanged());
        updateSettingLabels(settings);
    }

    private void createHeader(LinearLayout chrome) {
        PanelColumns header = new PanelColumns(470, 1.4f, false);
        chrome.addView(header, -1, -2);
        header.setGravity(Gravity.CENTER_VERTICAL);

        LinearLayout brand = row(header.left);
        ImageView mark = new ImageView(this);
        mark.setImageResource(R.drawable.ic_ring);
        mark.setColorFilter(ACCENT);
        mark.setScaleType(ImageView.ScaleType.CENTER_INSIDE);
        mark.setBackground(surface(ACCENT_SURFACE, 9, 0));
        mark.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        brand.addView(mark, new LinearLayout.LayoutParams(dp(34), dp(34)));
        TextView title = heading(brand, getString(R.string.app_name), 22);
        title.setPadding(dp(8), 0, 0, 0);
        fillRow(title);

        LinearLayout tabs = row(header.right);
        tabs.setLayoutParams(new LinearLayout.LayoutParams(-2, -2));
        tabs.setPadding(dp(3), dp(3), dp(3), dp(3));
        tabs.setBackground(surface(INSET, 11, LINE));
        header.right.setGravity(Gravity.END);
        monitorTab = button(tabs, "モニター", false);
        settingsTab = button(tabs, "VRC設定", false);
        spaceBefore(settingsTab, 3);
        monitorTab.setOnClickListener(view -> showPage(false));
        settingsTab.setOnClickListener(view -> showPage(true));
    }

    private void showPage(boolean settings) {
        if (settings != showingSettings) {
            View focus = getCurrentFocus();
            if (focus instanceof EditText) {
                InputMethodManager keyboard = getSystemService(InputMethodManager.class);
                if (keyboard != null) keyboard.hideSoftInputFromWindow(focus.getWindowToken(), 0);
                focus.clearFocus();
            }
        }
        showingSettings = settings;
        monitorPage.setVisibility(settings ? View.GONE : View.VISIBLE);
        settingsPage.setVisibility(settings ? View.VISIBLE : View.GONE);
        styleTab(monitorTab, !settings);
        styleTab(settingsTab, settings);
        scroll.scrollTo(0, 0);
        render();
    }

    private void createTelemetryPanel(LinearLayout parent) {
        LinearLayout telemetry = card(parent);
        measurementStatus = panelHeading(telemetry, "心拍", "No reading");
        LinearLayout vitals = row(telemetry);
        gapAbove(vitals, 6);
        vitals.setGravity(Gravity.BOTTOM);

        bpm = heading(vitals, "", 76);
        bpm.setTextColor(ACCENT);
        bpm.setIncludeFontPadding(false);
        bpm.setMaxLines(1);
        bpm.setGravity(Gravity.BOTTOM | Gravity.START);
        bpm.setTextLocale(Locale.ENGLISH);
        fillRow(bpm);
        bpm.getLayoutParams().height = dp(86);
        bpm.setAutoSizeTextTypeUniformWithConfiguration(29, 76, 1, android.util.TypedValue.COMPLEX_UNIT_SP);
        showBpm("—");

        LinearLayout extrema = row(vitals);
        extrema.setLayoutParams(new LinearLayout.LayoutParams(-2, -2));
        extrema.setPadding(0, 0, 0, dp(6));
        english(text(extrema, "Min", 13, MUTED));
        minimum = english(heading(extrema, "—", 13));
        spaceBefore(minimum, 4);
        TextView maxLabel = english(text(extrema, "Max", 13, MUTED));
        spaceBefore(maxLabel, 10);
        maximum = english(heading(extrema, "—", 13));
        spaceBefore(maximum, 4);

        heartChart = chart(telemetry, false);
        gapAbove(heartChart, 6);
        LinearLayout sample = row(telemetry);
        lastSample = english(text(sample, "Last sample —", 13, MUTED));
        fillRow(lastSample);
        Button reset = textButton(sample, "Reset");
        reset.setTextLocale(Locale.ENGLISH);
        reset.setOnClickListener(view -> {
            if (bridge != null) bridge.resetHeartHistory();
        });
    }

    private void createRingStatePanel(LinearLayout parent) {
        LinearLayout ring = card(parent);
        connectionStatus = panelHeading(ring, "リング状態", "未接続");
        LinearLayout data = row(ring);
        gapAbove(data, 8);
        connectedRing = heading(data, "—", 16);
        connectedRing.setSingleLine();
        connectedRing.setEllipsize(TextUtils.TruncateAt.END);
        fillRow(connectedRing);
        battery = text(data, "残量 —", 13, MUTED);
        spaceBefore(battery, 6);
        ringMessage = text(ring, "", 13, MUTED);
        ringMessage.setLines(2);
        ringMessage.setEllipsize(TextUtils.TruncateAt.END);
        gapAbove(ringMessage, 4);

        batteryHistory = disclosure(ring, "バッテリー履歴 · 24時間");
        batteryChart = chart(batteryHistory, true);
        batterySummary = english(text(batteryHistory, "", 12, MUTED));
        LinearLayout details = disclosure(ring, "リング詳細");
        deviceDetails = english(text(details, "—", 13, MUTED));
        deviceDetails.setTextIsSelectable(true);
    }

    private void createConnectionPanel(LinearLayout parent) {
        LinearLayout panel = card(parent);
        heading(panel, "リング接続", 17);
        TextView label = fieldLabel(panel, "リングを選択");
        gapAbove(label, 7);
        devicePicker = spinner(panel, "リングを選択", "リングを選択");
        labelField(label, devicePicker);
        LinearLayout actions = row(panel);
        gapAbove(actions, 8);
        scan = button(actions, "検索", false);
        connect = button(actions, "接続", false);
        fillRow(scan);
        fillRow(connect);
        spaceBefore(connect, 7);
        autoReconnect = check(panel, "自動再接続");
        gapAbove(autoReconnect, 4);

        scan.setOnClickListener(view -> launch(BridgeService.SCAN));
        connect.setOnClickListener(view -> toggleLink());
        autoReconnect.setOnCheckedChangeListener((view, checked) -> {
            if (!rendering && bridge != null) {
                if (checked && prefs.contains("lastAddress")) {
                    launch(BridgeService.RECONNECT_ON);
                } else {
                    bridge.setAutoReconnect(checked);
                }
            }
        });
    }

    private void createOscPanel(LinearLayout parent, BridgeSettings.Output settings) {
        LinearLayout osc = card(parent);
        oscStatus = panelHeading(osc, "VRChat OSC", "停止中");
        output = button(osc, "送信開始", true);
        output.getLayoutParams().width = -1;
        gapAbove(output, 8);
        output.setOnClickListener(view -> {
            if (bridge != null && bridge.snapshot().output() != BridgeService.Output.IDLE) {
                bridge.stopOutput();
            } else {
                beginOutput();
            }
        });

        host = input(osc, "IPアドレス", settings.host(), InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        host.setTypeface(Typeface.MONOSPACE);
        port = input(osc, "ポート", settings.port(), InputType.TYPE_CLASS_NUMBER);
        port.setTypeface(Typeface.MONOSPACE);
        port.getLayoutParams().width = dp(104);

        LinearLayout quick = row(osc);
        gapAbove(quick, 8);
        wifiPrefix = button(quick, "Wi-Fiから補完", false);
        defaults = button(quick, "初期値に戻す", false);
        fillRow(wifiPrefix);
        fillRow(defaults);
        spaceBefore(defaults, 6);
        for (Button action : new Button[] {wifiPrefix, defaults}) {
            action.setTextSize(13);
            action.setPadding(dp(5), dp(5), dp(5), dp(5));
        }
        wifiPrefix.setOnClickListener(view -> fillWifiPrefix());
        defaults.setOnClickListener(view -> {
            host.setText(BridgeSettings.DEFAULT_HOST);
            port.setText(BridgeSettings.DEFAULT_PORT);
        });

        outputMessage = text(osc, "", 13, MUTED);
        outputMessage.setVisibility(View.GONE);
        gapAbove(outputMessage, 8);
        lastSent = text(osc, "最終送信 —", 13, MUTED);
        gapAbove(lastSent, 8);
    }

    private void createParameterPanel(LinearLayout parent, BridgeSettings.Output settings) {
        LinearLayout panel = card(parent);
        settingsStatus = panelHeading(panel, "アバターパラメーター", "停止中");
        parameter = input(panel, "パラメーター名", settings.parameter(), PLAIN_INPUT);
        addressPreview = text(panel, settings.address(), 12, MUTED);
        addressPreview.setTypeface(Typeface.MONOSPACE);
        gapAbove(addressPreview, 4);

        LinearLayout fields = row(panel);
        fields.setGravity(Gravity.TOP);
        gapAbove(fields, 7);
        LinearLayout types = column(fields, new LinearLayout.LayoutParams(0, -2, 1));
        type = selectField(types, "OSC の型", "Int", "Float");
        type.setSelection(settings.floating() ? 1 : 0, false);
        LinearLayout scales = column(fields, new LinearLayout.LayoutParams(0, -2, 1.35f));
        scales.setPadding(dp(8), 0, 0, 0);
        scaling = selectField(scales, "スケーリング", "Raw BPM", "BPM ÷ 255");
        scaling.setSelection(settings.normalize() ? 1 : 0, false);
        interval = input(panel, "送信間隔（秒）", settings.interval(), PLAIN_INPUT);
        interval.getLayoutParams().width = dp(108);
    }

    private void createFallbackPanel(LinearLayout parent, BridgeSettings.Output settings) {
        LinearLayout panel = card(parent);
        heading(panel, "固定出力・心拍途絶時", 17);
        useFixed = check(panel, "固定BPMを出力");
        useFixed.setChecked(settings.useFixed());
        fixedBpm = input(panel, "固定BPM", settings.fixedBpm(),
                InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_SIGNED);
        fixedBpm.getLayoutParams().width = dp(108);
        TextView lossLabel = fieldLabel(panel, "心拍が途切れたとき");
        gapAbove(lossLabel, 6);
        loss = spinner(panel, "心拍が途切れたとき", "最終値を保持", "固定BPMに自動切り替え", "指定値に固定");
        labelField(lossLabel, loss);
        loss.setSelection(settings.loss().ordinal(), false);
        specifiedRow = column(panel, new LinearLayout.LayoutParams(-1, -2));
        specified = input(specifiedRow, "途絶時の指定値", settings.specified(), PLAIN_INPUT);
        specified.getLayoutParams().width = dp(108);
    }

    private void fillWifiPrefix() {
        if (bridge == null || bridge.snapshot().output() != BridgeService.Output.IDLE) return;
        String prefix = WifiIpv4.currentPrefix(getSystemService(ConnectivityManager.class));
        if (prefix == null) {
            message("接続中の Wi-Fi の IPv4 アドレスを取得できませんでした");
            return;
        }
        host.setText(prefix);
        host.requestFocus();
        host.setSelection(prefix.length());
        InputMethodManager keyboard = getSystemService(InputMethodManager.class);
        if (keyboard != null) keyboard.showSoftInput(host, InputMethodManager.SHOW_IMPLICIT);
    }

    private void toggleLink() {
        if (bridge == null) return;
        BridgeService.Snapshot current = bridge.snapshot();
        if (current.link() == BridgeService.Link.CONNECTED) {
            bridge.disconnect();
            return;
        }
        int index = devicePicker.getSelectedItemPosition() - 1;
        if (index >= 0 && index < shownCandidates.size()) {
            launch(new Intent(this, BridgeService.class).setAction(BridgeService.CONNECT)
                    .putExtra("address", shownCandidates.get(index).address()));
        }
    }

    private void beginOutput() {
        try {
            BridgeSettings.Output settings = readSettings();
            settings.endpoint();
            BridgeSettings.save(prefs, settings);
            launch(BridgeService.OUTPUT);
        } catch (IllegalArgumentException ex) {
            message(ex.getMessage());
        }
    }

    private BridgeSettings.Output readSettings() {
        boolean floating = type.getSelectedItemPosition() == 1;
        return new BridgeSettings.Output(value(host), value(port), value(parameter), floating,
                floating && scaling.getSelectedItemPosition() == 1, value(interval), useFixed.isChecked(),
                value(fixedBpm), BridgeSettings.Loss.values()[Math.max(0, loss.getSelectedItemPosition())],
                value(specified));
    }

    private void settingsChanged() {
        BridgeSettings.Output settings = readSettings();
        if (!settings.floating() && scaling.getSelectedItemPosition() != 0) {
            scaling.setSelection(0);
        }
        updateSettingLabels(settings);
        BridgeSettings.save(prefs, settings);
        if (bridge != null) bridge.updateValues();
        render();
    }

    private void updateSettingLabels(BridgeSettings.Output settings) {
        displayedSettings = settings;
        addressPreview.setText(settings.address());
        specifiedRow.setVisibility(settings.loss() == BridgeSettings.Loss.SPECIFIED ? View.VISIBLE : View.GONE);
    }

    private void render() {
        if (bridge == null) {
            devicePicker.setEnabled(false);
            scan.setEnabled(false);
            connect.setEnabled(false);
            output.setEnabled(false);
            autoReconnect.setEnabled(false);
            wifiPrefix.setEnabled(false);
            defaults.setEnabled(false);
            return;
        }
        BridgeService.Snapshot state = bridge.snapshot();
        long now = SystemClock.elapsedRealtime();
        boolean connected = state.link() == BridgeService.Link.CONNECTED;
        boolean canSelect = state.link() == BridgeService.Link.IDLE || state.link() == BridgeService.Link.RECONNECTING;
        boolean canEditEndpoint = state.output() == BridgeService.Output.IDLE;
        boolean fresh = connected && state.heartAt() != 0 && now - state.heartAt() <= BridgeService.HEART_FRESH_MS;

        updateCandidates(state.candidates(), canSelect ? null : state.ring());
        renderConnection(state, connected, canSelect);
        renderTelemetry(state, now, connected, fresh);
        renderOutput(state, fresh, canEditEndpoint);
    }

    private void renderConnection(BridgeService.Snapshot state, boolean connected, boolean canSelect) {
        rendering = true;
        autoReconnect.setChecked(state.autoReconnect());
        rendering = false;
        autoReconnect.setEnabled(true);
        scan.setEnabled(canSelect);
        devicePicker.setEnabled(canSelect);
        setText(connectedRing, state.ring() == null ? "—" : state.ring().name());
        setText(connect, connected ? "切断" : "接続");
        connect.setEnabled(connected || canSelect && devicePicker.getSelectedItemPosition() > 0);
        String status = switch (state.link()) {
            case SCANNING -> "検索中";
            case CONNECTING -> "接続中";
            case RECONNECTING -> "再接続中";
            case DISCONNECTING -> "切断中";
            case CONNECTED -> "接続済み";
            default -> "未接続";
        };
        updateBadge(connectionStatus, status, connected);
        String detail = state.ringStatus();
        if (connected || detail.equals(status) || detail.equals(status + "…")) {
            detail = "";
        }
        if (!TextUtils.equals(ringMessage.getText(), detail)) {
            ringMessage.setText(detail);
            ringMessage.setTooltipText(detail.isEmpty() ? null : detail);
        }
    }

    private void renderTelemetry(BridgeService.Snapshot state, long now, boolean connected, boolean fresh) {
        measurementStatus.setTextLocale(fresh ? Locale.JAPANESE : Locale.ENGLISH);
        String measurement = fresh ? "測定中" : connected && state.heartAt() != 0 ? "Stale" : "No reading";
        updateBadge(measurementStatus, measurement, fresh);
        showBpm(bpmText(state.bpm()));
        String lastTime = state.heartWallTime() == 0 ? "—" : clock.format(new Date(state.heartWallTime()));
        setText(lastSample, getString(R.string.heart_received_at, lastTime));
        setText(minimum, bpmText(state.min()));
        setText(maximum, bpmText(state.max()));

        if (state.battery() < 0) {
            setText(battery, "残量 —");
        } else {
            String charge = state.charging() ? "充電中" : "充電なし";
            String reading = getString(R.string.battery_reading, state.battery(), charge);
            setText(battery, now - state.batteryAt() > 180_000 ? getString(R.string.stale_value, reading) : reading);
        }
        if (showingSettings) return;
        heartChart.show(state.heartHistory(), now);
        if (batteryHistory.getVisibility() == View.VISIBLE) {
            batteryChart.show(state.batteryHistory(), now);
            renderBatterySummary(state.batteryHistory(), now);
        }
        if (deviceDetails.isShown()) renderRingDetails(state, connected, fresh);
    }

    private void renderRingDetails(BridgeService.Snapshot state, boolean connected, boolean fresh) {
        String telemetry = !connected ? "Disconnected"
                : state.heartAt() == 0 ? "Initializing" : fresh ? "Streaming" : "Stale";
        BridgeService.RingDiagnostics counts = state.diagnostics();
        String diagnostics = getString(R.string.ring_diagnostics, telemetry,
                counts.heartPackets(), counts.batteryPackets(), counts.heartPolls(), counts.heartFailures(),
                counts.batteryPolls(), counts.batteryFailures(), counts.validPackets(), counts.invalidPackets());
        if (!counts.warning().isEmpty()) {
            diagnostics += "\n" + getString(R.string.ring_last_warning, counts.warning());
        }
        String address = connected && state.ring() != null ? state.ring().address() : "—";
        setText(deviceDetails, getString(R.string.ring_details, address, connected ? state.deviceInfo() : "—", diagnostics));
    }

    private void renderOutput(BridgeService.Snapshot state, boolean fresh, boolean canEditEndpoint) {
        boolean sending = state.output() == BridgeService.Output.SENDING;
        String summary = switch (state.output()) {
            case IDLE -> state.oscError() == null ? "停止中" : "送信エラー";
            case STOPPING -> "停止中…";
            case SENDING -> "送信中";
        };
        updateBadge(oscStatus, summary, sending);
        updateBadge(settingsStatus, summary, sending);
        String detail = outputText(state, fresh);
        boolean showDetail = state.oscError() != null || sending && !detail.equals("送信中・実測");
        setMessage(outputMessage, showDetail ? detail : "");
        setText(lastSent, getString(R.string.last_output, state.lastOutput().isEmpty() ? "—" : state.lastOutput()));
        setText(output, canEditEndpoint ? "送信開始" : "送信停止");
        output.setEnabled(state.output() != BridgeService.Output.STOPPING);
        for (View field : new View[] {host, port, parameter, interval, type, defaults, wifiPrefix}) {
            field.setEnabled(canEditEndpoint);
        }
        scaling.setEnabled(canEditEndpoint && displayedSettings.floating());
    }

    private void showBpm(String value) {
        String label = getString(R.string.heart_rate, value);
        if (TextUtils.equals(bpm.getText(), label)) return;
        SpannableString styled = new SpannableString(label);
        int unitStart = label.lastIndexOf("BPM");
        if (unitStart >= 0) {
            styled.setSpan(new ForegroundColorSpan(MUTED), unitStart, label.length(), 0);
            styled.setSpan(new RelativeSizeSpan(.21f), unitStart, label.length(), 0);
        }
        bpm.setText(styled);
    }

    private String outputText(BridgeService.Snapshot state, boolean fresh) {
        if (state.oscError() != null) return state.oscError();
        if (state.output() == BridgeService.Output.IDLE) return "送信停止中";
        if (state.output() == BridgeService.Output.STOPPING) return "送信停止処理中";
        BridgeSettings.Values values = displayedSettings.values();
        if (values.useFixed()) {
            return values.fixedBpm() == null ? "一時停止・固定BPMを入力" : "送信中・固定BPM";
        }
        if (fresh) return "送信中・実測";
        return switch (values.loss()) {
            case HOLD -> state.lastOutput().isEmpty() ? "一時停止・心拍待ち" : "心拍途絶・最終値を保持";
            case FIXED -> values.fixedBpm() == null ? "一時停止・固定BPMを入力" : "心拍途絶・固定BPMを送信";
            case SPECIFIED -> values.specified() == null ? "一時停止・途絶時の指定値を入力" : "心拍途絶・指定値を送信";
        };
    }

    private void renderBatterySummary(List<TelemetryHistory.Point> history, long now) {
        int first = 0;
        while (first < history.size() && history.get(first).elapsed() < now - TelemetryHistory.BATTERY_GRAPH_MS) {
            first++;
        }
        if (first == history.size()) {
            batterySummary.setText(R.string.battery_history_empty);
            return;
        }
        int initial = history.get(first).value();
        int current = history.get(history.size() - 1).value();
        String delta = String.format(Locale.ROOT, "%+d", current - initial);
        int samples = history.size() - first;
        setText(batterySummary, getResources().getQuantityString(R.plurals.battery_history_summary,
                samples, initial, current, delta, samples));
    }

    private void updateCandidates(List<BridgeService.Candidate> candidates, BridgeService.Candidate activeRing) {
        if (activeRing != null && candidates.stream().noneMatch(candidate -> candidate.address().equals(activeRing.address()))) {
            candidates = new ArrayList<>(candidates);
            candidates.add(activeRing);
        }
        boolean changed = !shownCandidates.equals(candidates);
        if (!changed && activeRing == null) return;

        int previous = devicePicker.getSelectedItemPosition() - 1;
        if (activeRing != null) {
            selectedAddress = activeRing.address();
        } else if (previous >= 0 && previous < shownCandidates.size()) {
            selectedAddress = shownCandidates.get(previous).address();
        }
        if (changed) {
            shownCandidates = candidates;
            List<String> labels = new ArrayList<>();
            labels.add("リングを選択");
            for (BridgeService.Candidate candidate : candidates) {
                labels.add(candidate.label());
            }
            devicePicker.setAdapter(adapter(labels));
        }

        int index = candidates.isEmpty() ? 0 : 1;
        for (int i = 0; i < candidates.size(); i++) {
            if (candidates.get(i).address().equals(selectedAddress)) {
                index = i + 1;
                break;
            }
        }
        if (devicePicker.getSelectedItemPosition() != index) {
            devicePicker.setSelection(index, false);
        }
    }

    private WindowInsets applyInsets(View view, WindowInsets insets) {
        if (Build.VERSION.SDK_INT >= 30) {
            Insets safe = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout()
                    | WindowInsets.Type.ime());
            view.setPadding(safe.left, safe.top, safe.right, safe.bottom);
            return WindowInsets.CONSUMED;
        }
        return insets;
    }

    private final class PanelColumns extends LinearLayout {
        private final LinearLayout left;
        private final LinearLayout right;
        private final int minimumWide;
        private final boolean stackRightFirst;
        private final LayoutParams stackedLeft = new LayoutParams(-1, -2);
        private final LayoutParams stackedRight = new LayoutParams(-1, -2);
        private final LayoutParams wideLeft;
        private final LayoutParams wideRight;

        PanelColumns(int minimumWide, float leftWeight, boolean stackRightFirst) {
            super(MainActivity.this);
            this.minimumWide = minimumWide;
            this.stackRightFirst = stackRightFirst;
            wideLeft = new LayoutParams(0, -2, leftWeight);
            wideRight = new LayoutParams(0, -2, 1);
            wideRight.setMarginStart(dp(11));
            (stackRightFirst ? stackedLeft : stackedRight).topMargin = dp(11);
            setOrientation(VERTICAL);
            left = column(this, stackedLeft);
            right = column(this, stackedRight);
            if (stackRightFirst) bringChildToFront(left);
        }

        @Override
        protected void onMeasure(int widthSpec, int heightSpec) {
            boolean wide = MeasureSpec.getSize(widthSpec) >= dp(minimumWide);
            int orientation = wide ? HORIZONTAL : VERTICAL;
            if (orientation != getOrientation()) {
                setOrientation(orientation);
                left.setLayoutParams(wide ? wideLeft : stackedLeft);
                right.setLayoutParams(wide ? wideRight : stackedRight);
                if (stackRightFirst) bringChildToFront(wide ? right : left);
            }
            super.onMeasure(widthSpec, heightSpec);
        }
    }

    private LinearLayout card(LinearLayout parent) {
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(14), dp(13), dp(14), dp(13));
        panel.setBackground(surface(CARD, 13, LINE));
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.bottomMargin = dp(11);
        parent.addView(panel, params);
        return panel;
    }

    private TextView panelHeading(LinearLayout panel, String title, String initialStatus) {
        LinearLayout head = row(panel);
        TextView label = heading(head, title, 17);
        fillRow(label);
        TextView badge = text(head, "", 13, MUTED);
        spaceBefore(badge, 6);
        badge.setPadding(dp(8), dp(3), dp(8), dp(3));
        badge.setSingleLine();
        updateBadge(badge, initialStatus, false);
        return badge;
    }

    private void updateBadge(TextView badge, String label, boolean active) {
        setText(badge, label);
        if (Boolean.valueOf(active).equals(badge.getTag())) return;
        badge.setTag(active);
        int color = active ? ACCENT : MUTED;
        badge.setTextColor(color);
        badge.setBackground(surface(active ? ACCENT_SURFACE : INSET, 20, 0));
        GradientDrawable dot = surface(color, 3, 0);
        dot.setBounds(0, 0, dp(6), dp(6));
        badge.setCompoundDrawablesRelative(dot, null, null, null);
        badge.setCompoundDrawablePadding(dp(5));
    }

    private LinearLayout disclosure(LinearLayout parent, String label) {
        View line = new View(this);
        line.setBackgroundColor(LINE);
        LinearLayout.LayoutParams lineParams = new LinearLayout.LayoutParams(-1, dp(1));
        lineParams.topMargin = dp(3);
        parent.addView(line, lineParams);
        Button header = textButton(parent, getString(R.string.disclosure_collapsed, label));
        header.getLayoutParams().width = -1;
        header.setGravity(Gravity.START | Gravity.CENTER_VERTICAL);
        header.setContentDescription(label);
        LinearLayout body = column(parent, new LinearLayout.LayoutParams(-1, -2));
        body.setVisibility(View.GONE);
        if (Build.VERSION.SDK_INT >= 30) header.setStateDescription("折りたたみ");
        header.setOnClickListener(view -> {
            boolean expanded = body.getVisibility() != View.VISIBLE;
            body.setVisibility(expanded ? View.VISIBLE : View.GONE);
            header.setText(getString(expanded ? R.string.disclosure_expanded : R.string.disclosure_collapsed, label));
            if (Build.VERSION.SDK_INT >= 30) header.setStateDescription(expanded ? "展開" : "折りたたみ");
            render();
        });
        return body;
    }

    private TelemetryChart chart(LinearLayout parent, boolean batteryType) {
        TelemetryChart chart = new TelemetryChart(this, batteryType);
        parent.addView(chart, new LinearLayout.LayoutParams(-1, dp(batteryType ? 100 : 163)));
        return chart;
    }

    private LinearLayout column(LinearLayout parent, LinearLayout.LayoutParams params) {
        LinearLayout column = new LinearLayout(this);
        column.setOrientation(LinearLayout.VERTICAL);
        parent.addView(column, params);
        return column;
    }

    private LinearLayout row(LinearLayout parent) {
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        parent.addView(row, -1, -2);
        return row;
    }

    private static void fillRow(View view) {
        LinearLayout.LayoutParams params = (LinearLayout.LayoutParams) view.getLayoutParams();
        params.width = 0;
        params.weight = 1;
        view.setLayoutParams(params);
    }

    private void gapAbove(View view, int margin) {
        LinearLayout.LayoutParams params = (LinearLayout.LayoutParams) view.getLayoutParams();
        params.topMargin = dp(margin);
        view.setLayoutParams(params);
    }

    private void spaceBefore(View view, int margin) {
        LinearLayout.LayoutParams params = (LinearLayout.LayoutParams) view.getLayoutParams();
        params.setMarginStart(dp(margin));
        view.setLayoutParams(params);
    }

    private TextView heading(LinearLayout parent, String content, int size) {
        TextView text = text(parent, content, size, INK);
        text.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        return text;
    }

    private TextView text(LinearLayout parent, String content, int size, int color) {
        TextView text = new TextView(this);
        text.setText(content);
        text.setTextSize(size);
        text.setTextColor(color);
        parent.addView(text, parent.getOrientation() == LinearLayout.HORIZONTAL ? -2 : -1, -2);
        return text;
    }

    private static TextView english(TextView view) {
        view.setTextLocale(Locale.ENGLISH);
        return view;
    }

    private static void setText(TextView view, String label) {
        if (!TextUtils.equals(view.getText(), label)) view.setText(label);
    }

    private static void setMessage(TextView view, String label) {
        setText(view, label);
        view.setVisibility(label.isEmpty() ? View.GONE : View.VISIBLE);
    }

    private Button button(LinearLayout parent, String label, boolean primary) {
        Button button = new Button(this);
        button.setText(label);
        button.setAllCaps(false);
        button.setTextSize(15);
        button.setLetterSpacing(0);
        button.setMinimumWidth(0);
        button.setMinWidth(0);
        button.setMinimumHeight(0);
        button.setMinHeight(dp(primary ? 48 : 44));
        button.setPadding(dp(10), dp(5), dp(10), dp(5));
        button.setStateListAnimator(null);
        StateListDrawable background = new StateListDrawable();
        background.addState(new int[] {-android.R.attr.state_enabled}, surface(0xFF223237, 8, LINE));
        background.addState(new int[] {}, ripple(primary ? ACCENT : BUTTON_SURFACE, 8, primary ? ACCENT : LINE));
        button.setBackground(background);
        button.setTextColor(new ColorStateList(new int[][] {
                new int[] {-android.R.attr.state_enabled}, new int[] {}
        }, new int[] {0xFF879C98, primary ? ON_ACCENT : INK}));
        button.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        parent.addView(button, -2, -2);
        return button;
    }

    private Button textButton(LinearLayout parent, String label) {
        Button button = button(parent, label, false);
        button.setTextSize(13);
        button.setPadding(dp(3), dp(3), dp(3), dp(3));
        button.setTextColor(MUTED);
        button.setBackground(ripple(Color.TRANSPARENT, 6, 0));
        return button;
    }

    private void styleTab(Button tab, boolean selected) {
        tab.setSelected(selected);
        tab.setTextColor(selected ? INK : MUTED);
        tab.setBackground(ripple(selected ? BUTTON_SURFACE : Color.TRANSPARENT, 8, 0));
    }

    private CheckBox check(LinearLayout parent, String label) {
        CheckBox view = new CheckBox(this);
        view.setText(label);
        view.setTextColor(INK);
        view.setMinHeight(dp(44));
        view.setTextSize(14);
        parent.addView(view, -2, -2);
        return view;
    }

    private Spinner spinner(LinearLayout parent, String label, String... options) {
        Spinner spinner = new Spinner(this);
        spinner.setContentDescription(label);
        spinner.setMinimumHeight(dp(44));
        spinner.setAdapter(adapter(java.util.Arrays.asList(options)));
        parent.addView(spinner, parent.getOrientation() == LinearLayout.HORIZONTAL ? -2 : -1, -2);
        return spinner;
    }

    private ArrayAdapter<String> adapter(List<String> options) {
        ArrayAdapter<String> adapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, options);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        return adapter;
    }

    private static void onSelection(Spinner spinner, Runnable handler) {
        spinner.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override public void onItemSelected(AdapterView<?> parent, View view, int position, long id) { handler.run(); }
            @Override public void onNothingSelected(AdapterView<?> parent) {}
        });
    }

    private EditText input(LinearLayout parent, String label, String value, int inputType) {
        TextView fieldLabel = fieldLabel(parent, label);
        EditText field = edit(parent, value, inputType);
        labelField(fieldLabel, field);
        return field;
    }

    private Spinner selectField(LinearLayout parent, String label, String... options) {
        TextView fieldLabel = fieldLabel(parent, label);
        Spinner field = spinner(parent, label, options);
        labelField(fieldLabel, field);
        return field;
    }

    private TextView fieldLabel(LinearLayout parent, String label) {
        TextView field = text(parent, label, 13, MUTED);
        field.setPadding(0, dp(6), 0, dp(4));
        return field;
    }

    private EditText edit(LinearLayout parent, String value, int inputType) {
        EditText edit = new EditText(this);
        edit.setSingleLine(true);
        edit.setInputType(inputType);
        edit.setMinHeight(dp(44));
        edit.setTextSize(16);
        edit.setGravity(Gravity.CENTER_VERTICAL);
        edit.setPadding(dp(8), dp(4), dp(8), dp(4));
        edit.setBackground(surface(INSET, 7, LINE));
        edit.setTextColor(new ColorStateList(new int[][] {
                new int[] {-android.R.attr.state_enabled}, new int[] {}
        }, new int[] {MUTED, INK}));
        edit.setText(value);
        parent.addView(edit, parent.getOrientation() == LinearLayout.HORIZONTAL ? -2 : -1, -2);
        return edit;
    }

    private static void labelField(TextView label, View field) {
        field.setId(View.generateViewId());
        label.setLabelFor(field.getId());
    }

    private void message(String text) {
        Toast.makeText(this, text, Toast.LENGTH_LONG).show();
    }

    private GradientDrawable surface(int color, int radius, int border) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(color);
        drawable.setCornerRadius(dp(radius));
        if (border != 0) drawable.setStroke(dp(1), border);
        return drawable;
    }

    private RippleDrawable ripple(int color, int radius, int border) {
        int highlight = color == ACCENT ? 0x4010251D : 0x3079E3BA;
        return new RippleDrawable(ColorStateList.valueOf(highlight), surface(color, radius, border),
                surface(Color.WHITE, radius, 0));
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }

    private static String value(EditText field) {
        return field.getText().toString();
    }

    private static String bpmText(int number) {
        return number == 0 ? "—" : Integer.toString(number);
    }
}
