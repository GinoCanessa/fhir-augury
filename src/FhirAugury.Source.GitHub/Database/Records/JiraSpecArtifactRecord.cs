using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Artifact entry from a JIRA-Spec-Artifacts specification XML file.</summary>
[LdgSQLiteTable("jira_spec_artifacts")]
[LdgSQLiteIndex(nameof(JiraSpecId))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(SpecKey))]
[LdgSQLiteIndex(nameof(ArtifactKey))]
[LdgSQLiteIndex(nameof(ArtifactId))]
[LdgSQLiteIndex(nameof(ResourceType))]
public partial record class JiraSpecArtifactRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }
    public required string SpecKey { get; set; }
    public required int JiraSpecId { get; set; }
    public required string ArtifactKey { get; set; }
    public required string Name { get; set; }
    public string? ArtifactId { get; set; }
    public string? ResourceType { get; set; }
    public string? Workgroup { get; set; }
    public required bool Deprecated { get; set; }

    /// <summary>Serialized other artifact IDs (JSON string array), null if none.</summary>
    public string? OtherArtifactIds { get; set; }
}
