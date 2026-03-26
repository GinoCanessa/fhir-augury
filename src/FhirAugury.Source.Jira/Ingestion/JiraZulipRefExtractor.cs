using System.Text.RegularExpressions;
using FhirAugury.Common.Text;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Jira.Ingestion;

public partial class JiraZulipRefExtractor(JiraDatabase database, ILogger<JiraZulipRefExtractor> logger)
{
    [GeneratedRegex(
        @"https?://chat\.fhir\.org/#narrow/(?:stream|channel)/(\d+)-([^/\s]*)"
        + @"(?:/topic/([^\s?#/]+))?"
        + @"(?:/(?:with|near)/(\d+))?",
        RegexOptions.Compiled)]
    private static partial Regex ZulipUrlPattern();

    public void ExtractAll(CancellationToken ct = default)
    {
        using SqliteConnection connection = database.OpenConnection();

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM jira_zulip_refs";
            cmd.ExecuteNonQuery();
        }

        int refCount = 0;

        List<JiraIssueRecord> issues = JiraIssueRecord.SelectList(connection);
        foreach (JiraIssueRecord issue in issues)
        {
            ct.ThrowIfCancellationRequested();
            string text = string.Join(" ",
                new[] { issue.DescriptionPlain, issue.Summary,
                        issue.ResolutionDescriptionPlain, issue.RelatedArtifacts }
                    .Where(s => !string.IsNullOrEmpty(s)));

            foreach (JiraZulipRefRecord zRef in ExtractReferences(text, issue.Key, "issue"))
            {
                JiraZulipRefRecord.Insert(connection, zRef, ignoreDuplicates: true);
                refCount++;
            }
        }

        List<JiraCommentRecord> comments = JiraCommentRecord.SelectList(connection);
        foreach (JiraCommentRecord comment in comments)
        {
            ct.ThrowIfCancellationRequested();
            foreach (JiraZulipRefRecord zRef in ExtractReferences(
                comment.BodyPlain, comment.IssueKey, "comment"))
            {
                JiraZulipRefRecord.Insert(connection, zRef, ignoreDuplicates: true);
                refCount++;
            }
        }

        logger.LogInformation("Extracted {Count} Zulip references from Jira content", refCount);
    }

    internal static List<JiraZulipRefRecord> ExtractReferences(
        string? text, string issueKey, string sourceType)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        List<JiraZulipRefRecord> results = [];
        HashSet<string> seen = [];

        foreach (Match match in ZulipUrlPattern().Matches(text))
        {
            string url = match.Value;
            if (!seen.Add(url)) continue;

            int streamId = int.Parse(match.Groups[1].Value);
            string? streamName = match.Groups[2].Success
                ? Uri.UnescapeDataString(match.Groups[2].Value) : null;
            string? topicName = match.Groups[3].Success
                ? Uri.UnescapeDataString(match.Groups[3].Value) : null;
            int? messageId = match.Groups[4].Success
                ? int.Parse(match.Groups[4].Value) : null;

            results.Add(new JiraZulipRefRecord
            {
                Id = JiraZulipRefRecord.GetIndex(),
                IssueKey = issueKey,
                SourceType = sourceType,
                Url = url,
                StreamId = streamId,
                StreamName = streamName,
                TopicName = topicName,
                MessageId = messageId,
                Context = CrossRefPatterns.GetSurroundingText(text, match.Index, 160),
            });
        }

        return results;
    }
}
