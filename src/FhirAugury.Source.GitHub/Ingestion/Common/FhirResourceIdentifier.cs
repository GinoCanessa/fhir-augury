namespace FhirAugury.Source.GitHub.Ingestion.Common;

/// <summary>
/// Identifies the FHIR resource type from file content (XML or JSON) and extracts
/// key metadata such as name, url, and title.
/// </summary>
public static class FhirResourceIdentifier
{
    /// <summary>Result of identifying a FHIR resource from file content.</summary>
    public record IdentificationResult(
        string? ResourceType,
        string? Name,
        string? Url,
        string? Title);

    /// <summary>
    /// Attempts to identify the FHIR resource type from a file's content.
    /// Uses both filename prefix detection and content-based detection.
    /// </summary>
    public static IdentificationResult? TryIdentify(string filePath, string? content)
    {
        // Start with filename-based detection
        string fileName = Path.GetFileName(filePath);
        string? filenameType = FhirResourceTypes.TryGetFromFilename(fileName);

        if (filenameType is not null)
        {
            return new IdentificationResult(filenameType, null, null, null);
        }

        if (string.IsNullOrWhiteSpace(content))
            return null;

        // Content-based detection: look for resource type in XML root element or JSON resourceType
        string trimmed = content.TrimStart();

        if (trimmed.StartsWith('<'))
        {
            return TryIdentifyFromXml(trimmed);
        }

        if (trimmed.StartsWith('{'))
        {
            return TryIdentifyFromJson(trimmed);
        }

        return null;
    }

    private static IdentificationResult? TryIdentifyFromXml(string content)
    {
        // Extract root element name: <ElementName ... >
        int start = content.IndexOf('<');
        if (start < 0) return null;

        // Skip XML declaration
        if (content[start..].StartsWith("<?"))
        {
            int declEnd = content.IndexOf("?>", start);
            if (declEnd < 0) return null;
            start = content.IndexOf('<', declEnd + 2);
            if (start < 0) return null;
        }

        // Skip comments
        while (content[start..].StartsWith("<!--"))
        {
            int commentEnd = content.IndexOf("-->", start);
            if (commentEnd < 0) return null;
            start = content.IndexOf('<', commentEnd + 3);
            if (start < 0) return null;
        }

        int nameStart = start + 1;
        int nameEnd = content.IndexOfAny([' ', '>', '/', '\r', '\n'], nameStart);
        if (nameEnd < 0) return null;

        string rootElement = content[nameStart..nameEnd];

        if (FhirResourceTypes.FilenamePrefixTypes.Contains(rootElement))
        {
            return new IdentificationResult(rootElement, null, null, null);
        }

        return null;
    }

    private static IdentificationResult? TryIdentifyFromJson(string content)
    {
        // Simple extraction: look for "resourceType": "..." near the start
        const string marker = "\"resourceType\"";
        int idx = content.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0 || idx > 200) return null;

        int colonIdx = content.IndexOf(':', idx + marker.Length);
        if (colonIdx < 0) return null;

        int quoteStart = content.IndexOf('"', colonIdx + 1);
        if (quoteStart < 0) return null;

        int quoteEnd = content.IndexOf('"', quoteStart + 1);
        if (quoteEnd < 0) return null;

        string resourceType = content[(quoteStart + 1)..quoteEnd];

        if (FhirResourceTypes.FilenamePrefixTypes.Contains(resourceType))
        {
            return new IdentificationResult(resourceType, null, null, null);
        }

        return null;
    }
}
