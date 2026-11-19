package com.orzkwsk.colmiringvrcbridge;

import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Deque;
import java.util.List;

final class TelemetryHistory {
    static final long HEART_GRAPH_MS = 60_000;
    static final long BATTERY_GRAPH_MS = 24 * 60 * 60_000L;
    private static final long BATTERY_SAMPLE_INTERVAL_MS = 10 * 60_000;

    record Point(long elapsed, int value, boolean charging) {}

    private final Deque<Point> points = new ArrayDeque<>();
    private final boolean battery;
    private List<Point> snapshot = Collections.emptyList();

    TelemetryHistory(boolean battery) {
        this.battery = battery;
    }

    void add(long elapsed, int value, boolean charging) {
        Point last = points.peekLast();
        if (battery && last != null && last.value() == value && last.charging() == charging
                && elapsed - last.elapsed() < BATTERY_SAMPLE_INTERVAL_MS) {
            return;
        }
        long cutoff = elapsed - (battery ? BATTERY_GRAPH_MS : HEART_GRAPH_MS);
        int capacity = battery ? 2048 : 4096;
        while (!points.isEmpty() && (points.peekFirst().elapsed() < cutoff || points.size() >= capacity)) {
            points.removeFirst();
        }
        points.addLast(new Point(elapsed, value, charging));
        snapshot = Collections.unmodifiableList(new ArrayList<>(points));
    }

    void clear() {
        points.clear();
        snapshot = Collections.emptyList();
    }

    List<Point> snapshot() {
        return snapshot;
    }
}
