package com.orzkwsk.colmiringvrcbridge;

import android.annotation.SuppressLint;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.graphics.Path;
import android.view.View;

import java.util.Collections;
import java.util.List;

@SuppressLint("ViewConstructor") // Created programmatically for the heart-rate and battery panels.
final class TelemetryChart extends View {
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path path = new Path();
    private final Path fill = new Path();
    private final boolean battery;
    private List<TelemetryHistory.Point> points = Collections.emptyList();
    private long now;

    TelemetryChart(Context context, boolean battery) {
        super(context);
        this.battery = battery;
        setContentDescription(battery ? "バッテリー残量の24時間グラフ" : "Heart rate over the last 60 seconds");
    }

    void show(List<TelemetryHistory.Point> samples, long elapsed) {
        points = samples;
        now = elapsed;
        invalidate();
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        long range = battery ? TelemetryHistory.BATTERY_GRAPH_MS : TelemetryHistory.HEART_GRAPH_MS;
        long start = now - range;
        float left = dp(battery ? 34 : 30);
        float top = dp(8);
        float right = getWidth() - dp(8);
        float bottom = getHeight() - dp(20);
        if (right <= left || bottom <= top) {
            return;
        }

        int first = 0;
        while (first < points.size() && points.get(first).elapsed() < start) {
            first++;
        }
        int low = 0;
        int high = 100;
        if (!battery && first < points.size()) {
            low = 255;
            high = 0;
            for (int i = first; i < points.size(); i++) {
                int value = points.get(i).value();
                low = Math.min(low, value);
                high = Math.max(high, value);
            }
            low = Math.max(0, ((low - 5) / 10) * 10);
            high = ((high + 15) / 10) * 10;
        }

        paint.setStyle(Paint.Style.FILL);
        if (battery) {
            for (int i = first; i < points.size(); i++) {
                TelemetryHistory.Point point = points.get(i);
                long next = i + 1 < points.size() ? points.get(i + 1).elapsed() : now;
                if (point.charging() && next - point.elapsed() <= 30 * 60_000L) {
                    paint.setColor(0x2279E3BA);
                    canvas.drawRect(x(point.elapsed(), start, range, left, right), top,
                            x(next, start, range, left, right), bottom, paint);
                }
            }
        }

        paint.setTextSize(dp(12));
        paint.setStrokeWidth(dp(1));
        for (int line = 0; line <= 2; line++) {
            float y = top + (bottom - top) * line / 2f;
            paint.setColor(MainActivity.LINE);
            canvas.drawLine(left, y, right, y, paint);
            paint.setColor(MainActivity.MUTED);
            paint.setTextAlign(Paint.Align.RIGHT);
            int scaleValue = high - (high - low) * line / 2;
            canvas.drawText(scaleValue + (battery ? "%" : ""), left - dp(6), y + dp(4), paint);
        }
        paint.setTextAlign(Paint.Align.LEFT);
        canvas.drawText(battery ? "−24時間" : "−60 s", left, bottom + dp(15), paint);
        paint.setTextAlign(Paint.Align.RIGHT);
        canvas.drawText(battery ? "現在" : "Now", right, bottom + dp(15), paint);

        if (first >= points.size()) {
            paint.setTextAlign(Paint.Align.CENTER);
            String empty = battery ? "履歴なし" : "Waiting for heart-rate samples…";
            canvas.drawText(empty, (left + right) / 2, (top + bottom) / 2, paint);
            return;
        }

        path.reset();
        fill.reset();
        paint.setColor(MainActivity.ACCENT);
        paint.setStyle(Paint.Style.FILL);
        long previous = Long.MIN_VALUE;
        float lastX = left;
        float lastY = bottom;
        for (int i = first; i < points.size(); i++) {
            TelemetryHistory.Point point = points.get(i);
            float x = x(point.elapsed(), start, range, left, right);
            float y = bottom - (point.value() - low) * (bottom - top) / (high - low);
            if (i == first || point.elapsed() - previous > (battery ? 30 * 60_000L : 3_000L)) {
                path.moveTo(x, y);
                canvas.drawCircle(x, y, dp(2), paint);
                if (!battery) {
                    if (i > first) {
                        fill.lineTo(lastX, bottom);
                        fill.close();
                    }
                    fill.moveTo(x, bottom);
                    fill.lineTo(x, y);
                }
            } else {
                path.lineTo(x, y);
                if (!battery) fill.lineTo(x, y);
            }
            lastX = x;
            lastY = y;
            previous = point.elapsed();
        }
        if (!battery) {
            fill.lineTo(lastX, bottom);
            fill.close();
            paint.setColor(0x2079E3BA);
            canvas.drawPath(fill, paint);
            paint.setColor(MainActivity.ACCENT);
            canvas.drawCircle(lastX, lastY, dp(3), paint);
        }
        paint.setStyle(Paint.Style.STROKE);
        paint.setStrokeWidth(dp(2));
        paint.setStrokeJoin(Paint.Join.ROUND);
        paint.setStrokeCap(Paint.Cap.ROUND);
        canvas.drawPath(path, paint);
    }

    private static float x(long point, long start, long range, float left, float right) {
        return left + (right - left) * (point - start) / range;
    }

    private float dp(int value) {
        return value * getResources().getDisplayMetrics().density;
    }
}
