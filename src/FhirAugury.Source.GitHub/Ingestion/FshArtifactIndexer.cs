using FhirAugury.Parsing.Fsh;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Parses FSH files and indexes extracted definitions into the database.
/// Profile/Extension/Resource/Logical → github_structure_definitions
/// CodeSystem/ValueSet/DefinitionalInstance → github_canonical_artifacts (Format="fsh")
/// </summary>
public class FshArtifactIndexer(
    GitHubDatabase database,
    ILogger<FshArtifactIndexer> logger)
{
    /// <summary>
    /// Parses all given FSH files, constructs canonical URLs from the sushi config,
    /// and inserts records into the appropriate database tables.
    /// Returns the total number of definitions indexed.
    /// </summary>
    public int IndexFshFiles(
        string repoFullName,
        string clonePath,
        IReadOnlyList<string> fshFilePaths,
        SushiConfig? sushiConfig,
        CancellationToken ct)
    {
        if (fshFilePaths.Count == 0)
            return 0;

        if (sushiConfig?.Canonical is null)
        {
            logger.LogWarning(
                "No canonical URL available for FSH definitions in {Repo} — missing or incomplete sushi-config.yaml",
                repoFullName);
        }

        List<GitHubCanonicalArtifactRecord> artifactRecords = [];
        List<GitHubStructureDefinitionRecord> sdRecords = [];
        int totalIndexed = 0;

        foreach (string fshFile in fshFilePaths)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                List<FshDefinitionInfo> definitions = FshContentParser.ParseFile(fshFile);
                string relativePath = Path.GetRelativePath(clonePath, fshFile).Replace('\\', '/');

                foreach (FshDefinitionInfo def in definitions)
                {
                    string? canonicalUrl = sushiConfig is not null
                        ? FshContentParser.ConstructCanonicalUrl(def, sushiConfig)
                        : def.ExplicitUrl;

                    switch (def.Kind)
                    {
                        case FshDefinitionKind.Profile:
                        case FshDefinitionKind.Extension:
                        case FshDefinitionKind.Resource:
                        case FshDefinitionKind.Logical:
                            sdRecords.Add(BuildStructureDefinitionRecord(repoFullName, relativePath, def, canonicalUrl));
                            totalIndexed++;
                            break;

                        case FshDefinitionKind.CodeSystem:
                        case FshDefinitionKind.ValueSet:
                        case FshDefinitionKind.DefinitionalInstance:
                            artifactRecords.Add(BuildCanonicalArtifactRecord(repoFullName, relativePath, def, canonicalUrl));
                            totalIndexed++;
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse FSH file {File}", fshFile);
            }
        }

        // Batch insert all records
        using SqliteConnection connection = database.OpenConnection();

        if (artifactRecords.Count > 0)
        {
            const int batchSize = 1000;
            for (int i = 0; i < artifactRecords.Count; i += batchSize)
            {
                List<GitHubCanonicalArtifactRecord> batch = artifactRecords.GetRange(
                    i, Math.Min(batchSize, artifactRecords.Count - i));
                batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
            }
        }

        if (sdRecords.Count > 0)
        {
            const int batchSize = 1000;
            for (int i = 0; i < sdRecords.Count; i += batchSize)
            {
                List<GitHubStructureDefinitionRecord> batch = sdRecords.GetRange(
                    i, Math.Min(batchSize, sdRecords.Count - i));
                batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
            }
        }

        logger.LogInformation(
            "Indexed {Total} FSH definitions ({SdCount} SDs, {ArtifactCount} artifacts) from {FileCount} files for {Repo}",
            totalIndexed, sdRecords.Count, artifactRecords.Count, fshFilePaths.Count, repoFullName);

        return totalIndexed;
    }

    private static GitHubCanonicalArtifactRecord BuildCanonicalArtifactRecord(
        string repoFullName,
        string relativePath,
        FshDefinitionInfo def,
        string? canonicalUrl)
    {
        string resourceType = def.Kind switch
        {
            FshDefinitionKind.CodeSystem => "CodeSystem",
            FshDefinitionKind.ValueSet => "ValueSet",
            FshDefinitionKind.DefinitionalInstance => def.InstanceOf ?? "Unknown",
            _ => "Unknown",
        };

        return new GitHubCanonicalArtifactRecord
        {
            Id = GitHubCanonicalArtifactRecord.GetIndex(),
            RepoFullName = repoFullName,
            FilePath = relativePath,
            ResourceType = resourceType,
            Url = canonicalUrl ?? $"urn:unknown:{def.Name}",
            Name = def.Name,
            Title = def.Title,
            Version = def.ExplicitVersion,
            Status = def.ExplicitStatus,
            Description = def.Description,
            Publisher = null,
            WorkGroup = null,
            FhirMaturity = null,
            StandardsStatus = null,
            TypeSpecificData = null,
            Format = "fsh",
        };
    }

    private static GitHubStructureDefinitionRecord BuildStructureDefinitionRecord(
        string repoFullName,
        string relativePath,
        FshDefinitionInfo def,
        string? canonicalUrl)
    {
        // Map FSH definition kind to SD kind and artifact class
        (string kind, string artifactClass) = def.Kind switch
        {
            FshDefinitionKind.Profile => ("resource", "Profile"),
            FshDefinitionKind.Extension => ("complex-type", "Extension"),
            FshDefinitionKind.Resource => ("resource", "Resource"),
            FshDefinitionKind.Logical => ("logical", "LogicalModel"),
            _ => ("resource", "Unknown"),
        };

        string? baseDefinition = def.Parent is not null
            ? $"http://hl7.org/fhir/StructureDefinition/{def.Parent}"
            : null;

        return new GitHubStructureDefinitionRecord
        {
            Id = GitHubStructureDefinitionRecord.GetIndex(),
            RepoFullName = repoFullName,
            FilePath = relativePath,
            Url = canonicalUrl ?? $"urn:unknown:{def.Name}",
            Name = def.Name,
            Title = def.Title,
            Status = def.ExplicitStatus,
            ArtifactClass = artifactClass,
            Kind = kind,
            IsAbstract = null,
            FhirType = def.Parent,
            BaseDefinition = baseDefinition,
            Derivation = def.Kind == FshDefinitionKind.Resource ? "specialization" : "constraint",
            FhirVersion = null,
            Description = def.Description,
            Publisher = null,
            WorkGroup = null,
            FhirMaturity = null,
            StandardsStatus = null,
            Category = null,
            Contexts = null,
        };
    }
}
