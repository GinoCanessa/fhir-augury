using System.Text.Json;
using FhirAugury.Parsing.Fhir;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Parses StructureDefinition files discovered by category strategies,
/// classifies them, and inserts records into the database.
/// </summary>
public class StructureDefinitionIndexer(
    ILogger<StructureDefinitionIndexer> logger)
{
    /// <summary>
    /// Indexes all StructureDefinition files for a given repository.
    /// Clears existing SD records for the repo first, then parses and inserts new ones.
    /// </summary>
    public void IndexStructureDefinitions(
        string repoFullName,
        List<string> sdFilePaths,
        string clonePath,
        SqliteConnection connection,
        CancellationToken ct)
    {
        // Clear existing element records first (FK dependency)
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM github_sd_elements WHERE RepoFullName = @repo";
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.ExecuteNonQuery();
        }

        // Clear existing SD records for this repo
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM github_structure_definitions WHERE RepoFullName = @repo";
            cmd.Parameters.AddWithValue("@repo", repoFullName);
            cmd.ExecuteNonQuery();
        }

        List<GitHubStructureDefinitionRecord> sdRecords = [];
        List<GitHubFileTagRecord> tagRecords = [];
        List<GitHubSpecFileMapRecord> mapRecords = [];
        List<GitHubSdElementRecord> elementRecords = [];
        int parseFailures = 0;

        foreach (string filePath in sdFilePaths)
        {
            ct.ThrowIfCancellationRequested();

            StructureDefinitionInfo? sdInfo = FhirContentParser.TryParseStructureDefinition(filePath);
            if (sdInfo is null)
            {
                parseFailures++;
                logger.LogWarning("Failed to parse StructureDefinition from {FilePath}", filePath);
                continue;
            }

            string relativePath = Path.GetRelativePath(clonePath, filePath).Replace('\\', '/');
            string artifactClass = ArtifactClassifier.Classify(sdInfo.Kind, sdInfo.Derivation, sdInfo.FhirType);

            // Build SD record
            GitHubStructureDefinitionRecord sdRecord = new()
            {
                Id = GitHubStructureDefinitionRecord.GetIndex(),
                RepoFullName = repoFullName,
                FilePath = relativePath,
                Url = sdInfo.Url,
                Name = sdInfo.Name,
                Title = sdInfo.Title,
                Status = sdInfo.Status,
                ArtifactClass = artifactClass,
                Kind = sdInfo.Kind,
                IsAbstract = sdInfo.IsAbstract.HasValue ? (sdInfo.IsAbstract.Value ? 1 : 0) : null,
                FhirType = sdInfo.FhirType,
                BaseDefinition = sdInfo.BaseDefinition,
                Derivation = sdInfo.Derivation,
                FhirVersion = sdInfo.FhirVersion,
                Description = sdInfo.Description,
                Publisher = sdInfo.Publisher,
                WorkGroup = sdInfo.WorkGroup,
                FhirMaturity = sdInfo.FhirMaturity,
                StandardsStatus = sdInfo.StandardsStatus,
                Category = sdInfo.Category,
                Contexts = sdInfo.Contexts is { Count: > 0 }
                    ? JsonSerializer.Serialize(sdInfo.Contexts)
                    : null,
            };
            sdRecords.Add(sdRecord);

            // Build element records from differential
            foreach (ElementInfo element in sdInfo.DifferentialElements)
            {
                ct.ThrowIfCancellationRequested();

                string types = string.Join(";", element.Types.Select(t => t.Code));
                string typeProfiles = string.Join(";", element.Types.SelectMany(t => t.Profiles ?? []));
                string targetProfiles = string.Join(";", element.Types.SelectMany(t => t.TargetProfiles ?? []));

                elementRecords.Add(new GitHubSdElementRecord
                {
                    Id = GitHubSdElementRecord.GetIndex(),
                    RepoFullName = repoFullName,
                    StructureDefinitionId = sdRecord.Id,
                    ElementId = element.ElementId,
                    Path = element.Path,
                    Name = element.Name,
                    Short = element.Short,
                    Definition = element.Definition,
                    Comment = element.Comment,
                    MinCardinality = element.MinCardinality,
                    MaxCardinality = element.MaxCardinality,
                    Types = string.IsNullOrEmpty(types) ? null : types,
                    TypeProfiles = string.IsNullOrEmpty(typeProfiles) ? null : typeProfiles,
                    TargetProfiles = string.IsNullOrEmpty(targetProfiles) ? null : targetProfiles,
                    BindingStrength = element.BindingStrength,
                    BindingValueSet = element.BindingValueSet,
                    SliceName = element.SliceName,
                    IsModifier = element.IsModifier switch { true => 1, false => 0, null => null },
                    IsSummary = element.IsSummary switch { true => 1, false => 0, null => null },
                    FixedValue = element.FixedValue,
                    PatternValue = element.PatternValue,
                    FieldOrder = element.FieldOrder,
                });
            }

            // Build file tag for artifact class
            tagRecords.Add(new GitHubFileTagRecord
            {
                Id = GitHubFileTagRecord.GetIndex(),
                RepoFullName = repoFullName,
                FilePath = relativePath,
                TagCategory = "artifact-class",
                TagName = artifactClass,
            });

            // Build spec file map entry for canonical URL
            mapRecords.Add(new GitHubSpecFileMapRecord
            {
                Id = GitHubSpecFileMapRecord.GetIndex(),
                RepoFullName = repoFullName,
                ArtifactKey = sdInfo.Url,
                FilePath = relativePath,
                MapType = "structuredefinition",
            });
        }

        // Batch insert
        if (sdRecords.Count > 0)
        {
            const int batchSize = 1000;

            for (int i = 0; i < sdRecords.Count; i += batchSize)
            {
                List<GitHubStructureDefinitionRecord> batch = sdRecords.GetRange(i, Math.Min(batchSize, sdRecords.Count - i));
                batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
            }

            for (int i = 0; i < tagRecords.Count; i += batchSize)
            {
                List<GitHubFileTagRecord> batch = tagRecords.GetRange(i, Math.Min(batchSize, tagRecords.Count - i));
                batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
            }

            for (int i = 0; i < mapRecords.Count; i += batchSize)
            {
                List<GitHubSpecFileMapRecord> batch = mapRecords.GetRange(i, Math.Min(batchSize, mapRecords.Count - i));
                batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
            }

            for (int i = 0; i < elementRecords.Count; i += batchSize)
            {
                List<GitHubSdElementRecord> batch = elementRecords.GetRange(i, Math.Min(batchSize, elementRecords.Count - i));
                batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
            }
        }

        logger.LogInformation(
            "Indexed {SdCount} StructureDefinitions with {ElementCount} elements for {Repo} ({Failures} parse failures)",
            sdRecords.Count, elementRecords.Count, repoFullName, parseFailures);
    }
}
