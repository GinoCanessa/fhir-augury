using System.Text.RegularExpressions;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Text;

namespace FhirAugury.Common.Indexing;

/// <summary>
/// Extracts Confluence page URL references from text.
/// Pure detection — text in, typed records out. No storage or DI dependencies.
/// </summary>
public static partial class ConfluenceReferenceExtractor
{
    [GeneratedRegex(@"https?://confluence\.hl7\.org/.*?/(\d+)")]
    private static partial Regex ConfluenceUrlRegex();

    public static List<ConfluenceXRefRecord> GetReferences(
        string sourceType, string sourceId, params string[] texts)
    {
        List<ConfluenceXRefRecord> results = [];
        HashSet<string> seen = [];

        foreach (string text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            foreach (Match match in ConfluenceUrlRegex().Matches(text))
            {
                string pageId = match.Groups[1].Value;
                if (!seen.Add(pageId)) continue;

                results.Add(new ConfluenceXRefRecord
                {
                    Id = ConfluenceXRefRecord.GetIndex(),
                    ContentType = sourceType,
                    SourceId = sourceId,
                    LinkType = "mentions",
                    Context = CrossRefPatterns.GetSurroundingText(text, match.Index, 160),
                    PageId = pageId,
                });
            }
        }
        return results;
    }
}
