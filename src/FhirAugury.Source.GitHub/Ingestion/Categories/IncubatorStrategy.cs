using FhirAugury.Parsing.Fsh;
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

    public IReadOnlyList<string> DiscoverCanonicalArtifactFiles(
        string repoFullName, string clonePath, CancellationToken ct)
    {
        string resourcesDir = Path.Combine(clonePath, "input", "resources");
        if (!Directory.Exists(resourcesDir))
            return [];

        List<string> files = Directory.EnumerateFiles(resourcesDir, "*.xml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(resourcesDir, "*.json", SearchOption.AllDirectories))
            .ToList();

        logger.LogInformation("Discovered {Count} canonical artifact files in Incubator {Repo}", files.Count, repoFullName);
        return files;
    }

    public List<string> DiscoverStructureDefinitionFiles(string repoFullName, string clonePath, CancellationToken ct)
    {
        List<string> sdFiles = [];
        string resourcesDir = Path.Combine(clonePath, "input", "resources");
        if (!Directory.Exists(resourcesDir))
        {
            string inputDir = Path.Combine(clonePath, "input");
            if (!Directory.Exists(inputDir))
                return sdFiles;
            resourcesDir = inputDir;
        }

        foreach (string file in Directory.GetFiles(resourcesDir, "StructureDefinition-*.xml", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            sdFiles.Add(file);
        }

        logger.LogDebug("Discovered {Count} StructureDefinition files for {Repo}", sdFiles.Count, repoFullName);
        return sdFiles;
    }

    public (IReadOnlyList<string> FshFiles, SushiConfig? Config) DiscoverFshContent(
        string repoFullName, string clonePath, CancellationToken ct)
    {
        string configPath = Path.Combine(clonePath, "sushi-config.yaml");
        SushiConfig? config = SushiConfigParser.TryParse(configPath);

        string fshDir = Path.Combine(clonePath, "input", "fsh");
        if (!Directory.Exists(fshDir))
            return ([], config);

        List<string> fshFiles = Directory.EnumerateFiles(fshDir, "*.fsh", SearchOption.AllDirectories)
            .ToList();

        logger.LogInformation("Discovered {Count} FSH files in Incubator {Repo}", fshFiles.Count, repoFullName);
        return (fshFiles, config);
    }
}