using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Indexing;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Scans GitHub content (issues, PRs, comments, commit messages) for
/// cross-references to Jira, Zulip, Confluence, and FHIR elements.
/// </summary>
public class GitHubXRefRebuilder(GitHubDatabase database, ILogger<GitHubXRefRebuilder> logger)
{

    /// <summary>
    /// Extracts cross-references from all issues, comments, and commits in the database.
    /// </summary>
    public void RebuildAll(string repoFullName, HashSet<int>? validJiraNumbers = null, CancellationToken ct = default)
    {
        using SqliteConnection connection = database.OpenConnection();

        // Clear existing cross-reference tables
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM xref_jira;
                DELETE FROM xref_zulip;
                DELETE FROM xref_confluence;
                DELETE FROM xref_fhir_element;
                """;
            cmd.ExecuteNonQuery();
        }

        int refCount = 0;

        // Scan issues
        List<GitHubIssueRecord> allIssues = GitHubIssueRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (GitHubIssueRecord issue in allIssues)
        {
            ct.ThrowIfCancellationRequested();
            string text = $"{issue.Title} {issue.Body}";
            refCount += ExtractAndInsertAll(connection, text, "issue", issue.UniqueKey, validJiraNumbers);
        }

        // Scan comments
        List<GitHubCommentRecord> allComments = GitHubCommentRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (GitHubCommentRecord comment in allComments)
        {
            ct.ThrowIfCancellationRequested();
            string sourceId = $"{comment.RepoFullName}#{comment.IssueNumber}:{comment.Id}";
            refCount += ExtractAndInsertAll(connection, comment.Body, "comment", sourceId, validJiraNumbers);
        }

        // Scan commits
        List<GitHubCommitRecord> allCommits = GitHubCommitRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (GitHubCommitRecord commit in allCommits)
        {
            ct.ThrowIfCancellationRequested();
            refCount += ExtractAndInsertAll(connection, commit.Message, "commit", commit.Sha, validJiraNumbers);
        }

        // Scan file contents
        List<GitHubFileContentRecord> fileContents = GitHubFileContentRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (GitHubFileContentRecord file in fileContents)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(file.ContentText))
            {
                string sourceId = $"{file.RepoFullName}:{file.FilePath}";
                refCount += ExtractAndInsertAll(connection, file.ContentText, "file", sourceId, validJiraNumbers);
            }
        }

        logger.LogInformation("Extracted {Count} cross-references from {Repo}", refCount, repoFullName);
    }

    /// <summary>
    /// Runs all shared extractors on a single text and inserts all results into the xref tables.
    /// </summary>
    private static int ExtractAndInsertAll(
        SqliteConnection connection,
        string text,
        string sourceType,
        string sourceId,
        HashSet<int>? validJiraNumbers)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int count = 0;

        // Jira: shared extractor (FHIR-N, JF-N, GF-N, J#N, GF#N, Jira URLs)
        List<JiraXRefRecord> jiraRefs = ExtractJiraReferences(text, sourceType, sourceId, validJiraNumbers);
        foreach (JiraXRefRecord r in jiraRefs)
        {
            r.Id = JiraXRefRecord.GetIndex();
            JiraXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        // Zulip
        foreach (ZulipXRefRecord r in ZulipReferenceExtractor.GetReferences(sourceType, sourceId, text))
        {
            r.Id = ZulipXRefRecord.GetIndex();
            ZulipXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        // Confluence
        foreach (ConfluenceXRefRecord r in ConfluenceReferenceExtractor.GetReferences(sourceType, sourceId, text))
        {
            r.Id = ConfluenceXRefRecord.GetIndex();
            ConfluenceXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        // FHIR elements
        foreach (FhirElementXRefRecord r in FhirElementReferenceExtractor.GetReferences(sourceType, sourceId, text))
        {
            r.Id = FhirElementXRefRecord.GetIndex();
            FhirElementXRefRecord.Insert(connection, r, ignoreDuplicates: true);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Extracts Jira references using the shared extractor (FHIR-N, JF-N, GF-N, J#N, GF#N, Jira URLs).
    /// </summary>
    internal static List<JiraXRefRecord> ExtractJiraReferences(
        string text,
        string sourceType,
        string sourceId,
        HashSet<int>? validJiraNumbers)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        // Shared extractor for standard patterns (FHIR-N, JF-N, GF-N, J#N, GF#N, Jira URLs)
        CrossRefExtractionContext? context = validJiraNumbers is not null
            ? new CrossRefExtractionContext(ValidJiraNumbers: validJiraNumbers)
            : null;
        return JiraReferenceExtractor.GetReferences(sourceType, sourceId, context, text);
    }
}
