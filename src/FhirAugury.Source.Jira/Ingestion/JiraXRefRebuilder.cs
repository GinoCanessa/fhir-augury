using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Indexing;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Jira.Ingestion;

/// <summary>
/// Rebuilds all xref_* cross-reference tables by scanning all Jira issues and comments
/// and running shared extractors. Replaces the old JiraZulipRefExtractor.
/// </summary>
public class JiraXRefRebuilder(
    JiraDatabase database,
    ILogger<JiraXRefRebuilder> logger)
{
    public void RebuildAll(CancellationToken ct = default)
    {
        using SqliteConnection connection = database.OpenConnection();

        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM xref_zulip;
                DELETE FROM xref_github;
                DELETE FROM xref_confluence;
                DELETE FROM xref_fhir_element;
                """;
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

            refCount += ExtractAndInsertAll(connection, issue.Key, ContentTypes.Issue, text);
        }

        List<JiraCommentRecord> comments = JiraCommentRecord.SelectList(connection);
        foreach (JiraCommentRecord comment in comments)
        {
            ct.ThrowIfCancellationRequested();
            refCount += ExtractAndInsertAll(connection, comment.IssueKey, ContentTypes.Comment, comment.BodyPlain);
        }

        logger.LogInformation("Rebuilt cross-references: {RefCount} refs from {IssueCount} issues and {CommentCount} comments",
            refCount, issues.Count, comments.Count);
    }

    private static int ExtractAndInsertAll(SqliteConnection connection, string sourceId, string contentType, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int count = 0;

        foreach (ZulipXRefRecord r in ZulipReferenceExtractor.GetReferences(contentType, sourceId, text))
        {
            r.Id = ZulipXRefRecord.GetIndex();
            ZulipXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        foreach (GitHubXRefRecord r in GitHubReferenceExtractor.GetReferences(contentType, sourceId, text))
        {
            r.Id = GitHubXRefRecord.GetIndex();
            GitHubXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        foreach (ConfluenceXRefRecord r in ConfluenceReferenceExtractor.GetReferences(contentType, sourceId, text))
        {
            r.Id = ConfluenceXRefRecord.GetIndex();
            ConfluenceXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        foreach (FhirElementXRefRecord r in FhirElementReferenceExtractor.GetReferences(contentType, sourceId, text))
        {
            r.Id = FhirElementXRefRecord.GetIndex();
            FhirElementXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        return count;
    }
}
