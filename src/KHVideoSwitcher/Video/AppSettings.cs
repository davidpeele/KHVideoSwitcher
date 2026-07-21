using System.IO;
using System.Text.Json;

namespace KHVideoSwitcher.Video;

public sealed class AppSettings
{
    public const int PresetCount = 6;

    public PtzState?[] Presets { get; set; } = new PtzState?[PresetCount];
    public int FadeMs { get; set; } = 300;

    /// <summary>When true, selecting a scene (buttons/F-keys) takes it immediately with a fade.</summary>
    public bool AutoTakeScenes { get; set; } = true;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KHVideoSwitcher");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true, IncludeFields = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), _json);
                if (loaded is not null)
                {
                    if (loaded.Presets.Length != PresetCount)
                    {
                        var resized = new PtzState?[PresetCount];
                        Array.Copy(loaded.Presets, resized, Math.Min(loaded.Presets.Length, PresetCount));
                        loaded.Presets = resized;
                    }
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt settings should never block startup; fall through to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, _json));
    }
}
