using System.Text;
using System.Text.Json;

namespace FhirAugury.Source.GitHub.Ingestion.Parsing;

/// <summary>
/// Extracts string values from JSON files. For FHIR resources (detected via "resourceType"
/// field), extracts semantic fields. Falls back to extracting all string values.
/// </summary>
public class JsonFileContentParser : IFileContentParser
{
    public string ParserType => "json";

    private static readonly HashSet<string> FhirSemanticProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "title", "description", "definition", "comment",
        "purpose", "copyright", "requirements", "meaning",
        "display", "text", "value",
    };

    public string? ExtractText(string filePath, Stream content, int maxOutputLength)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 128,
            });

            StringBuilder sb = new StringBuilder();
            JsonElement root = doc.RootElement;

            bool isFhir = root.ValueKind == JsonValueKind.Object &&
                           root.TryGetProperty("resourceType", out _);

            if (isFhir)
                ExtractFhirContent(root, sb, maxOutputLength);
            else
                ExtractAllStrings(root, sb, maxOutputLength);

            string result = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (JsonException)
        {
            // Fall back to plain text if JSON is malformed
            content.Position = 0;
            return new PlainTextFileContentParser("json").ExtractText(filePath, content, maxOutputLength);
        }
    }

    private static void ExtractFhirContent(JsonElement element, StringBuilder sb, int maxLength)
    {
        if (sb.Length >= maxLength)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    if (sb.Length >= maxLength) break;

                    if (FhirSemanticProperties.Contains(prop.Name) && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        AppendText(sb, prop.Value.GetString(), maxLength);
                    }

                    // Also extract narrative div text
                    if (prop.Name == "div" && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        string? divHtml = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(divHtml))
                            AppendText(sb, StripHtmlTags(divHtml), maxLength);
                    }

                    // Recurse into objects/arrays
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        ExtractFhirContent(prop.Value, sb, maxLength);
                }
                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (sb.Length >= maxLength) break;
                    ExtractFhirContent(item, sb, maxLength);
                }
                break;
        }
    }

    private static void ExtractAllStrings(JsonElement element, StringBuilder sb, int maxLength)
    {
        if (sb.Length >= maxLength)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                AppendText(sb, element.GetString(), maxLength);
                break;

            case JsonValueKind.Object:
                foreach (JsonProperty prop in element.EnumerateObject())
                {
                    if (sb.Length >= maxLength) break;
                    ExtractAllStrings(prop.Value, sb, maxLength);
                }
                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (sb.Length >= maxLength) break;
                    ExtractAllStrings(item, sb, maxLength);
                }
                break;
        }
    }

    private static string StripHtmlTags(string html)
    {
        StringBuilder sb = new StringBuilder(html.Length);
        bool inTag = false;

        foreach (char c in html)
        {
            if (c == '<')
            {
                inTag = true;
                continue;
            }

            if (c == '>')
            {
                inTag = false;
                sb.Append(' ');
                continue;
            }

            if (!inTag)
                sb.Append(c);
        }

        return sb.ToString();
    }

    private static void AppendText(StringBuilder sb, string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || sb.Length >= maxLength)
            return;

        string cleaned = text.Trim();
        if (sb.Length > 0)
            sb.Append(' ');

        int remaining = maxLength - sb.Length;
        if (cleaned.Length > remaining)
            sb.Append(cleaned.AsSpan(0, remaining));
        else
            sb.Append(cleaned);
    }
}
