using System.Text.Json;

namespace OverlayKit;

internal sealed record OverlaySettings
{
    public string DisplayMode { get; init; } = "text";
    public bool ShowKeyboard { get; init; } = true;
    public bool ShowMouse { get; init; } = true;
    public bool OverlayVisible { get; init; } = true;
    public int FadeMilliseconds { get; init; } = 1200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static OverlaySettings Load()
    {
        var path = GetPath();
        var legacyPath = GetLegacyPath();
        if (!File.Exists(path) && !File.Exists(legacyPath))
        {
            return new OverlaySettings();
        }

        try
        {
            var settingsPath = File.Exists(path) ? path : legacyPath;
            var settings = Sanitize(
                JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(settingsPath), JsonOptions)
                ?? new OverlaySettings());
            if (settingsPath == legacyPath)
            {
                settings.Save();
            }

            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not load settings from {path} or {legacyPath}; using defaults. Details: {ex.Message}");
            return new OverlaySettings();
        }
    }

    public void Save()
    {
        var path = GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporaryPath, path, true);
    }

    public static OverlaySettings Sanitize(OverlaySettings settings) => settings with
    {
        DisplayMode = settings.DisplayMode is "keyboard" ? "keyboard" : "text",
        FadeMilliseconds = Math.Clamp(settings.FadeMilliseconds, 250, 5000)
    };

    private static string GetPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OverlayKit",
        "settings.json");

    private static string GetLegacyPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OBSInputOverlay",
        "settings.json");
}
