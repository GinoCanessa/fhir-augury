using FhirAugury.Parsing.Fsh;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database.Records;
using FhirAugury.Source.GitHub.Ingestion.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion.Categories;

/// <summary>
/// Strategy for FHIR Incubator repositories.
/// Parses sushi-config.yaml, walks resource directories, scans FSH files,
/// and handles the special-url-base convention for core-namespace incubated resources.
/// </summary>
public class IncubatorStrategy(
    ILogger<IncubatorStrategy> logger) : IRepoCategoryStrategy
{
    public RepoCategory Category => RepoCategory.Incubator;
    public string StrategyName => "incubator";

    public bool Validate(string repoFullName, string clonePath)
    {
        IgProjectDetector.DetectionResult result = IgProjectDetector.Detect(clonePath);
        if (!result.IsIgProject)
        {
            logger.LogWarning("Incubator repo {Repo} has no IG project markers (sushi-config.yaml or ig.ini)",
                repoFullName);
            return false;
        }
        return true;
    }

    public List<GitHubFileTagRecord> DiscoverTags(string repoFullName, string clonePath, CancellationToken ct)
    {
        List<GitHubFileTagRecord> tags = [];

        // Parse sushi-config.yaml for metadata
        string configPath = Path.Combine(clonePath, "sushi-config.yaml");
        SushiConfig? config = SushiConfigParser.TryParse(configPath);
        bool isCoreNamespace = config?.SpecialUrlBase is "http://hl7.org/fhir";

        // Determine resource directories to scan
        List<string> resourceDirs = [];
        if (config?.PathResource is { Count: > 0 })
        {
            foreach (string pathResource in config.PathResource)
            {
                string dir = Path.Combine(clonePath, pathResource.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(dir))
                    resourceDirs.Add(dir);
            }
        }

        // Fallback to input/resources/
        if (resourceDirs.Count == 0)
        {
            string defaultDir = Path.Combine(clonePath, "input", "resources");
            if (Directory.Exists(defaultDir))
                resourceDirs.Add(defaultDir);
        }

        // Scan XML files in resource directories
        foreach (string resourceDir in resourceDirs)
        {
            foreach (string file in Directory.EnumerateFiles(resourceDir, "*.xml", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                string relativePath = Path.GetRelativePath(clonePath, file).Replace('\\', '/');
                string fileName = Path.GetFileName(file);

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

                    tags.Add(new GitHubFileTagRecord
                    {
                        Id = GitHubFileTagRecord.GetIndex(),
                        RepoFullName = repoFullName,
                        FilePath = relativePath,
                        TagCategory = "incubator",
                        TagName = Path.GetFileNameWithoutExtension(fileName),
                        TagModifier = "incubated",
                    });

                    if (isCoreNamespace)
                    {
                        tags.Add(new GitHubFileTagRecord
                        {
                            Id = GitHubFileTagRecord.GetIndex(),
                            RepoFullName = repoFullName,
                            FilePath = relativePath,
                            TagCategory = "core-namespace",
                            TagName = "incubated",
                            TagModifier = null,
                        });
                    }
                }
            }
        }

        // Scan FSH files in input/fsh/
        string fshDir = Path.Combine(clonePath, "input", "fsh");
        if (Directory.Exists(fshDir))
        {
            foreach (string file in Directory.EnumerateFiles(fshDir, "*.fsh", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                string relativePath = Path.GetRelativePath(clonePath, file).Replace('\\', '/');

                tags.Add(new GitHubFileTagRecord
                {
                    Id = GitHubFileTagRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    FilePath = relativePath,
                    TagCategory = "incubator",
                    TagName = Path.GetFileNameWithoutExtension(file),
                    TagModifier = "incubated",
                });

                if (isCoreNamespace)
                {
                    tags.Add(new GitHubFileTagRecord
                    {
                        Id = GitHubFileTagRecord.GetIndex(),
                        RepoFullName = repoFullName,
                        FilePath = relativePath,
                        TagCategory = "core-namespace",
                        TagName = "incubated",
                        TagModifier = null,
                    });
                }
            }
        }

        logger.LogInformation(
            "{Strategy} discovery for {Repo}: {TagCount} tags",
            StrategyName, repoFullName, tags.Count);
        return tags;
    }

    public List<string>? GetPriorityPaths(string repoFullName, string clonePath)
    {
        return ["input/resources/", "input/fsh/"];
    }

    public List<string> GetAdditionalIgnorePatterns()
    {
        return
        [
            "input/fsh/fsh-index.txt",
            "fixme/",
        ];
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