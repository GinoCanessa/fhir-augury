using FhirAugury.Parsing.Fsh;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Ingestion.Categories;

/// <summary>
/// Strategy for category-specific repository discovery, tagging, and indexing.
/// Each <see cref="RepoCategory"/> has a corresponding strategy implementation.
/// </summary>
public interface IRepoCategoryStrategy
{
    /// <summary>The category this strategy handles.</summary>
    RepoCategory Category { get; }

    /// <summary>Human-readable strategy name for logging.</summary>
    string StrategyName { get; }

    /// <summary>
    /// Validates that the clone directory has the expected structure for this category.
    /// Returns false if the repo doesn't look right (e.g., missing expected files).
    /// </summary>
    bool Validate(string repoFullName, string clonePath);

    /// <summary>
    /// Discovers semantic tags for files in the repository clone.
    /// </summary>
    List<GitHubFileTagRecord> DiscoverTags(string repoFullName, string clonePath, CancellationToken ct);

    /// <summary>
    /// Returns the only paths that should be included in file content indexing.
    /// Files outside these paths are completely excluded from the <c>github_file_contents</c> table.
    /// Returns null to index all files (not recommended for structured repos).
    /// Paths are relative to the clone root and should use forward slashes with a trailing slash
    /// (e.g., <c>"source/"</c>, <c>"input/definitions/"</c>).
    /// </summary>
    List<string>? GetPriorityPaths(string repoFullName, string clonePath);

    /// <summary>
    /// Returns additional gitignore-style patterns to exclude from file content indexing.
    /// These are merged with global <see cref="FileContentIndexingOptions.IgnorePatterns"/>
    /// and any <c>.augury-index-ignore</c> file in the repository root.
    /// Applied after <see cref="GetPriorityPaths"/> filtering (only files within
    /// priority paths are candidates for ignore pattern matching).
    /// </summary>
    List<string> GetAdditionalIgnorePatterns();

    /// <summary>
    /// Builds artifact-to-file mappings specific to this category.
    /// </summary>
    void BuildArtifactMappings(string repoFullName, string clonePath, SqliteConnection connection, CancellationToken ct);

    /// <summary>
    /// Returns absolute file paths to scan for canonical artifacts (CodeSystem, ValueSet, etc.).
    /// Default implementation returns empty list for categories that don't have canonical artifacts.
    /// </summary>
    IReadOnlyList<string> DiscoverCanonicalArtifactFiles(string repoFullName, string clonePath, CancellationToken ct)
        => [];

    /// <summary>
    /// Returns absolute file paths of StructureDefinition files to parse and index.
    /// Default implementation returns empty list for categories that don't have SDs.
    /// </summary>
    List<string> DiscoverStructureDefinitionFiles(string repoFullName, string clonePath, CancellationToken ct)
        => [];

    /// <summary>
    /// Discovers FSH files and sushi-config.yaml for this repository.
    /// Default implementation returns empty list for categories without FSH content.
    /// </summary>
    (IReadOnlyList<string> FshFiles, SushiConfig? Config) DiscoverFshContent(
        string repoFullName, string clonePath, CancellationToken ct)
        => ([], null);
}
