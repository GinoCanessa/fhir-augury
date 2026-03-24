using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FhirAugury.Source.Confluence.Ingestion;

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
    public static string ToPlainText(string? storageContent)
    {
        if (string.IsNullOrWhiteSpace(storageContent))
            return string.Empty;

        try
        {
            string wrapped = $"<root xmlns:ac=\"http://atlassian.com/content\" xmlns:ri=\"http://atlassian.com/resource\">{storageContent}</root>";
            XDocument doc = XDocument.Parse(wrapped);

            string text = ExtractTextFromElement(doc.Root!);
            text = WhitespaceRegex().Replace(text, " ").Trim();
            return text;
        }
        catch
        {
            return StripHtmlFallback(storageContent);
        }
    }

    private static string ExtractTextFromElement(XElement element)
    {
        List<string> parts = new List<string>();

        foreach (XNode node in element.Nodes())
        {
            switch (node)
            {
                case XText textNode:
                    string text = textNode.Value.Trim();
                    if (!string.IsNullOrEmpty(text))
                        parts.Add(text);
                    break;

                case XElement childElement:
                    string localName = childElement.Name.LocalName;

                    if (localName == "structured-macro")
                    {
                        XElement? plainBody = childElement.Descendants()
                            .FirstOrDefault(e => e.Name.LocalName == "plain-text-body");
                        if (plainBody is not null)
                            parts.Add(plainBody.Value.Trim());
                        continue;
                    }

                    if (localName == "image")
                    {
                        string? alt = childElement.Attribute("alt")?.Value
                                  ?? childElement.Attribute("ac:alt")?.Value;
                        if (!string.IsNullOrEmpty(alt))
                            parts.Add(alt);
                        continue;
                    }

                    if (localName == "attachment")
                        continue;

                    string childText = ExtractTextFromElement(childElement);
                    if (!string.IsNullOrEmpty(childText))
                        parts.Add(childText);
                    break;
            }
        }

        return string.Join(" ", parts);
    }

    private static string StripHtmlFallback(string html)
    {
        string text = HtmlTagRegex().Replace(html, " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        return text;
    }
}
