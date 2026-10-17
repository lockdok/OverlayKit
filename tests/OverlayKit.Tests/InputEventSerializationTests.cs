using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace OverlayKit.Tests;

public class InputEventSerializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [Fact]
    public void VirtualKey_serializes_as_vk()
    {
        var evt = new InputEvent("key", "down", 0x41, "A");
        var json = JsonSerializer.Serialize(evt, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("vk", out var vk));
        Assert.Equal(0x41u, vk.GetUInt32());

        // Must NOT appear under the camelCase default name
        Assert.False(root.TryGetProperty("virtualKey", out _));
    }

    [Fact]
    public void Type_serializes_as_camelCase()
    {
        var evt = new InputEvent("key", "down", 0x41, "A");
        var json = JsonSerializer.Serialize(evt, Options);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("type", out var type));
        Assert.Equal("key", type.GetString());
    }

    [Fact]
    public void Null_optional_fields_are_omitted()
    {
        var evt = new InputEvent("scroll", Direction: "up", Horizontal: false);
        var json = JsonSerializer.Serialize(evt, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("phase", out _));
        Assert.False(root.TryGetProperty("label", out _));
        Assert.False(root.TryGetProperty("button", out _));
    }

    [Fact]
    public void Mouse_event_includes_button_and_phase()
    {
        var evt = new InputEvent("mouse", "down", Button: "left");
        var json = JsonSerializer.Serialize(evt, Options);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("mouse", root.GetProperty("type").GetString());
        Assert.Equal("down", root.GetProperty("phase").GetString());
        Assert.Equal("left", root.GetProperty("button").GetString());
    }

    [Fact]
    public void Roundtrip_preserves_all_fields()
    {
        var original = new InputEvent("key", "down", 0x51, "Q");
        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<InputEvent>(json, Options);

        Assert.Equal(original.Type, deserialized.Type);
        Assert.Equal(original.Phase, deserialized.Phase);
        Assert.Equal(original.VirtualKey, deserialized.VirtualKey);
        Assert.Equal(original.Label, deserialized.Label);
    }

    [Fact]
    public void VirtualKey_default_is_zero()
    {
        var evt = new InputEvent("scroll", Direction: "up");
        var json = JsonSerializer.Serialize(evt, Options);
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.TryGetProperty("vk", out var vk));
        Assert.Equal(0u, vk.GetUInt32());
    }
}

