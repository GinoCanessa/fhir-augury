using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Element-level data from StructureDefinition differentials.</summary>
[LdgSQLiteTable("github_sd_elements")]
[LdgSQLiteIndex(nameof(StructureDefinitionId))]
[LdgSQLiteIndex(nameof(Path))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(Path))]
[LdgSQLiteIndex(nameof(BindingValueSet))]
public partial record class GitHubSdElementRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }

    /// <summary>FK to github_structure_definitions.Id</summary>
    public required int StructureDefinitionId { get; set; }

    /// <summary>e.g., Patient.contact.relationship</summary>
    public required string ElementId { get; set; }

    /// <summary>e.g., Patient.contact.relationship</summary>
    public required string Path { get; set; }

    /// <summary>Last segment of path, e.g., relationship</summary>
    public required string Name { get; set; }

    /// <summary>Brief description</summary>
    public string? Short { get; set; }

    /// <summary>Full definition text</summary>
    public string? Definition { get; set; }

    /// <summary>Additional notes</summary>
    public string? Comment { get; set; }

    public int? MinCardinality { get; set; }

    /// <summary>"0", "1", or "*"</summary>
    public string? MaxCardinality { get; set; }

    /// <summary>Semicolon-joined type codes</summary>
    public string? Types { get; set; }

    /// <summary>Semicolon-joined profile canonical URLs</summary>
    public string? TypeProfiles { get; set; }

    /// <summary>Semicolon-joined target profile URLs (for Reference types)</summary>
    public string? TargetProfiles { get; set; }

    /// <summary>required / extensible / preferred / example</summary>
    public string? BindingStrength { get; set; }

    /// <summary>ValueSet canonical URL</summary>
    public string? BindingValueSet { get; set; }

    public string? SliceName { get; set; }

    /// <summary>0 or 1</summary>
    public int? IsModifier { get; set; }

    /// <summary>0 or 1</summary>
    public int? IsSummary { get; set; }

    /// <summary>Fixed value as string</summary>
    public string? FixedValue { get; set; }

    /// <summary>Pattern value as string</summary>
    public string? PatternValue { get; set; }

    /// <summary>Sequential order within the differential</summary>
    public required int FieldOrder { get; set; }
}
