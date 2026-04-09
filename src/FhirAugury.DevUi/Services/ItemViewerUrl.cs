namespace FhirAugury.DevUi.Services;

/// <summary>Builds URLs for the Item Viewer page with proper encoding.</summary>
public static class ItemViewerUrl
{
    public static string For(string source, string id) =>
        $"/item/{Uri.EscapeDataString(source)}/{EncodeIdForUrl(id, source)}";

    private static string EncodeIdForUrl(string id, string source)
    {
        // GitHub IDs contain slashes (HL7/fhir#4006) — keep slashes raw
        // for the catch-all route, but encode '#' as %23.
        if (source.Equals("github", StringComparison.OrdinalIgnoreCase))
            return id.Replace("#", "%23");
        return Uri.EscapeDataString(id);
    }
}
