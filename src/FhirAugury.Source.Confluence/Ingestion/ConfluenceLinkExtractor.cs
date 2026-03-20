using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FhirAugury.Source.Confluence.Ingestion;

/// <summary>Extracts internal page links from Confluence storage format content.</summary>
public static partial class ConfluenceLinkExtractor
{
    [GeneratedRegex(@"/pages/(\d+)", RegexOptions.Compiled)]
    private static partial Regex PageIdFromUrlRegex();

    /// <summary>
    /// Extracts internal page link target IDs from Confluence storage format content.
    /// </summary>
    /// <returns>List of (targetPageId, linkType) tuples.</returns>
    public static List<(string TargetPageId, string LinkType)> ExtractLinks(string? storageContent)
    {
        var links = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(storageContent))
            return links;

        try
        {
            var wrapped = $"<root xmlns:ac=\"http://atlassian.com/content\" xmlns:ri=\"http://atlassian.com/resource\">{storageContent}</root>";
            var doc = XDocument.Parse(wrapped);
            ExtractLinksFromElement(doc.Root!, links);
        }
        catch
        {
            // Fall back to regex extraction for malformed storage format
            ExtractLinksFromRegex(storageContent, links);
        }

        return links.DistinctBy(l => l.Item1).ToList();
    }

    private static void ExtractLinksFromElement(XElement element, List<(string, string)> links)
    {
        foreach (var child in element.Elements())
        {
            var localName = child.Name.LocalName;

            // <ri:page ri:content-id="12345" /> or <ri:page ri:space-key="..." ri:content-title="..." />
            if (localName == "page")
            {
                var contentId = child.Attributes()
                    .FirstOrDefault(a => a.Name.LocalName == "content-id")?.Value;
                if (!string.IsNullOrEmpty(contentId))
                {
                    links.Add((contentId, "page-link"));
                    continue;
                }
            }

            // <ac:link> containing <ri:page /> children
            if (localName == "link")
            {
                var pageRef = child.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "page");
                if (pageRef is not null)
                {
                    var contentId = pageRef.Attributes()
                        .FirstOrDefault(a => a.Name.LocalName == "content-id")?.Value;
                    if (!string.IsNullOrEmpty(contentId))
                    {
                        links.Add((contentId, "ac-link"));
                        continue;
                    }
                }
            }

            // <a href="/pages/12345/..."> standard HTML links
            if (localName == "a")
            {
                var href = child.Attribute("href")?.Value;
                if (!string.IsNullOrEmpty(href))
                {
                    var match = PageIdFromUrlRegex().Match(href);
                    if (match.Success)
                        links.Add((match.Groups[1].Value, "href"));
                }
            }

            ExtractLinksFromElement(child, links);
        }
    }

    private static void ExtractLinksFromRegex(string content, List<(string, string)> links)
    {
        // Extract content-id attributes
        var contentIdPattern = new Regex(@"content-id\s*=\s*[""'](\d+)[""']", RegexOptions.Compiled);
        foreach (Match match in contentIdPattern.Matches(content))
        {
            links.Add((match.Groups[1].Value, "page-link"));
        }

        // Extract /pages/{id} from href
        foreach (Match match in PageIdFromUrlRegex().Matches(content))
        {
            links.Add((match.Groups[1].Value, "href"));
        }
    }
}
