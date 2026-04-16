using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Published version entry from a JIRA-Spec-Artifacts specification XML file.</summary>
[LdgSQLiteTable("jira_spec_versions")]
[LdgSQLiteIndex(nameof(JiraSpecId))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(SpecKey))]
public partial record class JiraSpecVersionRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }
    public required string SpecKey { get; set; }
    public required int JiraSpecId { get; set; }
    public required string Code { get; set; }
    public string? Url { get; set; }
    public required bool Deprecated { get; set; }
}
