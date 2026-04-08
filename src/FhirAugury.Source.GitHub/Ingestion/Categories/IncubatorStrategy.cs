using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Indexing;
using FhirAugury.Source.GitHub.Ingestion.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion.Categories;

/// <summary>
/// Strategy for FHIR Incubator repositories.
/// Stub implementation for Phase 1; validates via IG project markers.
/// </summary>
public class IncubatorStrategy(
    ArtifactFileMapper artifactFileMapper,
    ILogger<IncubatorStrategy> logger) : IRepoCategoryStrategy
{
    public RepoCategory Category => RepoCategory.Incubator;
    public string StrategyName => "incubator";

    public bool Validate(string repoFullName, string clonePath)
    {
        IgProjectDetector.DetectionResult result = IgProjectDetector.Detect(clonePath);
        return result.IsIgProject;
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
