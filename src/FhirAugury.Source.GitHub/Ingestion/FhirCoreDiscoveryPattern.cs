using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion.Parsing;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Discovery pattern for FHIR Core repositories (e.g., HL7/fhir).
/// Parses source/fhir.ini and maps artifacts to files in the clone.
/// </summary>
public class FhirCoreDiscoveryPattern(ILogger<FhirCoreDiscoveryPattern> logger) : IRepoDiscoveryPattern
{
    public string PatternName => "fhir-core";

    public bool AppliesTo(string repoFullName, string clonePath)
    {
        string iniPath = Path.Combine(clonePath, "source", "fhir.ini");
        return File.Exists(iniPath);
    }

    public List<GitHubFileTagRecord> DiscoverTags(
        string repoFullName, string clonePath, CancellationToken ct)
    {
        string iniPath = Path.Combine(clonePath, "source", "fhir.ini");
        if (!File.Exists(iniPath))
            return [];

        FhirIniParser parser = new();
        List<ArtifactEntry> artifacts = parser.Parse(iniPath);

        logger.LogInformation("Parsed {Count} artifacts from fhir.ini", artifacts.Count);

        List<GitHubFileTagRecord> tags = [];

        foreach (ArtifactEntry artifact in artifacts)
        {
            ct.ThrowIfCancellationRequested();

            string artifactDir = Path.Combine(clonePath, "source", artifact.DirectoryKey);
            string? modifier = artifact.Modifiers.Count > 0 ? artifact.Modifiers.First() : null;

            if (Directory.Exists(artifactDir))
            {
                ScanDirectory(artifactDir, clonePath, repoFullName, artifact, modifier, tags);
            }
            else if (artifact.Category == "type")
            {
                // Fallback: scan source/datatypes/ for files matching the type name
                string datatypesDir = Path.Combine(clonePath, "source", "datatypes");
                if (Directory.Exists(datatypesDir))
                {
                    ScanDatatypesFallback(datatypesDir, clonePath, repoFullName, artifact, modifier, tags);
                }
            }
        }

        logger.LogInformation("Discovered {Count} file tags for {Repo}", tags.Count, repoFullName);
        return tags;
    }

    private static void ScanDirectory(
        string directory,
        string clonePath,
        string repoFullName,
        ArtifactEntry artifact,
        string? modifier,
        List<GitHubFileTagRecord> tags)
    {
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(clonePath, file).Replace('\\', '/');

            tags.Add(new GitHubFileTagRecord
            {
                Id = GitHubFileTagRecord.GetIndex(),
                RepoFullName = repoFullName,
                FilePath = relativePath,
                TagCategory = artifact.Category,
                TagName = artifact.Name,
                TagModifier = modifier,
            });

            // Check for FHIR resource type in XML filenames
            if (Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                string? resourceType = FhirResourceTypes.TryGetFromFilename(Path.GetFileName(file));
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
            }
        }
    }

    private static void ScanDatatypesFallback(
        string datatypesDir,
        string clonePath,
        string repoFullName,
        ArtifactEntry artifact,
        string? modifier,
        List<GitHubFileTagRecord> tags)
    {
        string lowerName = artifact.Name.ToLowerInvariant();

        foreach (string file in Directory.EnumerateFiles(datatypesDir, "*", SearchOption.AllDirectories))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Contains(lowerName, StringComparison.OrdinalIgnoreCase))
            {
                string relativePath = Path.GetRelativePath(clonePath, file).Replace('\\', '/');

                tags.Add(new GitHubFileTagRecord
                {
                    Id = GitHubFileTagRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    FilePath = relativePath,
                    TagCategory = artifact.Category,
                    TagName = artifact.Name,
                    TagModifier = modifier,
                });

                // Check for FHIR resource type in XML filenames
                if (Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                {
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
                }
            }
        }
    }
}
