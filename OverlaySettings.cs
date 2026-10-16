using System.Text.Json;

namespace OverlayKit;

internal sealed record OverlaySettings
{
    public string DisplayMode { get; init; } = "text";
    public bool ShowKeyboard { get; init; } = true;
    public bool ShowMouse { get; init; } = true;
    public bool OverlayVisible { get; init; } = true;
    public int FadeMilliseconds { get; init; } = 1200;
    public int PositionX { get; init; }
    public int PositionY { get; init; } = 100;
    public int LayoutScalePercent { get; init; } = 150;
    public string FontFamily { get; init; } = "Segoe UI";
    public int FontSize { get; init; } = 16;
    public string TextColor { get; init; } = "#F1F4F8";
    public string AccentColor { get; init; } = "#71E0B2";
    public string KeyColor { get; init; } = "#293342";
    public string PanelColor { get; init; } = "#0E1219";
    public int PanelOpacity { get; init; } = 86;

    public static readonly string[] FontFamilies =
    [
        "Segoe UI",
        "Arial",
        "Verdana",
        "Georgia",
        "Consolas",
        "Trebuchet MS"
    ];

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
        FadeMilliseconds = Math.Clamp(settings.FadeMilliseconds, 250, 5000),
        PositionX = Math.Clamp(settings.PositionX, 0, 100),
        PositionY = Math.Clamp(settings.PositionY, 0, 100),
        LayoutScalePercent = Math.Clamp(settings.LayoutScalePercent, 50, 300),
        FontFamily = FontFamilies.Contains(settings.FontFamily, StringComparer.Ordinal)
            ? settings.FontFamily
            : "Segoe UI",
        FontSize = Math.Clamp(settings.FontSize, 10, 36),
        TextColor = IsHexColor(settings.TextColor) ? settings.TextColor.ToUpperInvariant() : "#F1F4F8",
        AccentColor = IsHexColor(settings.AccentColor) ? settings.AccentColor.ToUpperInvariant() : "#71E0B2",
        KeyColor = IsHexColor(settings.KeyColor) ? settings.KeyColor.ToUpperInvariant() : "#293342",
        PanelColor = IsHexColor(settings.PanelColor) ? settings.PanelColor.ToUpperInvariant() : "#0E1219",
        PanelOpacity = Math.Clamp(settings.PanelOpacity, 0, 100)
    };

    public static bool IsHexColor(string? value) =>
        value is { Length: 7 }
        && value[0] == '#'
        && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static string GetPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OverlayKit",
        "settings.json");

    private static string GetLegacyPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OBSInputOverlay",
        "settings.json");
}
