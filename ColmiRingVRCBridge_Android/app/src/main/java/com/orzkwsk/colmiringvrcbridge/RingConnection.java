package com.orzkwsk.colmiringvrcbridge;

import android.annotation.SuppressLint;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothGatt;
import android.bluetooth.BluetoothGattCallback;
import android.bluetooth.BluetoothGattCharacteristic;
import android.bluetooth.BluetoothGattDescriptor;
import android.bluetooth.BluetoothGattService;
import android.bluetooth.BluetoothProfile;
import android.bluetooth.BluetoothStatusCodes;
import android.content.Context;
import android.os.Build;
import android.os.Handler;

import java.nio.charset.StandardCharsets;
import java.util.ArrayDeque;
import java.util.UUID;
import java.util.function.BiConsumer;
import java.util.function.BooleanSupplier;

@SuppressLint("MissingPermission") // BridgeService checks Bluetooth permission before starting work.
final class RingConnection {
    interface Listener {
        void onStage(String text);
        void onReady();
        void onData(byte[] data);
        void onDeviceInfo(UUID uuid, String value);
        void onPoll(boolean battery, int status);
        void onClosed(String reason, boolean manual);
    }

    private enum Operation { DISCOVER, SUBSCRIBE, WRITE, READ }

    private static final long OPERATION_TIMEOUT = 3_000;
    private static final long CONNECTION_TIMEOUT = 15_000;

    private final Context context;
    private final Handler worker;
    private final Listener listener;
    private final ArrayDeque<OperationRequest> details = new ArrayDeque<>();
    private final Runnable operationTimeout = () -> fail("GATT 操作がタイムアウトしました");
    private final Runnable connectTimeout = () -> fail("リングの接続がタイムアウトしました");
    private final Runnable closeTimeout = this::completeShutdown;
    private final Runnable poll = this::poll;
    private final Runnable continueMeasurement = this::continueMeasurement;

    private BluetoothGatt gatt;
    private BluetoothGattCharacteristic rx;
    private OperationRequest pending;
    private int writeType;
    private boolean closed;
    private boolean stopping;
    private boolean manualStop;
    private String stopReason;
    private boolean measurementStarted;
    private boolean ready;
    private long nextHeartRatePoll;
    private long nextBatteryPoll;

    private record OperationRequest(Operation kind, UUID uuid, BooleanSupplier initiate,
                                    BiConsumer<Integer, byte[]> complete) {}

    RingConnection(Context context, Handler worker, Listener listener) {
        this.context = context;
        this.worker = worker;
        this.listener = listener;
    }

    void connect(BluetoothDevice device) {
        worker.postDelayed(connectTimeout, CONNECTION_TIMEOUT);
        try {
            gatt = device.connectGatt(context, false, callback, BluetoothDevice.TRANSPORT_LE);
            if (gatt == null) {
                fail("GATT 接続を開始できませんでした");
            }
        } catch (SecurityException | IllegalArgumentException ex) {
            fail("Bluetooth の状態または権限を確認してください");
        }
    }

    private final BluetoothGattCallback callback = new BluetoothGattCallback() {
        @Override
        public void onConnectionStateChange(BluetoothGatt source, int status, int state) {
            worker.post(() -> {
                if (source != gatt || closed) {
                    return;
                }
                if (status != BluetoothGatt.GATT_SUCCESS || state == BluetoothProfile.STATE_DISCONNECTED) {
                    fail("BLE 接続が切れました (GATT " + status + ")");
                } else if (state == BluetoothProfile.STATE_CONNECTED && pending == null && !ready && !stopping) {
                    listener.onStage("GATT サービスを検索中…");
                    begin(new OperationRequest(Operation.DISCOVER, null, () -> gatt.discoverServices(),
                            (result, value) -> onServices(result)));
                }
            });
        }

        @Override
        public void onServicesDiscovered(BluetoothGatt source, int status) {
            worker.post(() -> finish(source, Operation.DISCOVER, null, status, null));
        }

        @Override
        public void onDescriptorWrite(BluetoothGatt source, BluetoothGattDescriptor descriptor, int status) {
            worker.post(() -> finish(source, Operation.SUBSCRIBE, descriptor.getUuid(), status, null));
        }

        @Override
        public void onCharacteristicWrite(BluetoothGatt source, BluetoothGattCharacteristic characteristic, int status) {
            worker.post(() -> finish(source, Operation.WRITE, characteristic.getUuid(), status, null));
        }

        @Override
        public void onCharacteristicRead(BluetoothGatt source, BluetoothGattCharacteristic characteristic,
                                         byte[] value, int status) {
            postRead(source, characteristic, value, status);
        }

        @Override
        @SuppressWarnings("deprecation")
        public void onCharacteristicRead(BluetoothGatt source, BluetoothGattCharacteristic characteristic, int status) {
            if (Build.VERSION.SDK_INT < 33) {
                postRead(source, characteristic, characteristic.getValue(), status);
            }
        }

        @Override
        public void onCharacteristicChanged(BluetoothGatt source, BluetoothGattCharacteristic characteristic, byte[] value) {
            postData(source, characteristic, value);
        }

        @Override
        @SuppressWarnings("deprecation")
        public void onCharacteristicChanged(BluetoothGatt source, BluetoothGattCharacteristic characteristic) {
            if (Build.VERSION.SDK_INT < 33) {
                postData(source, characteristic, characteristic.getValue());
            }
        }

        private void postRead(BluetoothGatt source, BluetoothGattCharacteristic characteristic, byte[] value, int status) {
            byte[] copy = value == null ? null : value.clone();
            worker.post(() -> finish(source, Operation.READ, characteristic.getUuid(), status, copy));
        }

        private void postData(BluetoothGatt source, BluetoothGattCharacteristic characteristic, byte[] value) {
            if (value == null || !RingProtocol.TX.equals(characteristic.getUuid())) {
                return;
            }
            byte[] copy = value.clone();
            worker.post(() -> {
                if (source == gatt && measurementStarted && !stopping && !closed) {
                    listener.onData(copy);
                }
            });
        }
    };

