using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Spec list entry from a JIRA-Spec-Artifacts SPECS-[FAMILY].xml file.</summary>
[LdgSQLiteTable("jira_spec_families")]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(Family))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(SpecKey))]
public partial record class JiraSpecFamilyRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }
    public required string Family { get; set; }
    public required string SpecKey { get; set; }
    public required string Name { get; set; }
    public string? Accelerator { get; set; }
    public required bool Deprecated { get; set; }
}
