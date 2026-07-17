using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Specification-level metadata from JIRA-Spec-Artifacts XML files.</summary>
[LdgSQLiteTable("jira_specs")]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(SpecKey))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(Family))]
[LdgSQLiteIndex(nameof(CanonicalUrl))]
[LdgSQLiteIndex(nameof(GitUrl))]
[LdgSQLiteIndex(nameof(DefaultWorkgroup))]
public partial record class JiraSpecRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }
    public required string FilePath { get; set; }
    public required string Family { get; set; }
    public required string SpecKey { get; set; }
    public string? SpecName { get; set; }
    public string? CanonicalUrl { get; set; }
    public string? CiUrl { get; set; }
    public string? BallotUrl { get; set; }
    public string? GitUrl { get; set; }
    public string? DefaultWorkgroup { get; set; }
    public required string DefaultVersion { get; set; }

    /// <summary>Serialized artifact page extensions (JSON string array), null if none.</summary>
    public string? ArtifactPageExtensions { get; set; }
}
