using System.Text.Json;

namespace FhirAugury.Common;

/// <summary>
/// Shared helper methods for extracting values from System.Text.Json elements.
/// </summary>
public static class JsonElementHelper
{
    /// <summary>Gets a string property value, returning null if missing or null-valued.</summary>
    public static string? GetString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var prop))
            return null;
        return prop.ValueKind == JsonValueKind.Null ? null : prop.ToString();
    }

    /// <summary>Gets a nested string property (2 levels deep), returning null if missing.</summary>
    public static string? GetNestedString(JsonElement parent, string prop1, string prop2)
    {
        if (!parent.TryGetProperty(prop1, out var p1) || p1.ValueKind == JsonValueKind.Null) return null;
        if (!p1.TryGetProperty(prop2, out var p2) || p2.ValueKind == JsonValueKind.Null) return null;
        return p2.ValueKind == JsonValueKind.String ? p2.GetString() : p2.ToString();
    }

    /// <summary>Gets a nested string property (3 levels deep), returning null if missing.</summary>
    public static string? GetNestedString(JsonElement parent, string prop1, string prop2, string prop3)
    {
        if (!parent.TryGetProperty(prop1, out var p1) || p1.ValueKind == JsonValueKind.Null) return null;
        if (!p1.TryGetProperty(prop2, out var p2) || p2.ValueKind == JsonValueKind.Null) return null;
        if (!p2.TryGetProperty(prop3, out var p3) || p3.ValueKind == JsonValueKind.Null) return null;
        return p3.ValueKind == JsonValueKind.String ? p3.GetString() : p3.ToString();
    }
}
