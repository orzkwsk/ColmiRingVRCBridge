package com.orzkwsk.colmiringvrcbridge;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.function.IntSupplier;
import java.util.function.Supplier;

final class OscOutput {
    interface Listener {
        void onSent(Number value);
        void onStopped(String error);
    }

    private final ScheduledExecutorService executor = Executors.newSingleThreadScheduledExecutor();
    private Session session;

    void start(BridgeSettings.Endpoint endpoint, Supplier<BridgeSettings.Values> values,
               IntSupplier freshBpm, Listener listener) {
        executor.execute(() -> {
            Session created = new Session(endpoint, values, freshBpm, listener);
            session = created;
            try {
                created.address = InetAddress.getByName(endpoint.host());
                created.socket = new DatagramSocket();
                created.ticks = executor.scheduleWithFixedDelay(created::tick, 0,
                        endpoint.periodMs(), TimeUnit.MILLISECONDS);
            } catch (IOException | IllegalArgumentException | SecurityException ex) {
                finish(created, ex);
            }
        });
    }

    void stop() {
        executor.execute(this::sendFinal);
    }

    void shutdown() {
        executor.execute(this::sendFinal);
        executor.shutdown();
    }

    private void sendFinal() {
        Session current = session;
        if (current == null) {
            return;
        }

        try {
            current.send(current.values.get().onLoss(current.endpoint, current.lastSent));
            finish(current, null);
        } catch (IOException | IllegalArgumentException | SecurityException ex) {
            finish(current, ex);
        }
    }

    private void finish(Session current, Exception error) {
        if (current.ticks != null) {
            current.ticks.cancel(false);
        }
        if (current.socket != null) {
            current.socket.close();
        }
        session = null;
        current.listener.onStopped(error == null ? null : "OSC 送信エラー · " + error.getMessage());
    }

    private final class Session {
        final BridgeSettings.Endpoint endpoint;
        final Supplier<BridgeSettings.Values> values;
        final IntSupplier freshBpm;
        final Listener listener;
        InetAddress address;
        DatagramSocket socket;
        ScheduledFuture<?> ticks;
        Number lastSent;

        Session(BridgeSettings.Endpoint endpoint, Supplier<BridgeSettings.Values> values,
                IntSupplier freshBpm, Listener listener) {
            this.endpoint = endpoint;
            this.values = values;
            this.freshBpm = freshBpm;
            this.listener = listener;
        }

        void tick() {
            try {
                send(values.get().select(endpoint, freshBpm.getAsInt(), lastSent));
            } catch (IOException | IllegalArgumentException | SecurityException ex) {
                finish(this, ex);
            }
        }

        void send(Number value) throws IOException {
            if (value == null) {
                return;
            }
            byte[] data = encode(endpoint.address(), endpoint.floating(), value);
            socket.send(new DatagramPacket(data, data.length, address, endpoint.port()));
            lastSent = value;
            listener.onSent(value);
        }
    }

    static byte[] encode(String address, boolean floating, Number value) {
        byte[] name = address.getBytes(StandardCharsets.UTF_8);
        // OSC strings include a null terminator and pad to a multiple of four bytes.
        int addressSize = (name.length + 4) & ~3;
        ByteBuffer buffer = ByteBuffer.allocate(addressSize + 8);
        buffer.put(name);
        buffer.position(addressSize);
        buffer.put((byte) ',').put((byte) (floating ? 'f' : 'i')).putShort((short) 0);
        if (floating) {
            buffer.putFloat(value.floatValue());
        } else {
            buffer.putInt(value.intValue());
        }
        return buffer.array();
    }
}
