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
    public void ExtractAll(CancellationToken ct = default)
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

            foreach (ZulipXRefRecord r in ZulipReferenceExtractor.GetReferences("issue", issue.Key, text))
            {
                r.Id = ZulipXRefRecord.GetIndex();
                ZulipXRefRecord.Insert(connection, r, ignoreDuplicates: true);
                refCount++;
            }

            foreach (GitHubXRefRecord r in GitHubReferenceExtractor.GetReferences("issue", issue.Key, text))
            {
                r.Id = GitHubXRefRecord.GetIndex();
                GitHubXRefRecord.Insert(connection, r, ignoreDuplicates: true);
                refCount++;
            }

            foreach (ConfluenceXRefRecord r in ConfluenceReferenceExtractor.GetReferences("issue", issue.Key, text))
            {
                r.Id = ConfluenceXRefRecord.GetIndex();
                ConfluenceXRefRecord.Insert(connection, r, ignoreDuplicates: true);
                refCount++;
            }

            foreach (FhirElementXRefRecord r in FhirElementReferenceExtractor.GetReferences("issue", issue.Key, text))
            {
                r.Id = FhirElementXRefRecord.GetIndex();
                FhirElementXRefRecord.Insert(connection, r, ignoreDuplicates: true);
                refCount++;
            }
        }

        List<JiraCommentRecord> comments = JiraCommentRecord.SelectList(connection);
        foreach (JiraCommentRecord comment in comments)
        {
            ct.ThrowIfCancellationRequested();

            foreach (ZulipXRefRecord r in ZulipReferenceExtractor.GetReferences("comment", comment.IssueKey, comment.BodyPlain))
            {
                r.Id = ZulipXRefRecord.GetIndex();
                ZulipXRefRecord.Insert(connection, r, ignoreDuplicates: true);
                refCount++;
            }

            foreach (GitHubXRefRecord r in GitHubReferenceExtractor.GetReferences("comment", comment.IssueKey, comment.BodyPlain))
            {
                r.Id = GitHubXRefRecord.GetIndex();
                GitHubXRefRecord.Insert(connection, r, ignoreDuplicates: true);
                refCount++;
            }

            foreach (ConfluenceXRefRecord r in ConfluenceReferenceExtractor.GetReferences("comment", comment.IssueKey, comment.BodyPlain))
            {
                r.Id = ConfluenceXRefRecord.GetIndex();
                ConfluenceXRefRecord.Insert(connection, r, ignoreDuplicates: true);
                refCount++;
            }

            foreach (FhirElementXRefRecord r in FhirElementReferenceExtractor.GetReferences("comment", comment.IssueKey, comment.BodyPlain))
            {
                r.Id = FhirElementXRefRecord.GetIndex();
                FhirElementXRefRecord.Insert(connection, r, ignoreDuplicates: true);
                refCount++;
            }
        }

        logger.LogInformation("Rebuilt cross-references: {RefCount} refs from {IssueCount} issues and {CommentCount} comments",
            refCount, issues.Count, comments.Count);
    }
}
