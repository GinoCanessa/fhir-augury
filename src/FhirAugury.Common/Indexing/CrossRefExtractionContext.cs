namespace FhirAugury.Common.Indexing;

/// <summary>
/// Disambiguation hints passed to extractors that need them.
/// Sources populate this with knowledge specific to their data.
/// </summary>
public record CrossRefExtractionContext(
    HashSet<int>? ValidJiraNumbers = null,
    HashSet<int>? KnownGitHubIssueIds = null
);
