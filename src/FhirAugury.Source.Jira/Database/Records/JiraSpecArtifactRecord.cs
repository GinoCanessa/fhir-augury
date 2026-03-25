using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>Parsed JIRA-Spec-Artifacts data linking specs to Git repos.</summary>
[LdgSQLiteTable("jira_spec_artifacts")]
[LdgSQLiteIndex(nameof(Family), nameof(SpecKey))]
public partial record class JiraSpecArtifactRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string Family { get; set; }

    [LdgSQLiteUnique]
    public required string SpecKey { get; set; }

    public required string SpecName { get; set; }
    public required string? GitUrl { get; set; }
    public required string? PublishedUrl { get; set; }
    public required string? DefaultWorkgroup { get; set; }
}
