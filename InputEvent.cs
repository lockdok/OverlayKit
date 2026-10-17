using System.Text.Json.Serialization;

namespace OverlayKit;

internal readonly record struct InputEvent(
    string Type,
    string? Phase = null,
    [property: JsonPropertyName("vk")] uint VirtualKey = 0,
    string? Label = null,
    string? Button = null,
    string? Direction = null,
    bool? Horizontal = null);

