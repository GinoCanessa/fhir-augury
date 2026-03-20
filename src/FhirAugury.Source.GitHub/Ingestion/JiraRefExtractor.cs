using System.Text.RegularExpressions;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Scans GitHub content (issues, PRs, comments, commit messages) for
/// Jira issue references (FHIR-N, JF-N, J#N, GF-N patterns).
/// </summary>
public partial class JiraRefExtractor(GitHubDatabase database, ILogger<JiraRefExtractor> logger)
{
    // Explicit Jira reference patterns
    [GeneratedRegex(@"\b(FHIR-\d+)\b", RegexOptions.Compiled)]
    private static partial Regex FhirPattern();

    [GeneratedRegex(@"\b(JF-\d+)\b", RegexOptions.Compiled)]
    private static partial Regex JfPattern();

    [GeneratedRegex(@"\bJ#(\d+)\b", RegexOptions.Compiled)]
    private static partial Regex JHashPattern();

    [GeneratedRegex(@"\b(GF-\d+)\b", RegexOptions.Compiled)]
    private static partial Regex GfPattern();

    // Bare #NNN reference (ambiguous — needs disambiguation)
    [GeneratedRegex(@"(?<![a-zA-Z\d/])#(\d+)\b", RegexOptions.Compiled)]
    private static partial Regex BareHashPattern();

    /// <summary>
    /// Extracts Jira references from all issues, comments, and commits in the database.
    /// </summary>
    public void ExtractAll(string repoFullName, HashSet<int>? validJiraNumbers = null, CancellationToken ct = default)
    {
        using var connection = database.OpenConnection();

        // Clear existing refs for this repo
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM github_jira_refs WHERE RepoFullName = @repo";
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.ExecuteNonQuery();
        }

        // Determine if repo has issues (for #NNN disambiguation)
        var repo = GitHubRepoRecord.SelectSingle(connection, FullName: repoFullName);
        var hasIssues = repo?.HasIssues ?? true;

        // Get all GitHub issue/PR numbers for this repo (for disambiguation)
        var githubNumbers = new HashSet<int>();
        if (hasIssues)
        {
            var issues = GitHubIssueRecord.SelectList(connection, RepoFullName: repoFullName);
            foreach (var issue in issues)
                githubNumbers.Add(issue.Number);
        }

        int refCount = 0;

        // Scan issues
        var allIssues = GitHubIssueRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (var issue in allIssues)
        {
            ct.ThrowIfCancellationRequested();
            var text = $"{issue.Title} {issue.Body}";
            var refs = ExtractReferences(text, repoFullName, hasIssues, githubNumbers, validJiraNumbers);

            foreach (var jiraRef in refs)
            {
                jiraRef.SourceType = "issue";
                jiraRef.SourceId = issue.UniqueKey;
                GitHubJiraRefRecord.Insert(connection, jiraRef, ignoreDuplicates: true);
                refCount++;
            }
        }

        // Scan comments
        var allComments = GitHubCommentRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (var comment in allComments)
        {
            ct.ThrowIfCancellationRequested();
            var refs = ExtractReferences(comment.Body, repoFullName, hasIssues, githubNumbers, validJiraNumbers);

            foreach (var jiraRef in refs)
            {
                jiraRef.SourceType = "comment";
                jiraRef.SourceId = $"{comment.RepoFullName}#{comment.IssueNumber}:{comment.Id}";
                GitHubJiraRefRecord.Insert(connection, jiraRef, ignoreDuplicates: true);
                refCount++;
            }
        }

        // Scan commits
        var allCommits = GitHubCommitRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (var commit in allCommits)
        {
            ct.ThrowIfCancellationRequested();
            var refs = ExtractReferences(commit.Message, repoFullName, hasIssues, githubNumbers, validJiraNumbers);

            foreach (var jiraRef in refs)
            {
                jiraRef.SourceType = "commit";
                jiraRef.SourceId = commit.Sha;
                GitHubJiraRefRecord.Insert(connection, jiraRef, ignoreDuplicates: true);
                refCount++;
            }
        }

        logger.LogInformation("Extracted {Count} Jira references from {Repo}", refCount, repoFullName);
    }

    /// <summary>Extracts Jira references from a text string.</summary>
    internal static List<GitHubJiraRefRecord> ExtractReferences(
        string text,
        string repoFullName,
        bool hasIssues,
        HashSet<int> githubNumbers,
        HashSet<int>? validJiraNumbers)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var results = new List<GitHubJiraRefRecord>();
        var seen = new HashSet<string>();

        // Explicit patterns
        AddMatches(results, seen, FhirPattern(), text, repoFullName, m => m.Groups[1].Value);
        AddMatches(results, seen, JfPattern(), text, repoFullName, m => m.Groups[1].Value);
        AddMatches(results, seen, GfPattern(), text, repoFullName, m => m.Groups[1].Value);

        // J#N → normalize to FHIR-N
        foreach (Match m in JHashPattern().Matches(text))
        {
            var num = m.Groups[1].Value;
            var jiraKey = $"FHIR-{num}";
            if (!seen.Add(jiraKey)) continue;

            if (validJiraNumbers is not null && !validJiraNumbers.Contains(int.Parse(num)))
                continue;

            results.Add(new GitHubJiraRefRecord
            {
                Id = GitHubJiraRefRecord.GetIndex(),
                SourceType = "",
                SourceId = "",
                RepoFullName = repoFullName,
                JiraKey = jiraKey,
                Context = ExtractContext(text, m.Index),
            });
        }

        // Bare #NNN disambiguation
        foreach (Match m in BareHashPattern().Matches(text))
        {
            if (!int.TryParse(m.Groups[1].Value, out var number)) continue;

            if (hasIssues && githubNumbers.Contains(number))
                continue; // It's a GitHub issue reference, skip

            var jiraKey = $"FHIR-{number}";
            if (!seen.Add(jiraKey)) continue;

            if (validJiraNumbers is not null && !validJiraNumbers.Contains(number))
                continue;

            results.Add(new GitHubJiraRefRecord
            {
                Id = GitHubJiraRefRecord.GetIndex(),
                SourceType = "",
                SourceId = "",
                RepoFullName = repoFullName,
                JiraKey = jiraKey,
                Context = ExtractContext(text, m.Index),
            });
        }

        return results;
    }

    private static void AddMatches(
        List<GitHubJiraRefRecord> results,
        HashSet<string> seen,
        Regex pattern,
        string text,
        string repoFullName,
        Func<Match, string> extractKey)
    {
        foreach (Match m in pattern.Matches(text))
        {
            var jiraKey = extractKey(m);
            if (!seen.Add(jiraKey)) continue;

            results.Add(new GitHubJiraRefRecord
            {
                Id = GitHubJiraRefRecord.GetIndex(),
                SourceType = "",
                SourceId = "",
                RepoFullName = repoFullName,
                JiraKey = jiraKey,
                Context = ExtractContext(text, m.Index),
            });
        }
    }

    private static string ExtractContext(string text, int matchIndex)
    {
        const int contextRadius = 80;
        var start = Math.Max(0, matchIndex - contextRadius);
        var end = Math.Min(text.Length, matchIndex + contextRadius);
        return text[start..end].Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
