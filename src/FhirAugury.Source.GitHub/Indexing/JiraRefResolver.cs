using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Indexing;

/// <summary>
/// Disambiguates bare #NNN references based on the repository's has_issues flag.
/// When has_issues is off, all #NNN treated as Jira refs.
/// When has_issues is on, check GitHub issue/PR numbers first.
/// </summary>
public class JiraRefResolver(GitHubDatabase database, ILogger<JiraRefResolver> logger)
{
    /// <summary>
    /// Determines whether a bare #NNN reference in the given repo is a Jira reference.
    /// Returns the Jira key (e.g., "FHIR-123") if it's a Jira reference, or null if it's a GitHub issue.
    /// </summary>
    public string? ResolveHashReference(string repoFullName, int number, HashSet<int>? validJiraNumbers = null)
    {
        using var connection = database.OpenConnection();

        var repo = GitHubRepoRecord.SelectSingle(connection, FullName: repoFullName);
        if (repo is null)
        {
            logger.LogDebug("Repo {Repo} not found, treating #{Number} as potential Jira ref", repoFullName, number);
            return ValidateJiraNumber(number, validJiraNumbers);
        }

        if (!repo.HasIssues)
        {
            // No GitHub issues — treat all #NNN as Jira references
            return ValidateJiraNumber(number, validJiraNumbers);
        }

        // Check if it matches a GitHub issue/PR number
        var issue = GitHubIssueRecord.SelectSingle(connection, UniqueKey: $"{repoFullName}#{number}");
        if (issue is not null)
        {
            // It's a GitHub issue/PR reference
            return null;
        }

        // No matching GitHub issue — check if it's a valid Jira number
        return ValidateJiraNumber(number, validJiraNumbers);
    }

    /// <summary>
    /// Resolves all bare #NNN references in a repo, returning only those that are Jira references.
    /// </summary>
    public List<(int Number, string JiraKey)> ResolveAllHashReferences(
        string repoFullName, IEnumerable<int> numbers, HashSet<int>? validJiraNumbers = null)
    {
        var results = new List<(int, string)>();

        foreach (var number in numbers)
        {
            var jiraKey = ResolveHashReference(repoFullName, number, validJiraNumbers);
            if (jiraKey is not null)
                results.Add((number, jiraKey));
        }

        return results;
    }

    private static string? ValidateJiraNumber(int number, HashSet<int>? validJiraNumbers)
    {
        if (validJiraNumbers is not null && !validJiraNumbers.Contains(number))
            return null;

        return $"FHIR-{number}";
    }
}
