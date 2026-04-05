using System.Text.Json;

namespace FhirAugury.Cli;

/// <summary>
/// Convenience extensions for extracting values from <see cref="JsonElement"/>.
/// </summary>
internal static class JsonElementExtensions
{
    /// <summary>
    /// Gets a string property value, or null if the property is missing or null.
    /// </summary>
    public static string? GetStringOrNull(this JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out JsonElement prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    /// <summary>
    /// Gets a Dictionary&lt;string, string&gt; from an object property, or an empty dictionary.
    /// </summary>
    public static Dictionary<string, string> GetStringDictionary(this JsonElement element, string propertyName)
    {
        Dictionary<string, string> result = [];
        if (element.TryGetProperty(propertyName, out JsonElement prop) && prop.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty kv in prop.EnumerateObject())
            {
                string? val = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() : kv.Value.ToString();
                if (val is not null)
                    result[kv.Name] = val;
            }
        }
        return result;
    }
}
