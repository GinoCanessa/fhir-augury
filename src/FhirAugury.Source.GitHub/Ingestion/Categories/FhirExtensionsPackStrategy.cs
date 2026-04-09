using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion.Categories;

/// <summary>
/// Strategy for FHIR Extensions Pack repositories.
/// Walks input/definitions/ recursively and tags artifacts with their
/// target resource context from the parent directory name.
/// </summary>
public class FhirExtensionsPackStrategy(
    ILogger<FhirExtensionsPackStrategy> logger) : IRepoCategoryStrategy
{
    public RepoCategory Category => RepoCategory.FhirExtensionsPack;
    public string StrategyName => "fhir-extensions-pack";

    public bool Validate(string repoFullName, string clonePath)
    {
        string definitionsDir = Path.Combine(clonePath, "input", "definitions");
        if (!Directory.Exists(definitionsDir))
        {
            logger.LogWarning("Extensions Pack repo {Repo} missing input/definitions/", repoFullName);
            return false;
        }
        return true;
    }

    public List<GitHubFileTagRecord> DiscoverTags(string repoFullName, string clonePath, CancellationToken ct)
    {
        List<GitHubFileTagRecord> tags = [];
        string definitionsDir = Path.Combine(clonePath, "input", "definitions");
        if (!Directory.Exists(definitionsDir))
            return tags;

        foreach (string file in Directory.EnumerateFiles(definitionsDir, "*.xml", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            string relativePath = Path.GetRelativePath(clonePath, file).Replace('\\', '/');
            string fileName = Path.GetFileName(file);

            // Tag with fhir-resource-type from filename prefix
            string? resourceType = FhirResourceTypes.TryGetFromFilename(fileName);
            if (resourceType is not null)
            {
                tags.Add(new GitHubFileTagRecord
                {
                    Id = GitHubFileTagRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    FilePath = relativePath,
                    TagCategory = "fhir-resource-type",
                    TagName = resourceType,
                    TagModifier = null,
                });
            }

            // Tag with target-resource from parent directory name
            string parentDir = Path.GetFileName(Path.GetDirectoryName(file)!);
            if (!string.Equals(parentDir, "definitions", StringComparison.OrdinalIgnoreCase))
            {
                tags.Add(new GitHubFileTagRecord
                {
                    Id = GitHubFileTagRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    FilePath = relativePath,
                    TagCategory = "target-resource",
                    TagName = parentDir,
                    TagModifier = null,
                });
            }
        }

        logger.LogInformation(
            "{Strategy} discovery for {Repo}: {TagCount} tags",
            StrategyName, repoFullName, tags.Count);
        return tags;
    }

    public List<string>? GetPriorityPaths(string repoFullName, string clonePath)
    {
        return ["input/definitions/"];
    }

    public List<string> GetAdditionalIgnorePatterns()
    {
        return [];
    }

    public void BuildArtifactMappings(string repoFullName, string clonePath, SqliteConnection connection, CancellationToken ct)
    {
        // Clear existing mappings
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM github_spec_file_map WHERE RepoFullName = @repo";
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.ExecuteNonQuery();
        }

        int mapCount = 0;

        // Map canonical artifacts by URL
        List<GitHubCanonicalArtifactRecord> artifacts =
            GitHubCanonicalArtifactRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (GitHubCanonicalArtifactRecord artifact in artifacts)
        {
            ct.ThrowIfCancellationRequested();
            GitHubSpecFileMapRecord.Insert(connection, new GitHubSpecFileMapRecord
            {
                Id = GitHubSpecFileMapRecord.GetIndex(),
                RepoFullName = repoFullName,
                ArtifactKey = artifact.Url,
                FilePath = artifact.FilePath,
                MapType = "canonical",
            }, ignoreDuplicates: true);
            mapCount++;
        }

        // Map structure definitions by URL
        List<GitHubStructureDefinitionRecord> sds =
            GitHubStructureDefinitionRecord.SelectList(connection, RepoFullName: repoFullName);
        foreach (GitHubStructureDefinitionRecord sd in sds)
        {
            ct.ThrowIfCancellationRequested();
            GitHubSpecFileMapRecord.Insert(connection, new GitHubSpecFileMapRecord
            {
                Id = GitHubSpecFileMapRecord.GetIndex(),
                RepoFullName = repoFullName,
                ArtifactKey = sd.Url,
                FilePath = sd.FilePath,
                MapType = "canonical",
            }, ignoreDuplicates: true);
            mapCount++;
        }

        logger.LogInformation("Built {Count} canonical artifact-file mappings for {Repo}", mapCount, repoFullName);
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
