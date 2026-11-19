using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ColmiRingVRCBridge.Models;

namespace ColmiRingVRCBridge.Services;

internal sealed class OscSettingsStore(string path)
{
    private readonly string _path = Path.GetFullPath(path);

    public static OscSettingsStore Local { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ColmiRingVRCBridge", "osc-settings.json"));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public OscSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new OscSettings();
            using var stream = File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var settings = JsonSerializer.Deserialize<OscSettings>(stream, JsonOptions) ?? new OscSettings();
            return settings.ValueType == OscValueType.Int ? settings with { Scaling = ScalingMode.RawBpm } : settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"OSC settings could not be read: {ex.GetType().Name}");
            return new OscSettings();
        }
    }

    public void Save(OscSettings settings)
    {
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"OSC settings could not be saved: {ex.GetType().Name}");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
