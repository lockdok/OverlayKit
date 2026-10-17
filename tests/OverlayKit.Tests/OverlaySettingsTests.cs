using Xunit;

namespace OverlayKit.Tests;

public class OverlaySettingsTests
{
    // --- Sanitize: DisplayMode ---

    [Theory]
    [InlineData("text", "text")]
    [InlineData("keyboard", "keyboard")]
    [InlineData("TEXT", "text")]
    [InlineData("invalid", "text")]
    [InlineData("", "text")]
    public void Sanitize_clamps_DisplayMode(string input, string expected)
    {
        var settings = new OverlaySettings { DisplayMode = input };
        var result = OverlaySettings.Sanitize(settings);
        Assert.Equal(expected, result.DisplayMode);
    }

    // --- Sanitize: integer clamping ---

    [Theory]
    [InlineData(0, 250)]
    [InlineData(100, 250)]
    [InlineData(250, 250)]
    [InlineData(1200, 1200)]
    [InlineData(5000, 5000)]
    [InlineData(9999, 5000)]
    public void Sanitize_clamps_FadeMilliseconds(int input, int expected)
    {
        var settings = new OverlaySettings { FadeMilliseconds = input };
        var result = OverlaySettings.Sanitize(settings);
        Assert.Equal(expected, result.FadeMilliseconds);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(200, 100)]
    public void Sanitize_clamps_PositionX(int input, int expected)
    {
        var settings = new OverlaySettings { PositionX = input };
        var result = OverlaySettings.Sanitize(settings);
        Assert.Equal(expected, result.PositionX);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(200, 100)]
    public void Sanitize_clamps_PositionY(int input, int expected)
    {
        var settings = new OverlaySettings { PositionY = input };
        var result = OverlaySettings.Sanitize(settings);
        Assert.Equal(expected, result.PositionY);
    }

    [Theory]
    [InlineData(10, 50)]
    [InlineData(50, 50)]
    [InlineData(150, 150)]
    [InlineData(300, 300)]
    [InlineData(500, 300)]
    public void Sanitize_clamps_LayoutScalePercent(int input, int expected)
    {
        var settings = new OverlaySettings { LayoutScalePercent = input };
        var result = OverlaySettings.Sanitize(settings);
        Assert.Equal(expected, result.LayoutScalePercent);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(10, 10)]
    [InlineData(16, 16)]
    [InlineData(36, 36)]
    [InlineData(72, 36)]
    public void Sanitize_clamps_FontSize(int input, int expected)
    {
        var settings = new OverlaySettings { FontSize = input };
        var result = OverlaySettings.Sanitize(settings);
        Assert.Equal(expected, result.FontSize);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(86, 86)]
    [InlineData(100, 100)]
    [InlineData(200, 100)]
    public void Sanitize_clamps_PanelOpacity(int input, int expected)
    {
        var settings = new OverlaySettings { PanelOpacity = input };
        var result = OverlaySettings.Sanitize(settings);
        Assert.Equal(expected, result.PanelOpacity);
    }

    // --- Sanitize: FontFamily whitelist ---

    [Theory]
    [InlineData("Segoe UI", "Segoe UI")]
    [InlineData("Arial", "Arial")]
    [InlineData("Consolas", "Consolas")]
    [InlineData("Trebuchet MS", "Trebuchet MS")]
    public void Sanitize_allows_whitelisted_fonts(string input, string expected)
    {
        var settings = new OverlaySettings { FontFamily = input };
        var result = OverlaySettings.Sanitize(settings);
        Assert.Equal(expected, result.FontFamily);
    }

    [Theory]
    [InlineData("Comic Sans MS")]
    [InlineData("")]
    [InlineData("segoe ui")]     // case-sensitive match
    [InlineData("Hack")]
    public void Sanitize_rejects_unknown_fonts(string input)
    {
        var settings = new OverlaySettings { FontFamily = input };
        var result = OverlaySettings.Sanitize(settings);
        Assert.Equal("Segoe UI", result.FontFamily);
    }

    // --- Sanitize: hex color validation ---

    [Theory]
    [InlineData("#F1F4F8", "#F1F4F8")]
    [InlineData("#aabbcc", "#AABBCC")]
    [InlineData("#000000", "#000000")]
    [InlineData("#ffffff", "#FFFFFF")]
    public void Sanitize_normalizes_valid_hex_colors(string input, string expected)
    {
        var settings = new OverlaySettings { TextColor = input };
        var result = OverlaySettings.Sanitize(settings);
        Assert.Equal(expected, result.TextColor);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#FFF")]
    [InlineData("#GGHHII")]
    [InlineData("F1F4F8")]
    [InlineData("#F1F4F8FF")]
    [InlineData("")]
    public void Sanitize_rejects_invalid_colors(string input)
    {
        var settings = new OverlaySettings { TextColor = input };
        var result = OverlaySettings.Sanitize(settings);
        Assert.Equal("#F1F4F8", result.TextColor);
    }

    // --- IsHexColor ---

    [Theory]
    [InlineData("#000000", true)]
    [InlineData("#FFFFFF", true)]
    [InlineData("#abcdef", true)]
    [InlineData("#123456", true)]
    [InlineData("#FFF", false)]
    [InlineData("000000", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("#12345", false)]
    [InlineData("#1234567", false)]
    public void IsHexColor_validates_correctly(string? input, bool expected)
    {
        Assert.Equal(expected, OverlaySettings.IsHexColor(input));
    }

    // --- Default values ---

    [Fact]
    public void Default_settings_survive_Sanitize_unchanged()
    {
        var defaults = new OverlaySettings();
        var sanitized = OverlaySettings.Sanitize(defaults);

        Assert.Equal(defaults.DisplayMode, sanitized.DisplayMode);
        Assert.Equal(defaults.ShowKeyboard, sanitized.ShowKeyboard);
        Assert.Equal(defaults.ShowMouse, sanitized.ShowMouse);
        Assert.Equal(defaults.OverlayVisible, sanitized.OverlayVisible);
        Assert.Equal(defaults.FadeMilliseconds, sanitized.FadeMilliseconds);
        Assert.Equal(defaults.PositionX, sanitized.PositionX);
        Assert.Equal(defaults.PositionY, sanitized.PositionY);
        Assert.Equal(defaults.LayoutScalePercent, sanitized.LayoutScalePercent);
        Assert.Equal(defaults.FontFamily, sanitized.FontFamily);
        Assert.Equal(defaults.FontSize, sanitized.FontSize);
        Assert.Equal(defaults.PanelOpacity, sanitized.PanelOpacity);
    }

    // --- Booleans pass through ---

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sanitize_preserves_ShowKeyboard(bool value)
    {
        var settings = new OverlaySettings { ShowKeyboard = value };
        var result = OverlaySettings.Sanitize(settings);
        Assert.Equal(value, result.ShowKeyboard);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sanitize_preserves_ShowMouse(bool value)
    {
        var settings = new OverlaySettings { ShowMouse = value };
        var result = OverlaySettings.Sanitize(settings);
        Assert.Equal(value, result.ShowMouse);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sanitize_preserves_OverlayVisible(bool value)
    {
        var settings = new OverlaySettings { OverlayVisible = value };
        var result = OverlaySettings.Sanitize(settings);
        Assert.Equal(value, result.OverlayVisible);
    }
}

