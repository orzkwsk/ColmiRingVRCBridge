package com.orzkwsk.colmiringvrcbridge;

import java.util.UUID;

final class RingProtocol {
    static final UUID UART = UUID.fromString("6e40fff0-b5a3-f393-e0a9-e50e24dcca9e");
    static final UUID RX = UUID.fromString("6e400002-b5a3-f393-e0a9-e50e24dcca9e");
    static final UUID TX = UUID.fromString("6e400003-b5a3-f393-e0a9-e50e24dcca9e");
    static final UUID CCCD = standardUuid("2902");
    static final UUID DEVICE_INFO = standardUuid("180a");
    static final UUID MODEL = standardUuid("2a24");
    static final UUID SERIAL = standardUuid("2a25");
    static final UUID FIRMWARE = standardUuid("2a26");
    static final UUID HARDWARE = standardUuid("2a27");

    static final int BATTERY = 0x03;
    static final int POLL_HEART_RATE = 0x1e;
    static final int REALTIME = 0x69;
    static final int STOP_REALTIME = 0x6a;
    static final int CONTINUOUS_HEART_RATE = 0x06;

    record Sample(int bpm, int battery, boolean charging, int error) {}

    private RingProtocol() {}

    private static UUID standardUuid(String shortId) {
        return UUID.fromString("0000" + shortId + "-0000-1000-8000-00805f9b34fb");
    }

    static byte[] start() {
        return packet(REALTIME, CONTINUOUS_HEART_RATE, 0x01);
    }

    static byte[] continueMeasurement() {
        return packet(REALTIME, CONTINUOUS_HEART_RATE, 0x03);
    }

    static byte[] stop() {
        return packet(STOP_REALTIME, CONTINUOUS_HEART_RATE, 0, 0);
    }

    static byte[] packet(int command, int... payload) {
        byte[] data = new byte[16];
        data[0] = (byte) command;
        for (int i = 0; i < payload.length; i++) {
            data[i + 1] = (byte) payload[i];
        }
        data[15] = checksum(data);
        return data;
    }

    static Sample decode(byte[] data) {
        if (data.length != 16 || data[15] != checksum(data)) {
            return null;
        }

        int type = Byte.toUnsignedInt(data[1]);
        int status = Byte.toUnsignedInt(data[2]);
        switch (Byte.toUnsignedInt(data[0])) {
            case BATTERY:
                return new Sample(0, Math.min(100, type), status != 0, 0);
            case POLL_HEART_RATE:
                return new Sample(type, -1, false, 0);
            case REALTIME:
                if (type == CONTINUOUS_HEART_RATE) {
                    return new Sample(status == 0 ? Byte.toUnsignedInt(data[3]) : 0, -1, false, status);
                }
                // Other measurement types do not belong to the continuous session.
                break;
            default:
                break;
        }
        return new Sample(0, -1, false, 0);
    }

    private static byte checksum(byte[] data) {
        int sum = 0;
        for (int i = 0; i < 15; i++) {
            sum += Byte.toUnsignedInt(data[i]);
        }
        return (byte) sum;
    }
}
