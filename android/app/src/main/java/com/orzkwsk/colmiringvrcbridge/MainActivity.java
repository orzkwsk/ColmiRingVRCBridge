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
import android.graphics.Paint;
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
import android.text.TextUtils;
import android.text.TextWatcher;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.view.inputmethod.InputMethodManager;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
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
    private static final int BG = 0xFFF4F2FA;
    private static final int CARD = 0xFFFCFBFF;
    static final int INSET = 0xFFF1EDF9;
    private static final int INK = 0xFF1C1830;
    static final int MUTED = 0xFF575269;
    static final int LINE = 0xFFCCC4E0;
    static final int ACCENT = 0xFF5940B7;
    private static final int CONNECTED_INK = 0xFF167246;
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

    private TextView connectedRing, connectionStatus, bpm, battery, charging, lastSample, minimum, maximum;
    private TextView batterySummary, deviceDetails, oscStatus, lastSent, status, addressPreview, endpointPreview, parameterSummary;
    private Button scan, connect, output, defaults, wifiPrefix;
    private Spinner devicePicker, type, scaling, loss;
    private EditText host, port, parameter, interval, fixedBpm, specified;
    private CheckBox useFixed, autoReconnect;
    private LinearLayout batteryHistory, specifiedRow;
    private TelemetryChart heartChart, batteryChart;
    private BridgeSettings.Output displayedSettings;

    private record Disclosure(LinearLayout container, LinearLayout body, TextView summary) {}

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

        TextView title = text(window, "ColmiRingVRCBridge", 14, INK);
        title.setPadding(dp(14), dp(7), dp(14), dp(7));
        title.setBackgroundColor(CARD);
        divider(window, 0);

        ScrollView scroll = new ScrollView(this);
        window.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(12), dp(12), dp(12), dp(12));
        scroll.addView(page);

        createRingPanel(page);
        createTelemetryPanel(page);
        BridgeSettings.Output settings = BridgeSettings.load(prefs);
        createVrcPanel(page, settings);
        createOscPanel(page, settings);
        Disclosure details = disclosure(page, "リング詳細", CARD, 0);
        deviceDetails = text(details.body(), "—", 12, MUTED);
        deviceDetails.setTextIsSelectable(true);

        divider(window, 0);
        LinearLayout footer = row(window);
        footer.setBackgroundColor(CARD);
        footer.setPadding(dp(14), dp(7), dp(14), dp(7));
        status = text(footer, "接続設定を読み込み中…", 12, MUTED);
        fillRow(status);
        text(footer, version(), 12, MUTED).setPadding(dp(12), 0, 0, 0);
        setContentView(window);

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

    private void createRingPanel(LinearLayout page) {
        LinearLayout ring = card(page, LinearLayout.HORIZONTAL);
        ring.setGravity(Gravity.TOP);
        LinearLayout device = column(ring, new LinearLayout.LayoutParams(0, -2, 1));
        device.setPadding(0, 0, dp(12), 0);
        connectedRing = text(device, "—", 20, INK);
        connectedRing.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        connectedRing.setSingleLine();
        connectedRing.setEllipsize(TextUtils.TruncateAt.END);
        connectedRing.setVisibility(View.GONE);
        devicePicker = spinner(device, "リングを選択", "リングを選択");
        connectionStatus = text(device, "未接続", 12, MUTED);
        connectionStatus.setPadding(0, dp(4), 0, 0);

        LinearLayout controls = column(ring, new LinearLayout.LayoutParams(-2, -2));
        controls.setGravity(Gravity.END);
        LinearLayout actions = row(controls);
        scan = button(actions, "検索", false);
        connect = button(actions, "接続", true);
        ((LinearLayout.LayoutParams) connect.getLayoutParams()).setMarginStart(dp(6));
        autoReconnect = check(controls, "自動再接続");
        alignEnd(autoReconnect, 6);
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

    private void createTelemetryPanel(LinearLayout page) {
        LinearLayout telemetry = card(page, LinearLayout.VERTICAL);
        LinearLayout vitals = row(telemetry);
        bpm = text(vitals, "— BPM", 41, INK);
        bpm.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        bpm.setMaxLines(1);
        bpm.setAutoSizeTextTypeUniformWithConfiguration(22, 41, 1, TypedValue.COMPLEX_UNIT_SP);
        fillRow(bpm);
        View separator = new View(this);
        separator.setBackgroundColor(LINE);
        LinearLayout.LayoutParams separatorParams = new LinearLayout.LayoutParams(dp(1), -1);
        separatorParams.setMargins(dp(6), 0, dp(12), 0);
        vitals.addView(separator, separatorParams);
        LinearLayout power = column(vitals, new LinearLayout.LayoutParams(-2, -2));
        battery = text(power, "—%", 19, INK);
        battery.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        charging = text(power, "Charging —", 12, MUTED);
        Button showBattery = button(power, "24 h history", false);
        showBattery.setTextColor(ACCENT);
        showBattery.setPaintFlags(showBattery.getPaintFlags() | Paint.UNDERLINE_TEXT_FLAG);
        showBattery.setPadding(0, 0, 0, 0);
        showBattery.setMinHeight(dp(24));
        showBattery.setBackground(ripple(Color.TRANSPARENT, 0, Color.TRANSPARENT));

        LinearLayout caption = row(telemetry);
        caption.setPadding(0, dp(7), 0, dp(4));
        fillRow(text(caption, "Last 60 s", 12, MUTED));
        lastSample = text(caption, "Last sample —", 12, MUTED);
        heartChart = chart(telemetry, false);
        LinearLayout extrema = row(telemetry);
        extrema.setPadding(0, dp(7), 0, 0);
        text(extrema, "Min", 12, MUTED).setPadding(0, 0, dp(5), 0);
        minimum = text(extrema, "— BPM", 14, INK);
        minimum.setPadding(0, 0, dp(15), 0);
        minimum.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        text(extrema, "Max", 12, MUTED).setPadding(0, 0, dp(5), 0);
        maximum = text(extrema, "— BPM", 14, INK);
        maximum.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        fillRow(maximum);
        Button reset = button(extrema, "Reset", false);
        reset.setMinHeight(dp(26));
        reset.setOnClickListener(view -> {
            if (bridge != null) bridge.resetHeartHistory();
        });

        batteryHistory = column(telemetry, new LinearLayout.LayoutParams(-1, -2));
        batteryHistory.setPadding(0, dp(12), 0, 0);
        text(batteryHistory, "Battery history · last 24 hours", 14, INK)
                .setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        batteryChart = chart(batteryHistory, true);
        batterySummary = text(batteryHistory, "", 12, MUTED);
        wireDisclosure(showBattery, batteryHistory, null);
    }

    private void createVrcPanel(LinearLayout page, BridgeSettings.Output settings) {
        LinearLayout vrc = card(page, LinearLayout.VERTICAL);
        LinearLayout head = row(vrc);
        TextView heading = text(head, "VRC", 20, INK);
        heading.setPadding(0, 0, dp(10), 0);
        heading.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        oscStatus = text(head, "送信停止中", 12, MUTED);
        oscStatus.setPadding(0, 0, dp(6), 0);
        fillRow(oscStatus);
        output = button(head, "送信開始", true);
        output.setMinWidth(dp(90));
        output.setOnClickListener(view -> {
            if (bridge != null && bridge.snapshot().output() != BridgeService.Output.IDLE) {
                bridge.stopOutput();
            } else {
                beginOutput();
            }
        });
        lastSent = text(vrc, "最終送信 —", 12, MUTED);
        lastSent.setPadding(0, dp(4), 0, 0);
        divider(vrc, 9);

        LinearLayout fixedRow = row(vrc);
        TextView fixedLabel = formLabel(fixedRow, "固定BPM");
        fixedBpm = edit(fixedRow, settings.fixedBpm(), InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_SIGNED);
        fixedBpm.setLayoutParams(new LinearLayout.LayoutParams(dp(58), -2));
        labelField(fixedLabel, fixedBpm);
        useFixed = check(fixedRow, "固定BPMを出力");
        useFixed.setChecked(settings.useFixed());
        useFixed.setPadding(dp(8), 0, 0, 0);
        fillRow(useFixed);

        LinearLayout lossRow = row(vrc);
        lossRow.setPadding(0, dp(7), 0, 0);
        TextView lossLabel = formLabel(lossRow, "心拍が途切れたとき");
        loss = spinner(lossRow, "心拍が途切れたとき", "最終値を保持", "固定BPMに自動切り替え", "指定値に固定");
        labelField(lossLabel, loss);
        fillRow(loss);
        loss.setSelection(settings.loss().ordinal(), false);

        specifiedRow = row(vrc);
        specifiedRow.setPadding(0, dp(7), 0, 0);
        TextView specifiedLabel = formLabel(specifiedRow, "途絶時の指定値");
        specified = edit(specifiedRow, settings.specified(), PLAIN_INPUT);
        specified.setLayoutParams(new LinearLayout.LayoutParams(dp(82), -2));
        labelField(specifiedLabel, specified);

        Disclosure params = disclosure(vrc, "パラメーター設定", INSET, 10);
        parameterSummary = params.summary();
        LinearLayout body = params.body();
        parameter = input(body, "パラメーター名", settings.parameter(), PLAIN_INPUT);
        addressPreview = text(body, settings.address(), 12, MUTED);
        addressPreview.setTypeface(Typeface.MONOSPACE);
        addressPreview.setPadding(0, dp(4), 0, 0);
        LinearLayout fields = row(body);
        fields.setGravity(Gravity.TOP);
        fields.setPadding(0, dp(7), 0, 0);
        LinearLayout types = column(fields, new LinearLayout.LayoutParams(0, -2, 1));
        TextView typeLabel = text(types, "型", 14, INK);
        type = spinner(types, "型", "Float", "Int");
        labelField(typeLabel, type);
        type.setSelection(settings.floating() ? 0 : 1, false);
        LinearLayout scales = column(fields, new LinearLayout.LayoutParams(0, -2, 1));
        scales.setPadding(dp(8), 0, 0, 0);
        TextView scaleLabel = text(scales, "スケーリング", 14, INK);
        scaling = spinner(scales, "スケーリング", "BPM ÷ 255", "Raw BPM");
        labelField(scaleLabel, scaling);
        scaling.setSelection(settings.normalize() ? 0 : 1, false);
        LinearLayout timing = row(body);
        timing.setPadding(0, dp(7), 0, 0);
        TextView intervalLabel = text(timing, "送信間隔", 14, INK);
        intervalLabel.setPadding(0, 0, dp(8), 0);
        interval = edit(timing, settings.interval(), PLAIN_INPUT);
        interval.setLayoutParams(new LinearLayout.LayoutParams(dp(70), -2));
        labelField(intervalLabel, interval);
        text(timing, "秒", 14, INK).setPadding(dp(5), 0, 0, 0);
    }

    private void createOscPanel(LinearLayout page, BridgeSettings.Output settings) {
        Disclosure target = disclosure(page, "OSC", CARD, 0);
        ((LinearLayout.LayoutParams) target.container().getLayoutParams()).bottomMargin = dp(9);
        endpointPreview = target.summary();
        LinearLayout address = row(target.body());
        address.setGravity(Gravity.TOP);
        LinearLayout hostColumn = column(address, new LinearLayout.LayoutParams(0, -2, 1));
        hostColumn.setPadding(0, 0, dp(8), 0);
        TextView hostLabel = text(hostColumn, "IPアドレス", 14, INK);
        LinearLayout entry = row(hostColumn);
        host = edit(entry, settings.host(), InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        labelField(hostLabel, host);
        fillRow(host);
        wifiPrefix = button(entry, "Wi-Fiから補完", false);
        ((LinearLayout.LayoutParams) wifiPrefix.getLayoutParams()).setMarginStart(dp(6));
        wifiPrefix.setOnClickListener(view -> fillWifiPrefix());
        entry.addOnLayoutChangeListener((view, left, top, right, bottom, oldLeft, oldTop, oldRight, oldBottom) -> {
            boolean stack = right - left < dp(240);
            int orientation = stack ? LinearLayout.VERTICAL : LinearLayout.HORIZONTAL;
            if (entry.getOrientation() == orientation) return;
            entry.setOrientation(orientation);
            host.setLayoutParams(stack ? new LinearLayout.LayoutParams(-1, -2) : new LinearLayout.LayoutParams(0, -2, 1));
            LinearLayout.LayoutParams buttonParams = new LinearLayout.LayoutParams(-2, -2);
            buttonParams.gravity = Gravity.START;
            if (stack) buttonParams.topMargin = dp(3);
            else buttonParams.setMarginStart(dp(6));
            wifiPrefix.setLayoutParams(buttonParams);
        });
        LinearLayout portColumn = column(address, new LinearLayout.LayoutParams(dp(105), -2));
        port = input(portColumn, "ポート", settings.port(), InputType.TYPE_CLASS_NUMBER);

        defaults = button(target.body(), "初期値に戻す", false);
        alignEnd(defaults, 8);
        defaults.setOnClickListener(view -> {
            host.setText(BridgeSettings.DEFAULT_HOST);
            port.setText(BridgeSettings.DEFAULT_PORT);
        });
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
        boolean floating = type.getSelectedItemPosition() == 0;
        return new BridgeSettings.Output(value(host), value(port), value(parameter), floating,
                floating && scaling.getSelectedItemPosition() == 0, value(interval), useFixed.isChecked(),
                value(fixedBpm), BridgeSettings.Loss.values()[Math.max(0, loss.getSelectedItemPosition())],
                value(specified));
    }

    private void settingsChanged() {
        BridgeSettings.Output settings = readSettings();
        if (!settings.floating() && scaling.getSelectedItemPosition() != 1) {
            scaling.setSelection(1);
        }
        updateSettingLabels(settings);
        BridgeSettings.save(prefs, settings);
        if (bridge != null) bridge.updateValues();
        render();
    }

    private void updateSettingLabels(BridgeSettings.Output settings) {
        displayedSettings = settings;
        addressPreview.setText(settings.address());
        parameterSummary.setText(getString(R.string.parameter_summary, settings.parameter(),
                settings.floating() ? "Float" : "Int"));
        endpointPreview.setText(getString(R.string.osc_endpoint, settings.host(), settings.port()));
        specifiedRow.setVisibility(settings.loss() == BridgeSettings.Loss.SPECIFIED ? View.VISIBLE : View.GONE);
    }

    private void render() {
        if (bridge == null) {
            scan.setEnabled(false);
            connect.setEnabled(false);
            output.setEnabled(false);
            autoReconnect.setEnabled(false);
            wifiPrefix.setEnabled(false);
            return;
        }
        BridgeService.Snapshot state = bridge.snapshot();
        updateCandidates(state.candidates());
        long now = SystemClock.elapsedRealtime();
        boolean connected = state.link() == BridgeService.Link.CONNECTED;
        boolean canSelect = state.link() == BridgeService.Link.IDLE || state.link() == BridgeService.Link.RECONNECTING;
        boolean canEditEndpoint = state.output() == BridgeService.Output.IDLE;
        rendering = true;
        autoReconnect.setChecked(state.autoReconnect());
        rendering = false;
        autoReconnect.setEnabled(true);
        scan.setEnabled(canSelect);
        devicePicker.setVisibility(connected ? View.GONE : View.VISIBLE);
        devicePicker.setEnabled(canSelect);
        connectedRing.setVisibility(connected ? View.VISIBLE : View.GONE);
        connectedRing.setText(state.ring() == null ? "—" : state.ring().name());
        connect.setText(connected ? "切断" : "接続");
        connect.setEnabled(connected || canSelect && devicePicker.getSelectedItemPosition() > 0);

        boolean fresh = state.heartAt() != 0 && now - state.heartAt() <= BridgeService.HEART_FRESH_MS;
        connectionStatus.setText(connectionText(state, fresh));
        connectionStatus.setTextColor(connected && fresh ? CONNECTED_INK : MUTED);
        status.setText(state.oscError() == null ? state.ringStatus() : state.oscError());
        bpm.setText(getString(R.string.heart_rate, bpmText(state.bpm())));
        String percent = state.battery() < 0 ? "—%" : getString(R.string.battery_percent, state.battery());
        String charge = getString(R.string.battery_charging, state.battery() < 0 ? "—" : state.charging() ? "Yes" : "No");
        boolean staleBattery = state.battery() >= 0 && now - state.batteryAt() > 180_000;
        battery.setText(staleBattery ? getString(R.string.stale_value, percent) : percent);
        charging.setText(staleBattery ? getString(R.string.stale_value, charge) : charge);
        String lastTime = state.heartWallTime() == 0 ? "—" : clock.format(new Date(state.heartWallTime()));
        lastSample.setText(getString(R.string.heart_received_at, lastTime));
        minimum.setText(getString(R.string.heart_rate, bpmText(state.min())));
        maximum.setText(getString(R.string.heart_rate, bpmText(state.max())));
        heartChart.show(state.heartHistory(), now);
        if (batteryHistory.getVisibility() == View.VISIBLE) {
            batteryChart.show(state.batteryHistory(), now);
            renderBatterySummary(state.batteryHistory(), now);
        }
        String deviceAddress = state.ring() == null ? "—" : state.ring().address();
        deviceDetails.setText(getString(R.string.ring_details, deviceAddress, state.deviceInfo(), state.diagnostics()));

        oscStatus.setText(outputText(state, fresh));
        lastSent.setText(getString(R.string.last_output, state.lastOutput().isEmpty() ? "—" : state.lastOutput()));
        output.setText(canEditEndpoint ? "送信開始" : "送信停止");
        output.setEnabled(state.output() != BridgeService.Output.STOPPING);
        for (View field : new View[] {host, port, parameter, interval, type, defaults, wifiPrefix}) {
            field.setEnabled(canEditEndpoint);
        }
        scaling.setEnabled(canEditEndpoint && displayedSettings.floating());
    }

    private String connectionText(BridgeService.Snapshot state, boolean fresh) {
        return switch (state.link()) {
            case CONNECTING -> "接続中";
            case RECONNECTING -> "再接続中";
            case DISCONNECTING -> "切断中";
            case CONNECTED -> fresh ? "接続済み" : state.heartAt() == 0 ? "接続済み・心拍待ち" : "接続済み・心拍更新停止";
            default -> "未接続";
        };
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
        batterySummary.setText(getString(R.string.battery_history_summary, initial, current, delta, history.size() - first));
    }

    private void updateCandidates(List<BridgeService.Candidate> candidates) {
        if (shownCandidates.equals(candidates)) return;
        int previous = devicePicker.getSelectedItemPosition() - 1;
        if (previous >= 0 && previous < shownCandidates.size()) {
            selectedAddress = shownCandidates.get(previous).address();
        }
        shownCandidates = candidates;
        List<String> labels = new ArrayList<>();
        labels.add("リングを選択");
        int index = candidates.isEmpty() ? 0 : 1;
        for (BridgeService.Candidate candidate : candidates) {
            labels.add(candidate.label());
            if (candidate.address().equals(selectedAddress)) index = labels.size() - 1;
        }
        devicePicker.setAdapter(adapter(labels));
        devicePicker.setSelection(index, false);
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

    private LinearLayout card(LinearLayout parent, int orientation) {
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(orientation);
        panel.setPadding(dp(14), dp(12), dp(14), dp(12));
        panel.setBackground(surface(CARD, 10, LINE));
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.bottomMargin = dp(9);
        parent.addView(panel, params);
        return panel;
    }

    private Disclosure disclosure(LinearLayout parent, String label, int color, int topMargin) {
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, -2);
        params.topMargin = dp(topMargin);
        LinearLayout container = column(parent, params);
        container.setBackground(surface(color, 8, LINE));
        LinearLayout head = row(container);
        head.setMinimumHeight(dp(36));
        head.setPadding(dp(11), dp(4), dp(11), dp(4));
        head.setFocusable(true);
        head.setBackground(ripple(Color.TRANSPARENT, 7, 0));
        TextView heading = text(head, label, 14, INK);
        heading.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        fillRow(heading);
        TextView summary = text(head, "", 12, MUTED);
        summary.setSingleLine();
        summary.setEllipsize(TextUtils.TruncateAt.END);
        summary.setMaxWidth(dp(146));
        summary.setPadding(dp(6), 0, 0, 0);
        TextView chevron = text(head, "⌄", 16, MUTED);
        chevron.setGravity(Gravity.CENTER);
        chevron.setLayoutParams(new LinearLayout.LayoutParams(dp(22), -2));
        chevron.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        LinearLayout body = column(container, new LinearLayout.LayoutParams(-1, -2));
        body.setPadding(dp(11), 0, dp(11), dp(11));
        wireDisclosure(head, body, chevron);
        return new Disclosure(container, body, summary);
    }

    private void wireDisclosure(View header, LinearLayout body, TextView chevron) {
        body.setVisibility(View.GONE);
        if (Build.VERSION.SDK_INT >= 30) header.setStateDescription("折りたたみ");
        header.setOnClickListener(view -> {
            boolean show = body.getVisibility() != View.VISIBLE;
            body.setVisibility(show ? View.VISIBLE : View.GONE);
            if (chevron != null) chevron.setText(show ? "⌃" : "⌄");
            if (Build.VERSION.SDK_INT >= 30) header.setStateDescription(show ? "展開" : "折りたたみ");
            render();
        });
    }

    private void divider(LinearLayout parent, int verticalMargin) {
        View line = new View(this);
        line.setBackgroundColor(LINE);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(-1, dp(1));
        params.topMargin = params.bottomMargin = dp(verticalMargin);
        parent.addView(line, params);
    }

    private TelemetryChart chart(LinearLayout parent, boolean batteryType) {
        TelemetryChart chart = new TelemetryChart(this, batteryType);
        parent.addView(chart, new LinearLayout.LayoutParams(-1, dp(batteryType ? 110 : 94)));
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

    private void alignEnd(View view, int topMargin) {
        LinearLayout.LayoutParams params = (LinearLayout.LayoutParams) view.getLayoutParams();
        params.gravity = Gravity.END;
        params.topMargin = dp(topMargin);
        view.setLayoutParams(params);
    }

    private TextView formLabel(LinearLayout parent, String label) {
        TextView view = text(parent, label, 14, INK);
        view.setLayoutParams(new LinearLayout.LayoutParams(dp(135), -2));
        view.setPadding(0, 0, dp(6), 0);
        return view;
    }

    private TextView text(LinearLayout parent, String content, int size, int color) {
        TextView text = new TextView(this);
        text.setText(content);
        text.setTextSize(size);
        text.setTextColor(color);
        parent.addView(text, parent.getOrientation() == LinearLayout.HORIZONTAL ? -2 : -1, -2);
        return text;
    }

    private Button button(LinearLayout parent, String label, boolean primary) {
        Button button = new Button(this);
        button.setText(label);
        button.setAllCaps(false);
        button.setTextSize(14);
        button.setLetterSpacing(0);
        button.setMinimumWidth(0);
        button.setMinWidth(0);
        button.setMinimumHeight(0);
        button.setMinHeight(dp(32));
        button.setPadding(dp(12), dp(4), dp(12), dp(4));
        button.setStateListAnimator(null);
        StateListDrawable background = new StateListDrawable();
        background.addState(new int[] {-android.R.attr.state_enabled}, surface(CARD, 6, LINE));
        background.addState(new int[] {}, ripple(primary ? ACCENT : INSET, 6, primary ? ACCENT : LINE));
        button.setBackground(background);
        button.setTextColor(new ColorStateList(new int[][] {
                new int[] {-android.R.attr.state_enabled}, new int[] {}
        }, new int[] {0xFF817991, primary ? Color.WHITE : INK}));
        if (primary) button.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        parent.addView(button, -2, -2);
        return button;
    }

    private CheckBox check(LinearLayout parent, String label) {
        CheckBox view = new CheckBox(this);
        view.setText(label);
        view.setTextColor(INK);
        view.setTextSize(14);
        parent.addView(view, -2, -2);
        return view;
    }

    private Spinner spinner(LinearLayout parent, String label, String... options) {
        Spinner spinner = new Spinner(this);
        spinner.setContentDescription(label);
        spinner.setMinimumHeight(dp(30));
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
        TextView fieldLabel = text(parent, label, 14, INK);
        fieldLabel.setPadding(0, dp(5), 0, dp(3));
        EditText field = edit(parent, value, inputType);
        labelField(fieldLabel, field);
        return field;
    }

    private EditText edit(LinearLayout parent, String value, int inputType) {
        EditText edit = new EditText(this);
        edit.setSingleLine(true);
        edit.setInputType(inputType);
        edit.setMinHeight(dp(30));
        edit.setTextSize(14);
        edit.setGravity(Gravity.CENTER_VERTICAL);
        edit.setPadding(dp(6), dp(2), dp(6), dp(2));
        edit.setBackground(surface(Color.WHITE, 4, LINE));
        edit.setTextColor(INK);
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
        int highlight = color == ACCENT ? 0x40FFFFFF : 0x305940B7;
        return new RippleDrawable(ColorStateList.valueOf(highlight), surface(color, radius, border),
                surface(Color.WHITE, radius, 0));
    }

    @SuppressWarnings("deprecation") // This flag-free lookup also works on API 29–32.
    private String version() {
        try {
            String name = getPackageManager().getPackageInfo(getPackageName(), 0).versionName;
            return getString(R.string.app_version, name);
        } catch (PackageManager.NameNotFoundException ex) {
            return "";
        }
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
