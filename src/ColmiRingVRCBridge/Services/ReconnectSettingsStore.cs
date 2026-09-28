using System.Text.Json;

namespace ColmiRingVRCBridge.Services;

internal sealed record ReconnectSettings(
    ulong? BluetoothAddress,
    string? DeviceName,
    bool AutoReconnect = true);

internal static class ReconnectSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ColmiRingVRCBridge",
        "connection.json");

    public static ReconnectSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new ReconnectSettings(null, null, true);
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<ReconnectSettings>(json)
                   ?? new ReconnectSettings(null, null, true);
        }
        catch
        {
            return new ReconnectSettings(null, null, true);
        }
    }

    public static void Save(ReconnectSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        catch
        {
            // Reconnect persistence must never affect live BLE operation.
        }
    }
}
