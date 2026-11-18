using System.Text;
using System.Text.Json;

namespace MoodyBlues.Backend.Sessions;

/// <summary>
/// Share-viewer settings are owned by the frontend, so the backend never models their fields: it
/// checks that a body is a JSON object of a sane size, stores it as text in a jsonb column, and
/// returns it verbatim as JSON (not as an escaped string).
/// </summary>
public static class ShareSettingsDocument
{
    /// <summary>The most serialized bytes a settings object may take.</summary>
    public const int MaxBytes = 16 * 1024;

    /// <summary>
    /// Validates <paramref name="settings"/> and returns the text to store (null clears the settings),
    /// or an error message for a 400 response.
    /// </summary>
    public static (string? Json, string? Error) Validate(JsonElement? settings)
    {
        if (settings is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return (null, null);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return (null, "Settings must be a JSON object or null.");
        }

        string json = element.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            return (null, $"Settings must be at most {MaxBytes / 1024} KB.");
        }

        return (json, null);
    }

    /// <summary>The stored text as a JSON value for a response; null when nothing is stored or the text is unreadable.</summary>
    public static JsonElement? ToElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
