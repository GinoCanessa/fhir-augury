using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FhirAugury.Sources.Confluence;

/// <summary>Parses Confluence storage format (XHTML/XML) to plain text.</summary>
public static partial class ConfluenceContentParser
{
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Converts Confluence storage format content to plain text.
    /// Strips macros, preserves table content and alt text.
    /// </summary>
    /// <param name="storageContent">Confluence storage format (XHTML/XML) content.</param>
    /// <returns>Plain text representation of the content.</returns>
    public static string ToPlainText(string? storageContent)
    {
        if (string.IsNullOrWhiteSpace(storageContent))
        {
            return string.Empty;
        }

        try
        {
            // Wrap in a root element to ensure valid XML
            var wrapped = $"<root xmlns:ac=\"http://atlassian.com/content\" xmlns:ri=\"http://atlassian.com/resource\">{storageContent}</root>";
            var doc = XDocument.Parse(wrapped);

            var text = ExtractTextFromElement(doc.Root!);
            text = WhitespaceRegex().Replace(text, " ").Trim();
            return text;
        }
        catch
        {
            // If XML parsing fails, fall back to regex-based stripping
            return StripHtmlFallback(storageContent);
        }
    }

    private static string ExtractTextFromElement(XElement element)
    {
        var parts = new List<string>();

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText textNode:
                    var text = textNode.Value.Trim();
                    if (!string.IsNullOrEmpty(text))
                        parts.Add(text);
                    break;

                case XElement childElement:
                    var localName = childElement.Name.LocalName;

                    // Skip known non-content macros
                    if (localName == "structured-macro")
                    {
                        // Extract plain-text-body from macros if present
                        var plainBody = childElement.Descendants()
                            .FirstOrDefault(e => e.Name.LocalName == "plain-text-body");
                        if (plainBody is not null)
                        {
                            parts.Add(plainBody.Value.Trim());
                        }
                        continue;
                    }

                    // For images, keep alt text
                    if (localName == "image")
                    {
                        var alt = childElement.Attribute("alt")?.Value
                                  ?? childElement.Attribute("ac:alt")?.Value;
                        if (!string.IsNullOrEmpty(alt))
                            parts.Add(alt);
                        continue;
                    }

                    // Skip attachment references
                    if (localName == "attachment")
                        continue;

                    // Recurse into other elements (p, li, td, span, etc.)
                    var childText = ExtractTextFromElement(childElement);
                    if (!string.IsNullOrEmpty(childText))
                        parts.Add(childText);
                    break;
            }
        }

        return string.Join(" ", parts);
    }

    private static string StripHtmlFallback(string html)
    {
        var text = HtmlTagRegex().Replace(html, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text;
    }
}
