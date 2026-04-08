using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Indexing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion.Categories;

/// <summary>
/// Strategy for UTG (Unified Terminology Governance) repositories.
/// Stub implementation for Phase 1; validates structure only.
/// </summary>
public class UtgStrategy(
    ArtifactFileMapper artifactFileMapper,
    ILogger<UtgStrategy> logger) : IRepoCategoryStrategy
{
    public RepoCategory Category => RepoCategory.Utg;
    public string StrategyName => "utg";

    public bool Validate(string repoFullName, string clonePath)
    {
        return Directory.Exists(Path.Combine(clonePath, "input", "sourceOfTruth"));
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

    public IReadOnlyList<string> DiscoverCanonicalArtifactFiles(
        string repoFullName, string clonePath, CancellationToken ct)
    {
        string sourceDir = Path.Combine(clonePath, "input", "sourceOfTruth");
        if (!Directory.Exists(sourceDir))
            return [];

        List<string> files = Directory.EnumerateFiles(sourceDir, "*.xml", SearchOption.AllDirectories)
            .ToList();

        logger.LogInformation("Discovered {Count} canonical artifact files in UTG", files.Count);
        return files;
    }

    public List<string> DiscoverStructureDefinitionFiles(string repoFullName, string clonePath, CancellationToken ct)
    {
        List<string> sdFiles = [];
        string resourcesDir = Path.Combine(clonePath, "input", "resources");
        if (!Directory.Exists(resourcesDir))
            return sdFiles;

        foreach (string file in Directory.GetFiles(resourcesDir, "StructureDefinition-*.xml", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            sdFiles.Add(file);
        }

        logger.LogDebug("Discovered {Count} StructureDefinition files for {Repo}", sdFiles.Count, repoFullName);
        return sdFiles;
    }
}