    @SuppressWarnings("deprecation")
    private void onServices(int status) {
        if (status != BluetoothGatt.GATT_SUCCESS) {
            fail("GATT サービスの検索に失敗しました (" + status + ")");
            return;
        }

        BluetoothGattService uart = gatt.getService(RingProtocol.UART);
        rx = uart == null ? null : uart.getCharacteristic(RingProtocol.RX);
        BluetoothGattCharacteristic tx = uart == null ? null : uart.getCharacteristic(RingProtocol.TX);
        BluetoothGattDescriptor cccd = tx == null ? null : tx.getDescriptor(RingProtocol.CCCD);
        if (rx == null || cccd == null) {
            fail("QRing UART のサービスまたは RX / TX / CCCD が見つかりません");
            return;
        }

        int txProperties = tx.getProperties();
        byte[] subscribe;
        if ((txProperties & BluetoothGattCharacteristic.PROPERTY_NOTIFY) != 0) {
            subscribe = BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE;
        } else if ((txProperties & BluetoothGattCharacteristic.PROPERTY_INDICATE) != 0) {
            subscribe = BluetoothGattDescriptor.ENABLE_INDICATION_VALUE;
        } else {
            fail("リングの TX が通知に対応していません");
            return;
        }

        int rxProperties = rx.getProperties();
        if ((rxProperties & BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE) != 0) {
            writeType = BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE;
        } else if ((rxProperties & BluetoothGattCharacteristic.PROPERTY_WRITE) != 0) {
            writeType = BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT;
        } else {
            fail("リングの RX に書き込めません");
            return;
        }

        listener.onStage("UART を確認。心拍通知を購読中…");
        begin(new OperationRequest(Operation.SUBSCRIBE, RingProtocol.CCCD, () -> {
            if (!gatt.setCharacteristicNotification(tx, true)) {
                return false;
            }
            if (Build.VERSION.SDK_INT >= 33) {
                return gatt.writeDescriptor(cccd, subscribe) == BluetoothStatusCodes.SUCCESS;
            }
            return cccd.setValue(subscribe) && gatt.writeDescriptor(cccd);
        }, (result, value) -> {
            if (result != BluetoothGatt.GATT_SUCCESS) {
                fail("心拍通知の購読に失敗しました (" + result + ")");
                return;
            }
            listener.onStage("連続心拍計測を開始中…");
            write(RingProtocol.start(), startStatus -> {
                if (startStatus == BluetoothGatt.GATT_SUCCESS) {
                    worker.postDelayed(continueMeasurement, 500);
                } else {
                    fail("計測開始コマンドに失敗しました (" + startStatus + ")");
                }
            });
        }));
    }

    private void continueMeasurement() {
        write(RingProtocol.continueMeasurement(), status -> {
            if (status != BluetoothGatt.GATT_SUCCESS) {
                shutdown("計測継続コマンドに失敗しました (" + status + ")", false);
                return;
            }
            ready = true;
            worker.removeCallbacks(connectTimeout);
            queueDeviceInfo(RingProtocol.MODEL);
            queueDeviceInfo(RingProtocol.SERIAL);
            queueDeviceInfo(RingProtocol.HARDWARE);
            queueDeviceInfo(RingProtocol.FIRMWARE);
            listener.onReady();
            poll();
        });
    }

