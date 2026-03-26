using System.Text.RegularExpressions;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Text;

namespace FhirAugury.Common.Indexing;

/// <summary>
/// Extracts Zulip chat URL references from text.
/// Pure detection — text in, typed records out. No storage or DI dependencies.
/// </summary>
public static partial class ZulipReferenceExtractor
{
    [GeneratedRegex(
        @"https?://chat\.fhir\.org/#narrow/(?:stream|channel)/(\d+)-([^/\s]*)"
        + @"(?:/topic/([^\s?#/]+))?"
        + @"(?:/(?:with|near)/(\d+))?",
        RegexOptions.Compiled)]
    private static partial Regex ZulipUrlRegex();

    public static List<ZulipXRefRecord> GetReferences(
        string sourceType, string sourceId, params string[] texts)
    {
        List<ZulipXRefRecord> results = [];
        HashSet<string> seen = [];

        foreach (string text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            foreach (Match match in ZulipUrlRegex().Matches(text))
            {
                string url = match.Value;
                if (!seen.Add(url)) continue;

                int streamId = int.Parse(match.Groups[1].Value);
                string? streamName = match.Groups[2].Success && match.Groups[2].Value.Length > 0
                    ? Uri.UnescapeDataString(match.Groups[2].Value) : null;
                string? topicName = match.Groups[3].Success
                    ? Uri.UnescapeDataString(match.Groups[3].Value) : null;
                int? messageId = match.Groups[4].Success
                    ? int.Parse(match.Groups[4].Value) : null;

                results.Add(new ZulipXRefRecord
                {
                    Id = 0,
                    SourceType = sourceType,
                    SourceId = sourceId,
                    LinkType = "mentions",
                    Context = CrossRefPatterns.GetSurroundingText(text, match.Index, 160),
                    StreamId = streamId,
                    StreamName = streamName,
                    TopicName = topicName,
                    MessageId = messageId,
                });
            }
        }
        return results;
    }
}
