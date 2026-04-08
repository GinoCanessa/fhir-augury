using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion.Parsing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion.Categories;

/// <summary>
/// Strategy for FHIR Core repositories (e.g., HL7/fhir).
/// Parses source/fhir.ini and maps artifacts to files in the clone.
/// </summary>
public class FhirCoreStrategy(ILogger<FhirCoreStrategy> logger) : IRepoCategoryStrategy
{
    public RepoCategory Category => RepoCategory.FhirCore;
    public string StrategyName => "fhir-core";

    public bool Validate(string repoFullName, string clonePath)
    {
        string iniPath = Path.Combine(clonePath, "source", "fhir.ini");
        return File.Exists(iniPath);
    }

    public List<GitHubFileTagRecord> DiscoverTags(string repoFullName, string clonePath, CancellationToken ct)
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

    public List<string>? GetPriorityPaths(string repoFullName, string clonePath)
    {
        return ["source/"];
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

        // Map directories under source/ (core FHIR repo convention)
        string sourceDir = Path.Combine(clonePath, "source");
        if (Directory.Exists(sourceDir))
        {
            foreach (string dir in Directory.GetDirectories(sourceDir))
            {
                ct.ThrowIfCancellationRequested();
                string dirName = Path.GetFileName(dir);
                string relativePath = Path.GetRelativePath(clonePath, dir).Replace('\\', '/');

                GitHubSpecFileMapRecord.Insert(connection, new GitHubSpecFileMapRecord
                {
                    Id = GitHubSpecFileMapRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    ArtifactKey = dirName,
                    FilePath = relativePath,
                    MapType = "directory",
                }, ignoreDuplicates: true);
                mapCount++;
            }

            foreach (string file in Directory.GetFiles(sourceDir, "*.xml", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string fileName = Path.GetFileNameWithoutExtension(file);
                string relativePath = Path.GetRelativePath(clonePath, file).Replace('\\', '/');

                GitHubSpecFileMapRecord.Insert(connection, new GitHubSpecFileMapRecord
                {
                    Id = GitHubSpecFileMapRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    ArtifactKey = fileName,
                    FilePath = relativePath,
                    MapType = "file",
                }, ignoreDuplicates: true);
                mapCount++;
            }
        }

        logger.LogInformation("Built {Count} artifact-file mappings for {Repo}", mapCount, repoFullName);
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
