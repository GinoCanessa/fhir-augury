using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Semantic tag applied to a file based on repository discovery patterns.</summary>
[LdgSQLiteTable("github_file_tags")]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(FilePath))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(TagCategory))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(TagName))]
public partial record class GitHubFileTagRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }

    /// <summary>Relative to clone root, forward slashes.</summary>
    public required string FilePath { get; set; }

    /// <summary>Tag category: type, resource, logical-model, infrastructure, fhir-resource-type.</summary>
    public required string TagCategory { get; set; }

    /// <summary>Tag name: the artifact or resource type name.</summary>
    public required string TagName { get; set; }

    /// <summary>Optional modifier: draft, removed, etc.</summary>
    public string? TagModifier { get; set; }

    /// <summary>Configurable weight for search ranking.</summary>
    public double Weight { get; set; } = 1.0;
}