    private void queueDeviceInfo(UUID uuid) {
        BluetoothGattService service = gatt.getService(RingProtocol.DEVICE_INFO);
        BluetoothGattCharacteristic item = service == null ? null : service.getCharacteristic(uuid);
        if (item != null && (item.getProperties() & BluetoothGattCharacteristic.PROPERTY_READ) != 0) {
            details.add(new OperationRequest(Operation.READ, uuid, () -> gatt.readCharacteristic(item),
                    (status, value) -> {
                        if (status == BluetoothGatt.GATT_SUCCESS && value != null) {
                            listener.onDeviceInfo(uuid, new String(value, StandardCharsets.UTF_8).trim());
                        }
                        worker.post(poll);
                    }));
        }
    }

    private void poll() {
        if (!ready || stopping || closed) {
            return;
        }
        long now = android.os.SystemClock.uptimeMillis();
        if (now >= nextBatteryPoll) {
            nextBatteryPoll = now + 60_000;
            write(RingProtocol.packet(RingProtocol.BATTERY), status -> afterPollWrite(true, status));
        } else if (now >= nextHeartRatePoll) {
            nextHeartRatePoll = now + 1_000;
            write(RingProtocol.packet(RingProtocol.POLL_HEART_RATE, 0x33), status -> afterPollWrite(false, status));
        } else if (!details.isEmpty()) {
            begin(details.removeFirst());
        } else {
            worker.postAtTime(poll, Math.min(nextBatteryPoll, nextHeartRatePoll));
        }
    }

    private void afterPollWrite(boolean battery, int status) {
        listener.onPoll(battery, status);
        worker.post(poll);
    }

    @SuppressWarnings("deprecation")
    private void write(byte[] packet, java.util.function.IntConsumer completion) {
        begin(new OperationRequest(Operation.WRITE, RingProtocol.RX, () -> {
            boolean accepted;
            if (Build.VERSION.SDK_INT >= 33) {
                accepted = gatt.writeCharacteristic(rx, packet, writeType) == BluetoothStatusCodes.SUCCESS;
            } else {
                rx.setWriteType(writeType);
                accepted = rx.setValue(packet) && gatt.writeCharacteristic(rx);
            }
            if (accepted && Byte.toUnsignedInt(packet[0]) == RingProtocol.REALTIME && packet[2] == 0x01) {
                measurementStarted = true;
            }
            return accepted;
        }, (status, value) -> completion.accept(status)));
    }

    private void begin(OperationRequest request) {
        pending = request;
        try {
            if (request.initiate().getAsBoolean()) {
                worker.postDelayed(operationTimeout, OPERATION_TIMEOUT);
            } else {
                finish(gatt, request.kind(), request.uuid(), BluetoothGatt.GATT_FAILURE, null);
            }
        } catch (SecurityException ex) {
            fail("Bluetooth の権限を確認してください");
        }
    }

    private void finish(BluetoothGatt source, Operation kind, UUID uuid, int status, byte[] value) {
        if (source != gatt || pending == null || pending.kind() != kind
                || !java.util.Objects.equals(uuid, pending.uuid())) {
            return;
        }

        worker.removeCallbacks(operationTimeout);
        OperationRequest completed = pending;
        pending = null;
        if (stopping) {
            sendStop();
        } else {
            completed.complete().accept(status, value);
        }
    }

    void disconnect() {
        shutdown("切断しました", true);
    }

    private void shutdown(String reason, boolean manual) {
        if (closed || stopping) {
            return;
        }
        stopping = true;
        manualStop = manual;
        stopReason = reason;
        worker.removeCallbacks(poll);
        worker.removeCallbacks(continueMeasurement);
        worker.removeCallbacks(connectTimeout);
        worker.removeCallbacks(operationTimeout);
        worker.postDelayed(closeTimeout, 1_000);
        if (pending == null || !measurementStarted) {
            sendStop();
        }
    }

    private void sendStop() {
        if (!measurementStarted || gatt == null) {
            completeShutdown();
            return;
        }
        measurementStarted = false;
        write(RingProtocol.stop(), ignored -> completeShutdown());
    }

    void closeNow() {
        manualStop = true;
        closeLink("切断しました");
    }

    private void completeShutdown() {
        closeLink(stopReason);
    }

    private void fail(String reason) {
        closeLink(stopping ? stopReason : reason);
    }

    private void closeLink(String reason) {
        if (closed) {
            return;
        }
        closed = true;
        worker.removeCallbacks(closeTimeout);
        worker.removeCallbacks(operationTimeout);
        worker.removeCallbacks(connectTimeout);
        worker.removeCallbacks(poll);
        worker.removeCallbacks(continueMeasurement);
        details.clear();
        pending = null;
        if (gatt != null) {
            try {
                gatt.disconnect();
            } catch (SecurityException ignored) {
                // The permission can be revoked while the link is active.
            }
            try {
                gatt.close();
            } catch (SecurityException ignored) {
                // Android can revoke access before the client is released.
            }
            gatt = null;
        }
        listener.onClosed(reason, manualStop);
    }
}
