using FhirAugury.Parsing.Fhir;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Parses FHIR canonical artifact files and indexes them into the database.
/// Handles individual artifacts and bundles (e.g., SearchParameter bundles in FHIR Core).
/// </summary>
public class CanonicalArtifactIndexer(
    GitHubDatabase database,
    ILogger<CanonicalArtifactIndexer> logger)
{
    private static readonly HashSet<string> CanonicalResourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "CodeSystem", "ValueSet", "ConceptMap", "SearchParameter",
        "OperationDefinition", "NamingSystem", "CapabilityStatement",
    };

    /// <summary>
    /// Parses the given files, extracts canonical artifact metadata, and inserts records into the database.
    /// Returns the number of records indexed.
    /// </summary>
    public int IndexFiles(
        string repoFullName,
        string clonePath,
        IReadOnlyList<string> filePaths,
        CancellationToken ct)
    {
        using SqliteConnection connection = database.OpenConnection();

        // Clear existing canonical artifact records for this repo
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM github_canonical_artifacts WHERE RepoFullName = @repo";
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.ExecuteNonQuery();
        }

        List<GitHubCanonicalArtifactRecord> allRecords = [];
        int parseErrors = 0;

        foreach (string filePath in filePaths)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                List<GitHubCanonicalArtifactRecord> records = ParseFile(repoFullName, clonePath, filePath);
                allRecords.AddRange(records);
            }
            catch (Exception ex)
            {
                parseErrors++;
                logger.LogDebug(ex, "Failed to parse canonical artifact: {FilePath}", filePath);
            }
        }

        if (allRecords.Count > 0)
        {
            const int batchSize = 1000;
            for (int i = 0; i < allRecords.Count; i += batchSize)
            {
                List<GitHubCanonicalArtifactRecord> batch = allRecords.GetRange(i, Math.Min(batchSize, allRecords.Count - i));
                batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
            }
        }

        if (parseErrors > 0)
        {
            logger.LogDebug("Skipped {ErrorCount} unparseable files out of {Total} for {Repo}",
                parseErrors, filePaths.Count, repoFullName);
        }

        logger.LogInformation("Indexed {Count} canonical artifacts from {FileCount} files for {Repo}",
            allRecords.Count, filePaths.Count, repoFullName);

        return allRecords.Count;
    }

    private List<GitHubCanonicalArtifactRecord> ParseFile(
        string repoFullName,
        string clonePath,
        string absolutePath)
    {
        string relativePath = Path.GetRelativePath(clonePath, absolutePath).Replace('\\', '/');
        string extension = Path.GetExtension(absolutePath).ToLowerInvariant();
        string format = extension switch { ".xml" => "xml", ".json" => "json", _ => "unknown" };

        // Try single artifact first
        CanonicalArtifactInfo? artifact = FhirContentParser.TryParseCanonicalArtifact(absolutePath);
        if (artifact is not null && CanonicalResourceTypes.Contains(artifact.ResourceType))
        {
            return [MapToRecord(repoFullName, relativePath, format, artifact)];
        }

        // Try Bundle (for SearchParameter bundles in FHIR Core)
        List<CanonicalArtifactInfo> bundleEntries = FhirContentParser.TryParseBundle(absolutePath);
        if (bundleEntries.Count > 0)
        {
            return bundleEntries
                .Where(e => CanonicalResourceTypes.Contains(e.ResourceType))
                .Select(e => MapToRecord(repoFullName, relativePath, format, e))
                .ToList();
        }

        return [];
    }

    private static GitHubCanonicalArtifactRecord MapToRecord(
        string repoFullName,
        string relativePath,
        string format,
        CanonicalArtifactInfo info)
    {
        return new GitHubCanonicalArtifactRecord
        {
            Id = GitHubCanonicalArtifactRecord.GetIndex(),
            RepoFullName = repoFullName,
            FilePath = relativePath,
            ResourceType = info.ResourceType,
            Url = info.Url,
            Name = info.Name,
            Title = info.Title,
            Version = info.Version,
            Status = info.Status,
            Description = info.Description,
            Publisher = info.Publisher,
            WorkGroup = info.WorkGroup,
            FhirMaturity = info.FhirMaturity,
            StandardsStatus = info.StandardsStatus,
            TypeSpecificData = SerializeTypeSpecificData(info.TypeSpecificData),
            Format = format,
        };
    }

    private static string? SerializeTypeSpecificData(Dictionary<string, object?> data)
    {
        if (data.Count == 0) return null;
        return System.Text.Json.JsonSerializer.Serialize(data);
    }
}
