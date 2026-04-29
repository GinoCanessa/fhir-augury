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
            string text = JoinNonEmpty(
                issue.DescriptionPlain, issue.Summary,
                issue.ResolutionDescriptionPlain, issue.RelatedArtifacts);

            refCount += ExtractAndInsertAll(connection, issue.Key, ContentTypes.Issue, text);
        }

        List<JiraProjectScopeStatementRecord> pss = JiraProjectScopeStatementRecord.SelectList(connection);
        foreach (JiraProjectScopeStatementRecord row in pss)
        {
            ct.ThrowIfCancellationRequested();
            string text = JoinNonEmpty(
                row.DescriptionPlain,
                row.ProjectDescriptionPlain,
                row.ProjectNeedPlain,
                row.ProjectDependenciesPlain);

            refCount += ExtractAndInsertAll(connection, row.Key, ContentTypes.Issue, text);
        }

        List<JiraBaldefRecord> baldefs = JiraBaldefRecord.SelectList(connection);
        foreach (JiraBaldefRecord row in baldefs)
        {
            ct.ThrowIfCancellationRequested();
            string text = JoinNonEmpty(
                row.DescriptionPlain,
                row.OrganizationalParticipationPlain);

            refCount += ExtractAndInsertAll(connection, row.Key, ContentTypes.Issue, text);
        }

        List<JiraBallotRecord> ballots = JiraBallotRecord.SelectList(connection);
        foreach (JiraBallotRecord row in ballots)
        {
            ct.ThrowIfCancellationRequested();
            // BALLOT.DescriptionPlain is typically empty (plan §2.2); the
            // spec-change ↔ ballot-comment relationship is queryable via
            // jira_ballot.RelatedFhirIssue and jira_issue_links, so no
            // new xref kind is needed here.
            refCount += ExtractAndInsertAll(connection, row.Key, ContentTypes.Issue, row.DescriptionPlain);
        }

        List<JiraCommentRecord> comments = JiraCommentRecord.SelectList(connection);
        foreach (JiraCommentRecord comment in comments)
        {
            ct.ThrowIfCancellationRequested();
            refCount += ExtractAndInsertAll(connection, comment.IssueKey, ContentTypes.Comment, comment.BodyPlain);
        }

        logger.LogInformation(
            "Rebuilt cross-references: {RefCount} refs from {IssueCount} issues, {PssCount} PSS, {BaldefCount} BALDEF, {BallotCount} BALLOT and {CommentCount} comments",
            refCount, issues.Count, pss.Count, baldefs.Count, ballots.Count, comments.Count);
    }

    private static string JoinNonEmpty(params string?[] parts)
        => string.Join(" ", parts.Where(static s => !string.IsNullOrEmpty(s)));

    private static int ExtractAndInsertAll(SqliteConnection connection, string sourceId, string contentType, string? text)
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
