using System.Xml;
using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion.Categories;

/// <summary>
/// Strategy for UTG (Unified Terminology Governance) repositories.
/// Walks input/sourceOfTruth/ recursively, detects resource types from XML root elements,
/// tags with vocabulary family, and handles retired artifacts.
/// </summary>
public class UtgStrategy(
    ILogger<UtgStrategy> logger) : IRepoCategoryStrategy
{
    public RepoCategory Category => RepoCategory.Utg;
    public string StrategyName => "utg";

    private static readonly HashSet<string> KnownVocabularyFamilies =
        new(StringComparer.OrdinalIgnoreCase) { "fhir", "v2", "v3", "unified", "external", "retired" };

    public bool Validate(string repoFullName, string clonePath)
    {
        string sourceOfTruth = Path.Combine(clonePath, "input", "sourceOfTruth");
        if (!Directory.Exists(sourceOfTruth))
            return false;

        string[] expectedFamilies = ["fhir", "v2", "v3", "unified"];
        foreach (string family in expectedFamilies)
        {
            if (!Directory.Exists(Path.Combine(sourceOfTruth, family)))
                logger.LogWarning("UTG repo {Repo} missing expected vocabulary family directory: {Family}",
                    repoFullName, family);
        }

        return true;
    }

    public List<GitHubFileTagRecord> DiscoverTags(string repoFullName, string clonePath, CancellationToken ct)
    {
        List<GitHubFileTagRecord> tags = [];
        string sourceOfTruth = Path.Combine(clonePath, "input", "sourceOfTruth");
        if (!Directory.Exists(sourceOfTruth))
            return tags;

        foreach (string file in Directory.EnumerateFiles(sourceOfTruth, "*.xml", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            string relativePath = Path.GetRelativePath(clonePath, file).Replace('\\', '/');
            string relativeToSot = Path.GetRelativePath(sourceOfTruth, file).Replace('\\', '/');

            // Determine vocabulary family from first path segment under sourceOfTruth/
            string? family = DetectVocabularyFamily(relativeToSot);
            bool isRetired = string.Equals(family, "retired", StringComparison.OrdinalIgnoreCase);

            if (family is not null)
            {
                tags.Add(new GitHubFileTagRecord
                {
                    Id = GitHubFileTagRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    FilePath = relativePath,
                    TagCategory = "vocabulary-family",
                    TagName = family.ToLowerInvariant(),
                    TagModifier = isRetired ? "retired" : null,
                });
            }

            // Detect FHIR resource type from XML root element
            string? rootElement = DetectXmlRootElement(file);
            if (rootElement is not null)
            {
                tags.Add(new GitHubFileTagRecord
                {
                    Id = GitHubFileTagRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    FilePath = relativePath,
                    TagCategory = "fhir-resource-type",
                    TagName = rootElement,
                    TagModifier = isRetired ? "retired" : null,
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
        return ["input/sourceOfTruth/", "input/resources/"];
    }

    public List<string> GetAdditionalIgnorePatterns()
    {
        return
        [
            "input/sourceOfTruth/history/",
            "input/sourceOfTruth/control-manifests/",
            "input/sourceOfTruth/release-tracking/",
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

    // ── Helpers ──────────────────────────────────────────────

    private static string? DetectVocabularyFamily(string relativeToSourceOfTruth)
    {
        int sepIndex = relativeToSourceOfTruth.IndexOf('/');
        if (sepIndex <= 0)
            return null;

        string firstSegment = relativeToSourceOfTruth[..sepIndex];
        return KnownVocabularyFamilies.Contains(firstSegment) ? firstSegment : null;
    }

    /// <summary>
    /// Opens an XML file and reads the root element local name.
    /// Returns null if the file cannot be parsed.
    /// </summary>
    internal static string? DetectXmlRootElement(string filePath)
    {
        try
        {
            using FileStream fs = File.OpenRead(filePath);
            using XmlReader reader = XmlReader.Create(fs, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                    return reader.LocalName;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }
}
