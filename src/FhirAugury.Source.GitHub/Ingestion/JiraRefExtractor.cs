using System.Text.RegularExpressions;
using FhirAugury.Common.Database.Records;
using FhirAugury.Common.Indexing;
using FhirAugury.Common.Text;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Scans GitHub content (issues, PRs, comments, commit messages) for
/// cross-references to Jira, Zulip, Confluence, and FHIR elements.
/// Keeps the GitHub-specific bare #NNN → Jira disambiguation logic.
/// </summary>
public partial class JiraRefExtractor(GitHubDatabase database, ILogger<JiraRefExtractor> logger)
{
    // Bare #NNN reference (ambiguous — needs GitHub-specific disambiguation)
    [GeneratedRegex(@"(?<![a-zA-Z\d/])#(\d+)\b", RegexOptions.Compiled)]
    private static partial Regex BareHashPattern();

    /// <summary>
    /// Extracts cross-references from all issues, comments, and commits in the database.
    /// </summary>
    public void ExtractAll(string repoFullName, HashSet<int>? validJiraNumbers = null, CancellationToken ct = default)
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

        // Determine if repo has issues (for #NNN disambiguation)
        GitHubRepoRecord? repo = GitHubRepoRecord.SelectSingle(connection, FullName: repoFullName);
        bool hasIssues = repo?.HasIssues ?? true;

        // Get all GitHub issue/PR numbers for this repo (for disambiguation)
        HashSet<int> githubNumbers = [];
        if (hasIssues)
        {
            List<GitHubIssueRecord> issues = GitHubIssueRecord.SelectList(connection, RepoFullName: repoFullName);
            foreach (GitHubIssueRecord issue in issues)
                githubNumbers.Add(issue.Number);
        }

        int refCount = 0;

        // Scan issues
        List<GitHubIssueRecord> allIssues = GitHubIssueRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (GitHubIssueRecord issue in allIssues)
        {
            ct.ThrowIfCancellationRequested();
            string text = $"{issue.Title} {issue.Body}";
            refCount += ExtractAndInsertAll(connection, text, "issue", issue.UniqueKey, hasIssues, githubNumbers, validJiraNumbers);
        }

        // Scan comments
        List<GitHubCommentRecord> allComments = GitHubCommentRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (GitHubCommentRecord comment in allComments)
        {
            ct.ThrowIfCancellationRequested();
            string sourceId = $"{comment.RepoFullName}#{comment.IssueNumber}:{comment.Id}";
            refCount += ExtractAndInsertAll(connection, comment.Body, "comment", sourceId, hasIssues, githubNumbers, validJiraNumbers);
        }

        // Scan commits
        List<GitHubCommitRecord> allCommits = GitHubCommitRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (GitHubCommitRecord commit in allCommits)
        {
            ct.ThrowIfCancellationRequested();
            refCount += ExtractAndInsertAll(connection, commit.Message, "commit", commit.Sha, hasIssues, githubNumbers, validJiraNumbers);
        }

        // Scan file contents
        List<GitHubFileContentRecord> fileContents = GitHubFileContentRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (GitHubFileContentRecord file in fileContents)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(file.ContentText))
            {
                string sourceId = $"{file.RepoFullName}:{file.FilePath}";
                refCount += ExtractAndInsertAll(connection, file.ContentText, "file", sourceId, hasIssues, githubNumbers, validJiraNumbers);
            }
        }

        logger.LogInformation("Extracted {Count} cross-references from {Repo}", refCount, repoFullName);
    }

    /// <summary>
    /// Runs all shared extractors plus GitHub-specific bare #NNN Jira disambiguation
    /// on a single text and inserts all results into the xref tables.
    /// </summary>
    private static int ExtractAndInsertAll(
        SqliteConnection connection,
        string text,
        string sourceType,
        string sourceId,
        bool hasIssues,
        HashSet<int> githubNumbers,
        HashSet<int>? validJiraNumbers)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int count = 0;

        // Jira: shared extractor + GitHub-specific bare #NNN disambiguation
        List<JiraXRefRecord> jiraRefs = ExtractJiraReferences(text, sourceType, sourceId, hasIssues, githubNumbers, validJiraNumbers);
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
    /// Extracts Jira references using the shared extractor plus GitHub-specific
    /// bare #NNN disambiguation logic.
    /// </summary>
    internal static List<JiraXRefRecord> ExtractJiraReferences(
        string text,
        string sourceType,
        string sourceId,
        bool hasIssues,
        HashSet<int> githubNumbers,
        HashSet<int>? validJiraNumbers)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        // Shared extractor for standard patterns (FHIR-N, JF-N, GF-N, J#N, GF#N, Jira URLs)
        CrossRefExtractionContext? context = validJiraNumbers is not null
            ? new CrossRefExtractionContext(ValidJiraNumbers: validJiraNumbers)
            : null;
        List<JiraXRefRecord> results = JiraReferenceExtractor.GetReferences(sourceType, sourceId, context, text);
        HashSet<string> seen = [];
        foreach (JiraXRefRecord r in results)
            seen.Add(r.JiraKey);

        // GitHub-specific: bare #NNN disambiguation (not in shared extractor)
        foreach (Match m in BareHashPattern().Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out int number)) continue;

            if (hasIssues && githubNumbers.Contains(number))
                continue; // It's a GitHub issue reference, skip

            string jiraKey = $"FHIR-{number}";
            if (!seen.Add(jiraKey)) continue;

            if (validJiraNumbers is not null && !validJiraNumbers.Contains(number))
                continue;

            results.Add(new JiraXRefRecord
            {
                Id = 0,
                SourceType = sourceType,
                SourceId = sourceId,
                LinkType = "mentions",
                JiraKey = jiraKey,
                Context = CrossRefPatterns.GetSurroundingText(text, m.Index, 160),
            });
        }

        return results;
    }
}
