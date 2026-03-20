using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FhirAugury.Common.Text;

/// <summary>
/// Defines the format of content for text extraction.
/// </summary>
public enum ContentFormat
{
    /// <summary>Plain text requiring no transformation.</summary>
    PlainText,

    /// <summary>HTML content with tags and entities.</summary>
    Html,

    /// <summary>Markdown-formatted content.</summary>
    Markdown,
}

/// <summary>
/// Static utility class for cleaning and normalizing text content.
/// </summary>
public static partial class TextSanitizer
{
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.Singleline)]
    private static partial Regex MarkdownCodeBlockRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^\)]+\)")]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"\[([^\]]*)\]\([^\)]+\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex MarkdownInlineCodeRegex();

    [GeneratedRegex(@"^#{1,6}\s+", RegexOptions.Multiline)]
    private static partial Regex MarkdownHeaderRegex();

    [GeneratedRegex(@"(\*\*|__)(.*?)\1", RegexOptions.Singleline)]
    private static partial Regex MarkdownBoldRegex();

    [GeneratedRegex(@"~~(.*?)~~", RegexOptions.Singleline)]
    private static partial Regex MarkdownStrikethroughRegex();

    [GeneratedRegex(@"(?<!\*)(\*|_)(?!\*)(.+?)\1", RegexOptions.Singleline)]
    private static partial Regex MarkdownItalicRegex();

    [GeneratedRegex(@"^[\s]*[-*+]\s+", RegexOptions.Multiline)]
    private static partial Regex MarkdownListRegex();

    [GeneratedRegex(@"^[\s]*>\s?", RegexOptions.Multiline)]
    private static partial Regex MarkdownBlockquoteRegex();

    [GeneratedRegex(@"^[-*_]{3,}\s*$", RegexOptions.Multiline)]
    private static partial Regex MarkdownHorizontalRuleRegex();

    /// <summary>
    /// Removes HTML tags, decodes HTML entities, and normalizes whitespace.
    /// </summary>
    /// <param name="html">The HTML content to strip, or null.</param>
    /// <returns>Plain text with tags removed and entities decoded, or empty string for null input.</returns>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var text = HtmlTagRegex().Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        text = WhitespaceRegex().Replace(text, " ");

        return text.Trim();
    }

    /// <summary>
    /// Removes common markdown syntax (headers, bold, italic, links, images, code blocks).
    /// </summary>
    /// <param name="md">The markdown content to strip, or null.</param>
    /// <returns>Plain text with markdown syntax removed, or empty string for null input.</returns>
    public static string StripMarkdown(string? md)
    {
        if (string.IsNullOrEmpty(md))
        {
            return string.Empty;
        }

        // Order matters: code blocks first, then inline elements
        var text = MarkdownCodeBlockRegex().Replace(md, " ");
        text = MarkdownImageRegex().Replace(text, "$1");
        text = MarkdownLinkRegex().Replace(text, "$1");
        text = MarkdownInlineCodeRegex().Replace(text, "$1");
        text = MarkdownHeaderRegex().Replace(text, "");
        text = MarkdownBoldRegex().Replace(text, "$2");
        text = MarkdownStrikethroughRegex().Replace(text, "$1");
        text = MarkdownItalicRegex().Replace(text, "$2");
        text = MarkdownListRegex().Replace(text, "");
        text = MarkdownBlockquoteRegex().Replace(text, "");
        text = MarkdownHorizontalRuleRegex().Replace(text, "");
        text = WhitespaceRegex().Replace(text, " ");

        return text.Trim();
    }

    /// <summary>
    /// Normalizes text to NFC (Canonical Decomposition, followed by Canonical Composition) Unicode form.
    /// </summary>
    /// <param name="text">The text to normalize, or null.</param>
    /// <returns>NFC-normalized text, or empty string for null input.</returns>
    public static string NormalizeUnicode(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Extracts plain text from content based on its format.
    /// </summary>
    /// <param name="content">The content to extract text from, or null.</param>
    /// <param name="format">The format of the content.</param>
    /// <returns>Plain text extracted from the content.</returns>
    public static string ExtractPlainText(string? content, ContentFormat format) => format switch
    {
        ContentFormat.Html => StripHtml(content),
        ContentFormat.Markdown => StripMarkdown(content),
        ContentFormat.PlainText => content ?? string.Empty,
        _ => content ?? string.Empty,
    };
}
