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

    public IReadOnlyList<string> DiscoverCanonicalArtifactFiles(
        string repoFullName, string clonePath, CancellationToken ct)
    {
        string defsDir = Path.Combine(clonePath, "input", "definitions");
        if (!Directory.Exists(defsDir))
            return [];

        List<string> files = [];
        string[] prefixes = ["CodeSystem-", "ValueSet-", "SearchParameter-", "ConceptMap-"];

        foreach (string xmlFile in Directory.EnumerateFiles(defsDir, "*.xml", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileName(xmlFile);
            if (prefixes.Any(p => fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                files.Add(xmlFile);
            }
        }

        logger.LogInformation("Discovered {Count} canonical artifact files in Extensions Pack", files.Count);
        return files;
    }

    public List<string> DiscoverStructureDefinitionFiles(string repoFullName, string clonePath, CancellationToken ct)
    {
        List<string> sdFiles = [];
        string definitionsDir = Path.Combine(clonePath, "input", "definitions");
        if (!Directory.Exists(definitionsDir))
            return sdFiles;

        foreach (string file in Directory.GetFiles(definitionsDir, "StructureDefinition-*.xml", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            sdFiles.Add(file);
        }

        logger.LogDebug("Discovered {Count} StructureDefinition files for {Repo}", sdFiles.Count, repoFullName);
        return sdFiles;
    }
}
