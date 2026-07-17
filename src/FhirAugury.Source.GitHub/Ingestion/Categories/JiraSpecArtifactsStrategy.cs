using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion.Categories;

/// <summary>
/// Strategy for the JIRA-Spec-Artifacts repository category.
/// Indexes Jira specification metadata, artifact/page registries, workgroups, and families.
/// </summary>
public class JiraSpecArtifactsStrategy(
    JiraSpecXmlIndexer indexer,
    ILogger<JiraSpecArtifactsStrategy> logger) : IRepoCategoryStrategy
{
    public RepoCategory Category => RepoCategory.JiraSpecArtifacts;
    public string StrategyName => "jira-spec-artifacts";

    public bool Validate(string repoFullName, string clonePath)
    {
        string xmlDir = Path.Combine(clonePath, "xml");
        if (!Directory.Exists(xmlDir))
        {
            logger.LogDebug("JiraSpecArtifacts validation failed: xml/ directory not found in {Repo}", repoFullName);
            return false;
        }

        // Check for _families.xml in xml/ or root
        bool hasFamilies = File.Exists(Path.Combine(xmlDir, "_families.xml")) ||
                           File.Exists(Path.Combine(clonePath, "_families.xml"));
        if (!hasFamilies)
        {
            logger.LogDebug("JiraSpecArtifacts validation failed: _families.xml not found in {Repo}", repoFullName);
            return false;
        }

        // Check for at least one SPECS-*.xml
        bool hasSpecsFile = Directory.EnumerateFiles(xmlDir, "SPECS-*.xml").Any() ||
                            Directory.EnumerateFiles(clonePath, "SPECS-*.xml").Any();
        if (!hasSpecsFile)
        {
            logger.LogDebug("JiraSpecArtifacts validation failed: no SPECS-*.xml found in {Repo}", repoFullName);
            return false;
        }

        return true;
    }

    public List<GitHubFileTagRecord> DiscoverTags(string repoFullName, string clonePath, CancellationToken ct)
    {
        List<GitHubFileTagRecord> tags = [];

        // Tag files in both xml/ and root
        IEnumerable<string> allXmlFiles = Enumerable.Empty<string>();
        string xmlDir = Path.Combine(clonePath, "xml");
        if (Directory.Exists(xmlDir))
            allXmlFiles = allXmlFiles.Concat(Directory.EnumerateFiles(xmlDir, "*.xml"));

        allXmlFiles = allXmlFiles.Concat(
            Directory.EnumerateFiles(clonePath, "*.xml")
                .Where(f => !f.StartsWith(xmlDir, StringComparison.OrdinalIgnoreCase)));

        foreach (string file in allXmlFiles)
        {
            ct.ThrowIfCancellationRequested();

            string fileName = Path.GetFileName(file);
            string relativePath = Path.GetRelativePath(clonePath, file).Replace('\\', '/');

            // Determine tag type
            string tagName;
            if (fileName.Equals("_families.xml", StringComparison.OrdinalIgnoreCase))
            {
                tagName = "jira-family-list";
            }
            else if (fileName.Equals("_workgroups.xml", StringComparison.OrdinalIgnoreCase))
            {
                tagName = "jira-workgroup-list";
            }
            else if (fileName.StartsWith("SPECS-", StringComparison.OrdinalIgnoreCase))
            {
                tagName = "jira-spec-list";
            }
            else
            {
                // Individual spec files: must match [FAMILY]-*.xml pattern
                int hyphenIndex = fileName.IndexOf('-');
                if (hyphenIndex <= 0)
                    continue;

                tagName = "jira-spec-definition";

                // Also tag with family
                string prefix = fileName[..hyphenIndex];
                tags.Add(new GitHubFileTagRecord
                {
                    Id = GitHubFileTagRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    FilePath = relativePath,
                    TagCategory = "spec-family",
                    TagName = prefix.ToUpperInvariant(),
                });
            }

            tags.Add(new GitHubFileTagRecord
            {
                Id = GitHubFileTagRecord.GetIndex(),
                RepoFullName = repoFullName,
                FilePath = relativePath,
                TagCategory = "type",
                TagName = tagName,
            });
        }

        logger.LogInformation(
            "{Strategy} discovery for {Repo}: {TagCount} tags",
            StrategyName, repoFullName, tags.Count);
        return tags;
    }

    public List<string>? GetPriorityPaths(string repoFullName, string clonePath)
    {
        return ["xml/"];
    }

    public List<string> GetAdditionalIgnorePatterns()
    {
        return ["tools/", "schemas/", "images/", "json/", ".github/"];
    }

    public void BuildArtifactMappings(string repoFullName, string clonePath, SqliteConnection connection, CancellationToken ct)
    {
        indexer.IndexRepository(repoFullName, clonePath, connection, ct);
    }
}
