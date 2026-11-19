package com.orzkwsk.colmiringvrcbridge;

import android.Manifest;
import android.app.Activity;
import android.content.ComponentName;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.graphics.Insets;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.SystemClock;
import android.text.InputType;
import android.text.TextWatcher;
import android.view.View;
import android.view.WindowInsets;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import java.text.DateFormat;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Date;
import java.util.List;

public final class MainActivity extends Activity {
    private static final int BG = 0xFF11171C;
    private static final int CARD = 0xFF1C272F;
    private static final int INSET = 0xFF141D24;
    private static final int INK = 0xFFF0F6F8;
    private static final int MUTED = 0xFFADC2CE;
    private static final int ACCENT = 0xFF78E6C5;
    private static final int PERMISSION_BLE = 11;
    private static final int PERMISSION_NOTIFICATION = 12;

    private final Handler ui = new Handler(Looper.getMainLooper());
    private final DateFormat clock = DateFormat.getTimeInstance(DateFormat.MEDIUM);
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

    private TextView connectionStatus, bpm, battery, lastSample, extrema, deviceDetails;
    private TextView oscStatus, lastSent, addressPreview, endpointPreview, specifiedLabel;
    private Button scan, connect, output, test, stop, defaults;
    private Spinner devicePicker, type, scaling, loss;
    private EditText host, port, parameter, interval, fixedBpm, specified;
    private CheckBox allDevices, useFixed;
    private Switch autoReconnect;
    private TelemetryChart heartChart, batteryChart;

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
        ScrollView scroll = new ScrollView(this);
        scroll.setBackgroundColor(BG);
        scroll.setFillViewport(true);
        scroll.setOnApplyWindowInsetsListener((view, insets) -> applyInsets(view, insets));
        LinearLayout page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(16), dp(16), dp(16), dp(16));
        page.setFocusableInTouchMode(true);
        scroll.addView(page);
        setContentView(scroll);

        TextView title = text(page, "ColmiRing VRC Bridge", 25, INK);
        title.setTypeface(Typeface.DEFAULT, Typeface.BOLD);

        LinearLayout ring = card(page, "リング接続");
        connectionStatus = text(ring, "未接続", 15, MUTED);
        devicePicker = spinner(ring, "リングを選択", "リングを選択");
        LinearLayout ringActions = row(ring);
        scan = button(ringActions, "検索", false);
        connect = button(ringActions, "接続", true);
        allDevices = check(ring, "すべての BLE デバイスを表示");
        autoReconnect = new Switch(this);
        autoReconnect.setText("自動再接続");
        autoReconnect.setMinHeight(dp(48));
        autoReconnect.setTextColor(INK);
        ring.addView(autoReconnect, -1, -2);
        scan.setOnClickListener(view -> launch(new Intent(this, BridgeService.class).setAction(BridgeService.SCAN)
                .putExtra("all", allDevices.isChecked())));
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

        LinearLayout telemetry = card(page, "心拍とバッテリー");
        bpm = text(telemetry, "— BPM", 43, ACCENT);
        bpm.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        battery = text(telemetry, "バッテリー —%", 16, INK);
        lastSample = text(telemetry, "最終受信 —", 13, MUTED);
        heartChart = chart(telemetry, false);
        extrema = text(telemetry, "最小 —  ·  最大 —", 14, MUTED);
        button(telemetry, "心拍履歴をリセット", false).setOnClickListener(view -> {
            if (bridge != null) bridge.resetHeartHistory();
        });
        batteryChart = chart(disclosure(telemetry, "バッテリー履歴 · 24時間"), true);

        LinearLayout outputPanel = card(page, "VRChat OSC");
        oscStatus = text(outputPanel, "停止中", 15, MUTED);
        lastSent = text(outputPanel, "", 13, MUTED);
        LinearLayout oscActions = row(outputPanel);
        output = button(oscActions, "送信開始", true);
        test = button(oscActions, "経路テスト", false);
        output.setOnClickListener(view -> {
            if (bridge != null && bridge.snapshot().output() != BridgeService.Output.IDLE) {
                bridge.stopOutput();
            } else {
                beginOutput(BridgeService.OUTPUT);
            }
        });
        test.setOnClickListener(view -> beginOutput(BridgeService.TEST));

        BridgeSettings.Output settings = BridgeSettings.load(prefs);
        useFixed = check(outputPanel, "固定BPMを出力");
        useFixed.setChecked(settings.useFixed());
        fixedBpm = input(outputPanel, "固定BPM", settings.fixedBpm(), InputType.TYPE_CLASS_NUMBER | InputType.TYPE_NUMBER_FLAG_SIGNED);
        text(outputPanel, "心拍が途切れたとき", 13, MUTED);
        loss = spinner(outputPanel, "心拍が途切れたとき", "最終値を保持", "固定BPMに自動切り替え", "指定値に固定");
        loss.setSelection(settings.loss().ordinal(), false);
        specifiedLabel = text(outputPanel, "途絶時の指定値", 13, MUTED);
        specified = edit(outputPanel, settings.specified(), InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        labelField(specifiedLabel, specified);

        LinearLayout params = disclosure(outputPanel, "パラメーター設定");
        parameter = input(params, "パラメーター名", settings.parameter(), InputType.TYPE_CLASS_TEXT);
        addressPreview = text(params, settings.address(), 13, MUTED);
        text(params, "OSC の型", 13, MUTED);
        type = spinner(params, "OSC の型", "Int", "Float");
        type.setSelection(settings.floating() ? 1 : 0, false);
        text(params, "スケーリング", 13, MUTED);
        scaling = spinner(params, "スケーリング", "Raw BPM", "BPM ÷ 255");
        scaling.setSelection(settings.normalize() ? 1 : 0, false);
        interval = input(params, "送信間隔（秒）", settings.interval(),
                InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);

        LinearLayout target = disclosure(outputPanel, "送信先");
        endpointPreview = text(target, "", 14, MUTED);
        host = input(target, "IP アドレス / ホスト名", settings.host(), InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_URI);
        port = input(target, "UDP ポート", settings.port(), InputType.TYPE_CLASS_NUMBER);
        defaults = button(target, "送信先を初期値に戻す", false);
        defaults.setOnClickListener(view -> {
            host.setText(BridgeSettings.DEFAULT_HOST);
            port.setText(BridgeSettings.DEFAULT_PORT);
        });

        LinearLayout details = disclosure(page, "リング詳細");
        deviceDetails = text(details, "—", 13, MUTED);
        deviceDetails.setTextIsSelectable(true);
        stop = button(page, "ブリッジを停止", false);
        stop.setOnClickListener(view -> {
            if (bridge != null) bridge.stopBridge();
        });

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

    private void toggleLink() {
        if (bridge == null) return;
        BridgeService.Snapshot current = bridge.snapshot();
        if (current.link() != BridgeService.Link.IDLE) {
            bridge.disconnect();
            return;
        }
        int index = devicePicker.getSelectedItemPosition() - 1;
        if (index >= 0 && index < shownCandidates.size()) {
            launch(new Intent(this, BridgeService.class).setAction(BridgeService.CONNECT)
                    .putExtra("address", shownCandidates.get(index).address()));
        }
    }

    private void beginOutput(String action) {
        try {
            BridgeSettings.Output settings = readSettings();
            settings.endpoint(BridgeService.TEST.equals(action));
            BridgeSettings.save(prefs, settings);
            launch(action);
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
        addressPreview.setText(settings.address());
        endpointPreview.setText(getString(R.string.osc_endpoint, settings.host(), settings.port()));
        int visibility = settings.loss() == BridgeSettings.Loss.SPECIFIED ? View.VISIBLE : View.GONE;
        specified.setVisibility(visibility);
        specifiedLabel.setVisibility(visibility);
    }

    private void render() {
        if (bridge == null) {
            for (Button button : new Button[] {scan, connect, output, test, stop}) {
                button.setEnabled(false);
            }
            autoReconnect.setEnabled(false);
            return;
        }
        BridgeService.Snapshot state = bridge.snapshot();
        updateCandidates(state.candidates());
        long now = SystemClock.elapsedRealtime();
        boolean idle = state.link() == BridgeService.Link.IDLE;
        boolean connected = state.link() == BridgeService.Link.CONNECTED;
        boolean outputIdle = state.output() == BridgeService.Output.IDLE;
        boolean ringBusy = state.link() == BridgeService.Link.DISCONNECTING;
        boolean canEditEndpoint = outputIdle && !state.stopping();
        rendering = true;
        autoReconnect.setChecked(state.autoReconnect());
        rendering = false;
        autoReconnect.setEnabled(!state.stopping());
        scan.setEnabled(idle && !state.stopping());
        allDevices.setEnabled(idle && !state.stopping());
        devicePicker.setEnabled(idle && !state.stopping());
        connect.setText(idle ? "接続" : state.link() == BridgeService.Link.SCANNING ? "検索停止" : "切断");
        connect.setEnabled(!state.stopping() && !ringBusy && (!idle || devicePicker.getSelectedItemPosition() > 0));

        String ringName = state.ring() == null ? "" : state.ring().name() + "  ·  ";
        String heartState = connected && state.heartAt() == 0 ? "・心拍待ち"
                : connected && now - state.heartAt() > 5_000 ? "・心拍更新停止" : "";
        connectionStatus.setText(getString(R.string.connection_status, ringName, state.ringStatus(), heartState));
        bpm.setText(getString(R.string.heart_rate, bpmText(state.bpm())));
        battery.setText(state.battery() < 0 ? "バッテリー —%"
                : "バッテリー " + state.battery() + "%" + (state.charging() ? "  ·  充電中" : "")
                + (now - state.batteryAt() > 180_000 ? "  ·  更新停止" : ""));
        String lastTime = state.heartWallTime() == 0 ? "—" : clock.format(new Date(state.heartWallTime()));
        lastSample.setText(getString(R.string.heart_received_at, lastTime));
        extrema.setText(getString(R.string.heart_extrema, bpmText(state.min()), bpmText(state.max())));
        heartChart.show(state.heartHistory(), now);
        if (batteryChart.getParent() instanceof View chartPanel && chartPanel.getVisibility() == View.VISIBLE) {
            batteryChart.show(state.batteryHistory(), now);
        }
        String deviceAddress = state.ring() == null ? "" : "Bluetooth ID " + state.ring().address() + "\n";
        String uuidDetails = connected ? "\nUART " + RingProtocol.UART + "\nRX " + RingProtocol.RX
                + "\nTX " + RingProtocol.TX : "";
        deviceDetails.setText(getString(R.string.ring_details, deviceAddress, state.deviceInfo(),
                uuidDetails, state.diagnostics()));

        oscStatus.setText(state.oscStatus());
        lastSent.setText(state.lastOutput());
        lastSent.setVisibility(state.lastOutput().isEmpty() ? View.GONE : View.VISIBLE);
        output.setText(outputIdle ? "送信開始" : "送信停止");
        output.setEnabled(!state.stopping() && state.output() != BridgeService.Output.STOPPING);
        test.setEnabled(canEditEndpoint);
        stop.setEnabled(!state.stopping() && (!idle || !outputIdle || state.autoReconnect() && prefs.contains("lastAddress")));
        for (View field : new View[] {host, port, parameter, interval, type, defaults}) {
            field.setEnabled(canEditEndpoint);
        }
        scaling.setEnabled(canEditEndpoint && type.getSelectedItemPosition() == 1);
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

    private LinearLayout card(LinearLayout parent, String heading) {
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(14), dp(12), dp(14), dp(12));
        panel.setBackground(surface(CARD, 14));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, -2);
        lp.topMargin = dp(12);
        parent.addView(panel, lp);
        TextView label = text(panel, heading, 19, INK);
        label.setTypeface(Typeface.DEFAULT, Typeface.BOLD);
        return panel;
    }

    private LinearLayout disclosure(LinearLayout parent, String label) {
        Button header = button(parent, getString(R.string.show_section, label), false);
        LinearLayout panel = new LinearLayout(this);
        panel.setOrientation(LinearLayout.VERTICAL);
        panel.setPadding(dp(8), dp(4), dp(8), dp(4));
        panel.setVisibility(View.GONE);
        parent.addView(panel, -1, -2);
        header.setOnClickListener(view -> {
            boolean show = panel.getVisibility() != View.VISIBLE;
            panel.setVisibility(show ? View.VISIBLE : View.GONE);
            header.setText(getString(show ? R.string.hide_section : R.string.show_section, label));
            render();
        });
        return panel;
    }

    private TelemetryChart chart(LinearLayout parent, boolean batteryType) {
        TelemetryChart chart = new TelemetryChart(this, batteryType);
        chart.setBackground(surface(INSET, 8));
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, dp(batteryType ? 126 : 112));
        lp.topMargin = dp(8);
        parent.addView(chart, lp);
        return chart;
    }

    private LinearLayout row(LinearLayout parent) {
        LinearLayout row = new LinearLayout(this);
        parent.addView(row, -1, -2);
        return row;
    }

    private TextView text(LinearLayout parent, String content, int size, int color) {
        TextView text = new TextView(this);
        text.setText(content);
        text.setTextSize(size);
        text.setTextColor(color);
        text.setPadding(0, dp(3), 0, dp(3));
        parent.addView(text, -1, -2);
        return text;
    }

    private Button button(LinearLayout parent, String label, boolean primary) {
        Button button = new Button(this);
        button.setText(label);
        button.setAllCaps(false);
        button.setMinHeight(dp(48));
        button.setBackgroundTintList(new ColorStateList(new int[][] {
                new int[] {-android.R.attr.state_enabled}, new int[] {}
        }, new int[] {0xFF29363D, primary ? ACCENT : 0xFF33464F}));
        button.setTextColor(new ColorStateList(new int[][] {
                new int[] {-android.R.attr.state_enabled}, new int[] {}
        }, new int[] {0xFF91A4AE, primary ? BG : INK}));
        LinearLayout.LayoutParams lp = parent.getOrientation() == LinearLayout.HORIZONTAL
                ? new LinearLayout.LayoutParams(0, -2, 1) : new LinearLayout.LayoutParams(-1, -2);
        parent.addView(button, lp);
        return button;
    }

    private CheckBox check(LinearLayout parent, String label) {
        CheckBox view = new CheckBox(this);
        view.setText(label);
        view.setTextColor(INK);
        view.setMinHeight(dp(48));
        parent.addView(view, -1, -2);
        return view;
    }

    private Spinner spinner(LinearLayout parent, String label, String... options) {
        Spinner spinner = new Spinner(this);
        spinner.setContentDescription(label);
        spinner.setMinimumHeight(dp(48));
        spinner.setAdapter(adapter(java.util.Arrays.asList(options)));
        parent.addView(spinner, -1, -2);
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
        TextView fieldLabel = text(parent, label, 13, MUTED);
        EditText field = edit(parent, value, inputType);
        labelField(fieldLabel, field);
        return field;
    }

    private EditText edit(LinearLayout parent, String value, int inputType) {
        EditText edit = new EditText(this);
        edit.setSingleLine(true);
        edit.setInputType(inputType);
        edit.setMinHeight(dp(48));
        edit.setTextSize(16);
        edit.setTextColor(INK);
        edit.setText(value);
        parent.addView(edit, -1, -2);
        return edit;
    }

    private static void labelField(TextView label, EditText field) {
        field.setId(View.generateViewId());
        label.setLabelFor(field.getId());
    }

    private void message(String text) {
        Toast.makeText(this, text, Toast.LENGTH_LONG).show();
    }

    private static GradientDrawable surface(int color, float radius) {
        GradientDrawable drawable = new GradientDrawable();
        drawable.setColor(color);
        drawable.setCornerRadius(radius);
        return drawable;
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
