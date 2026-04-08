using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion.Categories;

/// <summary>
/// Strategy for FHIR Extensions Pack repositories.
/// Stub implementation for Phase 1.
/// </summary>
public class FhirExtensionsPackStrategy(
    ArtifactFileMapper artifactFileMapper,
    ILogger<FhirExtensionsPackStrategy> logger) : IRepoCategoryStrategy
{
    public RepoCategory Category => RepoCategory.FhirExtensionsPack;
    public string StrategyName => "fhir-extensions-pack";

    public bool Validate(string repoFullName, string clonePath)
    {
        return Directory.Exists(clonePath);
    }

    public List<GitHubFileTagRecord> DiscoverTags(string repoFullName, string clonePath, CancellationToken ct)
    {
        return [];
    }

    public List<string>? GetPriorityPaths(string repoFullName, string clonePath)
    {
        return null;
    }

    public List<string> GetAdditionalIgnorePatterns()
    {
        return [];
    }

    public void BuildArtifactMappings(string repoFullName, string clonePath, SqliteConnection connection, CancellationToken ct)
    {
        artifactFileMapper.BuildMappings(repoFullName, clonePath, ct);
    }
}
