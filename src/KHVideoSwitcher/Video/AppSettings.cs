using System.IO;
using System.Text.Json;

namespace KHVideoSwitcher.Video;

public sealed class AppSettings
{
    public const int PresetCount = 6;

    /// <summary>Each preset stores a complete look: scene kind + camera framing.</summary>
    public Scene?[] Presets { get; set; } = new Scene?[PresetCount];
    public int FadeMs { get; set; } = 300;

    /// <summary>When true, selecting a scene (buttons/F-keys) takes it immediately with a fade.</summary>
    public bool AutoTakeScenes { get; set; } = true;

    /// <summary>When true, media-state changes switch scenes automatically.</summary>
    public bool AutoScenes { get; set; }

    /// <summary>
    /// True (default): dragging in PREVIEW moves the picture with the mouse.
    /// False: dragging moves the crop box / "camera" instead.
    /// </summary>
    public bool DragMovesPicture { get; set; } = true;

    /// <summary>Base64 luma-grid fingerprint of the stock (yeartext) screen.</summary>
    public string? StockFingerprint { get; set; }

    /// <summary>Capture target (size suffix stripped) the stock fingerprint was taken from.</summary>
    public string? StockTargetName { get; set; }

    /// <summary>Last used camera / media capture target, restored at startup.</summary>
    public string? LastCameraName { get; set; }
    public string? LastMediaTargetName { get; set; }

    /// <summary>Over-the-shoulder inset: media box width as a fraction of the frame.</summary>
    public double OtsInsetWidthFraction { get; set; } = 0.42;

    /// <summary>Over-the-shoulder inset: gap from the top edge, output pixels.</summary>
    public double OtsInsetTopMargin { get; set; } = 72;

    /// <summary>Over-the-shoulder inset: gap from the right edge, output pixels.</summary>
    public double OtsInsetRightMargin { get; set; } = 56;

    /// <summary>
    /// When true, the camera framing in OVER-THE-SHOULDER shifts left to keep the
    /// media inset from covering the subject: pans within the crop where the
    /// current zoom leaves room, and shifts the rendered frame (revealing the
    /// black background) for whatever the pan couldn't cover.
    /// </summary>
    public bool OtsShiftCameraForInset { get; set; }

    /// <summary>Ask Windows not to draw the capture highlight border (applies when capture starts).</summary>
    public bool HideCaptureBorder { get; set; } = true;

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
                        var resized = new Scene?[PresetCount];
                        Array.Copy(loaded.Presets, resized, Math.Min(loaded.Presets.Length, PresetCount));
                        loaded.Presets = resized;
                    }
                    // Drop entries from the pre-scene settings format (their
                    // PTZ deserializes as zoom 0, which is never valid).
                    for (var i = 0; i < loaded.Presets.Length; i++)
                    {
                        if (loaded.Presets[i] is { Ptz.Zoom: <= 0 })
                            loaded.Presets[i] = null;
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
