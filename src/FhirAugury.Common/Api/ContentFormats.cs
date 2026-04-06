namespace FhirAugury.Common.Api;

/// <summary>
/// Standard content format identifiers used across all source services.
/// </summary>
public static class ContentFormats
{
    /// <summary>Plain text (default).</summary>
    public const string Text = "text";

    /// <summary>Rendered HTML.</summary>
    public const string Html = "html";

    /// <summary>Source-native storage format (e.g., Confluence storage XML, GitHub markdown).</summary>
    public const string Raw = "raw";
}
