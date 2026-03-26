using FhirAugury.Common.Text;

namespace FhirAugury.Source.Zulip.Ingestion;

/// <summary>Converts Zulip HTML message content to plain text for indexing.</summary>
public static class ZulipContentProcessor
{
    /// <summary>Strips HTML and normalizes the result for FTS5 indexing.</summary>
    public static string HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        return TextSanitizer.StripHtml(html) ?? string.Empty;
    }
}
