package com.orzkwsk.colmiringvrcbridge;

import android.content.SharedPreferences;

final class BridgeSettings {
    static final String DEFAULT_HOST = "127.0.0.1";
    static final String DEFAULT_PORT = "9000";
    static final String PREFIX = "/avatar/parameters/";

    enum Loss { HOLD, FIXED, SPECIFIED }

    record Output(String host, String port, String parameter, boolean floating, boolean normalize,
                  String interval, boolean useFixed, String fixedBpm, Loss loss, String specified) {
        String address() {
            String name = parameter.trim();
            if (name.startsWith(PREFIX)) {
                return name;
            }
            return PREFIX + name.replaceFirst("^/+", "");
        }

        Endpoint endpoint() {
            String hostname = host.trim();
            if (hostname.matches("(?:[0-9]{1,3}\\.){3}")) {
                throw new IllegalArgumentException("PC の IP アドレスの最後の数字を入力してください");
            }
            if (hostname.isEmpty() || hostname.contains("/") || hostname.chars().anyMatch(Character::isWhitespace)) {
                throw new IllegalArgumentException("OSC 送信先に IP アドレスまたはホスト名を入力してください");
            }
            if (hostname.startsWith("[") && hostname.endsWith("]")) {
                hostname = hostname.substring(1, hostname.length() - 1);
            }
            Integer portNumber = integer(port);
            if (portNumber == null || portNumber < 1 || portNumber > 65_535) {
                throw new IllegalArgumentException("ポートは 1〜65535 を入力してください");
            }
            String address = address();
            if (address.length() <= PREFIX.length() || address.chars().anyMatch(Character::isISOControl)) {
                throw new IllegalArgumentException("有効な OSC パラメーター名を入力してください");
            }
            double seconds;
            try {
                seconds = Double.parseDouble(interval.trim());
            } catch (NumberFormatException ex) {
                seconds = Double.NaN;
            }
            if (!Double.isFinite(seconds) || seconds < 0.05 || seconds > 4_294_967.294) {
                throw new IllegalArgumentException("送信間隔は 0.05〜4294967.294 秒を入力してください");
            }
            Values values = values();
            if ((useFixed || loss == Loss.FIXED) && values.fixedBpm() == null) {
                throw new IllegalArgumentException("固定BPMに整数を入力してください");
            }
            if (!useFixed && loss == Loss.SPECIFIED && values.specified() == null) {
                throw new IllegalArgumentException("途絶時の指定値を、選択したOSC型に合う数値で入力してください");
            }
            return new Endpoint(hostname, portNumber, address, floating, floating && normalize,
                    Math.round(seconds * 1_000));
        }

        Values values() {
            return new Values(useFixed, integer(fixedBpm), loss, parseValue(specified, floating));
        }
    }

    record Endpoint(String host, int port, String address, boolean floating, boolean normalize, long periodMs) {
        Number fromBpm(int bpm) {
            int clamped = Math.max(0, Math.min(255, bpm));
            if (!floating) {
                return clamped;
            }
            return normalize ? clamped / 255f : (float) clamped;
        }
    }

    record Values(boolean useFixed, Integer fixedBpm, Loss loss, Number specified) {
        Number select(Endpoint endpoint, int freshBpm, Number last) {
            if (useFixed) {
                return fixed(endpoint);
            }
            return freshBpm > 0 ? endpoint.fromBpm(freshBpm) : onLoss(endpoint, last);
        }

        Number onLoss(Endpoint endpoint, Number last) {
            switch (loss) {
                case HOLD: return last;
                case FIXED: return fixed(endpoint);
                case SPECIFIED: return specified;
                default: throw new AssertionError(loss);
            }
        }

        private Number fixed(Endpoint endpoint) {
            return fixedBpm == null ? null : endpoint.fromBpm(fixedBpm);
        }
    }

    private BridgeSettings() {}

    static Output load(SharedPreferences prefs) {
        return new Output(prefs.getString("host", DEFAULT_HOST), prefs.getString("port", DEFAULT_PORT),
                prefs.getString("parameter", "VRCOSC/Heartrate/Value"), prefs.getBoolean("float", false),
                prefs.getBoolean("normalize", false), prefs.getString("interval", "1.0"),
                prefs.getBoolean("useFixed", false), prefs.getString("fixedBpm", "50"),
                Loss.valueOf(prefs.getString("loss", Loss.SPECIFIED.name())), prefs.getString("specified", "0"));
    }

    static void save(SharedPreferences prefs, Output output) {
        prefs.edit().putString("host", output.host()).putString("port", output.port())
                .putString("parameter", output.parameter()).putBoolean("float", output.floating())
                .putBoolean("normalize", output.normalize()).putString("interval", output.interval())
                .putBoolean("useFixed", output.useFixed()).putString("fixedBpm", output.fixedBpm())
                .putString("loss", output.loss().name()).putString("specified", output.specified()).apply();
    }

    private static Number parseValue(String input, boolean floating) {
        if (!floating) {
            return integer(input);
        }
        try {
            float value = Float.parseFloat(input.trim());
            return Float.isFinite(value) ? value : null;
        } catch (NumberFormatException ex) {
            return null;
        }
    }

    private static Integer integer(String input) {
        try {
            return Integer.valueOf(input.trim());
        } catch (NumberFormatException ex) {
            return null;
        }
    }
}
