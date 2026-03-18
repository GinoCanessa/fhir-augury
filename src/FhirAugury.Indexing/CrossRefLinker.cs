using System.Text.RegularExpressions;
using FhirAugury.Database.Records;
using FhirAugury.Models;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Indexing;

/// <summary>
/// Scans text fields for cross-source identifiers and populates the xref_links table.
/// </summary>
public static partial class CrossRefLinker
{
    // Compiled regex patterns for identifier extraction
    [GeneratedRegex(@"\b(FHIR-\d+)\b")]
    private static partial Regex JiraKeyRegex();

    [GeneratedRegex(@"https?://jira\.hl7\.org/browse/(FHIR-\d+)")]
    private static partial Regex JiraUrlRegex();

    [GeneratedRegex(@"https?://chat\.fhir\.org/#narrow/stream/(\d+)[^/]*/topic/(.+?)(?:\s|$)")]
    private static partial Regex ZulipUrlRegex();

    [GeneratedRegex(@"https?://github\.com/(HL7/[^/]+)/issues/(\d+)")]
    private static partial Regex GitHubIssueUrlRegex();

    [GeneratedRegex(@"https?://confluence\.hl7\.org/.*?/(\d+)")]
    private static partial Regex ConfluenceUrlRegex();

    /// <summary>
    /// Extracts cross-reference links from a text string.
    /// </summary>
    /// <param name="text">The text to scan for identifiers.</param>
    /// <returns>A list of (TargetType, TargetId, Context) tuples.</returns>
    public static List<(string TargetType, string TargetId, string Context)> ExtractLinks(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var results = new List<(string TargetType, string TargetId, string Context)>();
        var seen = new HashSet<(string, string)>();

        // Jira URLs (check before Jira keys to avoid double-matching)
        foreach (Match match in JiraUrlRegex().Matches(text))
        {
            var key = (TargetType: "jira", TargetId: match.Groups[1].Value);
            if (seen.Add(key))
            {
                results.Add((key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
            }
        }

        // Jira keys (skip if already found via URL)
        foreach (Match match in JiraKeyRegex().Matches(text))
        {
            var key = (TargetType: "jira", TargetId: match.Groups[1].Value);
            if (seen.Add(key))
            {
                results.Add((key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
            }
        }

        // Zulip URLs
        foreach (Match match in ZulipUrlRegex().Matches(text))
        {
            var streamId = match.Groups[1].Value;
            var topic = Uri.UnescapeDataString(match.Groups[2].Value.TrimEnd());
            var key = (TargetType: "zulip", TargetId: $"{streamId}:{topic}");
            if (seen.Add(key))
            {
                results.Add((key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
            }
        }

        // GitHub issue URLs
        foreach (Match match in GitHubIssueUrlRegex().Matches(text))
        {
            var repo = match.Groups[1].Value;
            var number = match.Groups[2].Value;
            var key = (TargetType: "github", TargetId: $"{repo}#{number}");
            if (seen.Add(key))
            {
                results.Add((key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
            }
        }

        // Confluence URLs
        foreach (Match match in ConfluenceUrlRegex().Matches(text))
        {
            var pageId = match.Groups[1].Value;
            var key = (TargetType: "confluence", TargetId: pageId);
            if (seen.Add(key))
            {
                results.Add((key.TargetType, key.TargetId, GetSurroundingText(text, match.Index)));
            }
        }

        return results;
    }

    /// <summary>
    /// Extracts ~100 characters of surrounding text around a match position.
    /// </summary>
    public static string GetSurroundingText(string fullText, int matchIndex, int contextChars = 100)
    {
        var halfContext = contextChars / 2;
        var start = Math.Max(0, matchIndex - halfContext);
        var end = Math.Min(fullText.Length, matchIndex + halfContext);
        var context = fullText[start..end].ReplaceLineEndings(" ").Trim();
        if (start > 0) context = "..." + context;
        if (end < fullText.Length) context += "...";
        return context;
    }

    /// <summary>
    /// Processes newly ingested items and creates cross-reference links.
    /// </summary>
    public static void LinkNewItems(SqliteConnection connection, IngestionResult result)
    {
        foreach (var item in result.NewAndUpdatedItems)
        {
            LinkItem(connection, item.SourceType, item.SourceId, item.SearchableTextFields);
        }
    }

    /// <summary>
    /// Creates cross-reference links for a single item's text fields.
    /// </summary>
    private static void LinkItem(
        SqliteConnection connection,
        string sourceType,
        string sourceId,
        IReadOnlyList<string> textFields)
    {
        // Delete existing links for this source item
        DeleteLinksForSource(connection, sourceType, sourceId);

        var seen = new HashSet<(string, string)>();

        foreach (var text in textFields)
        {
            var links = ExtractLinks(text);
            foreach (var (targetType, targetId, context) in links)
            {
                // Avoid self-links
                if (targetType == sourceType && targetId == sourceId)
                {
                    continue;
                }

                // Deduplicate across text fields
                if (!seen.Add((targetType, targetId)))
                {
                    continue;
                }

                var record = new CrossRefLinkRecord
                {
                    Id = CrossRefLinkRecord.GetIndex(),
                    SourceType = sourceType,
                    SourceId = sourceId,
                    TargetType = targetType,
                    TargetId = targetId,
                    LinkType = "mention",
                    Context = context,
                };
                CrossRefLinkRecord.Insert(connection, record);
            }
        }
    }

    /// <summary>
    /// Rebuilds all cross-reference links by scanning all text fields in all source tables.
    /// </summary>
    public static void RebuildAllLinks(SqliteConnection connection, CancellationToken ct = default)
    {
        // Clear all existing links
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM xref_links;";
            cmd.ExecuteNonQuery();
        }

        // Scan Jira issues
        var issues = JiraIssueRecord.SelectList(connection);
        foreach (var issue in issues)
        {
            ct.ThrowIfCancellationRequested();

            var textFields = new List<string>();
            if (!string.IsNullOrEmpty(issue.Title)) textFields.Add(issue.Title);
            if (!string.IsNullOrEmpty(issue.Description)) textFields.Add(issue.Description);
            if (!string.IsNullOrEmpty(issue.Summary)) textFields.Add(issue.Summary);
            if (!string.IsNullOrEmpty(issue.ResolutionDescription)) textFields.Add(issue.ResolutionDescription);
            if (!string.IsNullOrEmpty(issue.RelatedArtifacts)) textFields.Add(issue.RelatedArtifacts);

            LinkItem(connection, "jira", issue.Key, textFields);
        }

        // Scan Jira comments
        var comments = JiraCommentRecord.SelectList(connection);
        foreach (var comment in comments)
        {
            ct.ThrowIfCancellationRequested();

            var textFields = new List<string>();
            if (!string.IsNullOrEmpty(comment.Body)) textFields.Add(comment.Body);

            LinkItem(connection, "jira-comment", $"{comment.IssueKey}:{comment.Id}", textFields);
        }

        // Scan Zulip messages
        var messages = ZulipMessageRecord.SelectList(connection);
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();

            var textFields = new List<string>();
            if (!string.IsNullOrEmpty(msg.ContentPlain)) textFields.Add(msg.ContentPlain);

            LinkItem(connection, "zulip", $"{msg.StreamName}:{msg.Topic}", textFields);
        }
    }

    private static void DeleteLinksForSource(SqliteConnection connection, string sourceType, string sourceId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM xref_links WHERE SourceType = @sourceType AND SourceId = @sourceId;";
        cmd.Parameters.AddWithValue("@sourceType", sourceType);
        cmd.Parameters.AddWithValue("@sourceId", sourceId);
        cmd.ExecuteNonQuery();
    }
}
