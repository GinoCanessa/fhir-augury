using FhirAugury.Source.GitHub.Database.Records;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Discovers semantic tags for files in a repository based on
/// repository-specific metadata patterns.
/// </summary>
public interface IRepoDiscoveryPattern
{
    /// <summary>Human-readable pattern name for logging.</summary>
    string PatternName { get; }

    /// <summary>
    /// Determines if this pattern applies to the given repository.
    /// </summary>
    bool AppliesTo(string repoFullName, string clonePath);

    /// <summary>
    /// Discovers tags for all relevant files in the repository clone.
    /// </summary>
    List<GitHubFileTagRecord> DiscoverTags(
        string repoFullName, string clonePath, CancellationToken ct);
}
