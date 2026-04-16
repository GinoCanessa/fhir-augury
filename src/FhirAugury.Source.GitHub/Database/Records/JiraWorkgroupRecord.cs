using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Workgroup entry from the JIRA-Spec-Artifacts _workgroups.xml file.</summary>
[LdgSQLiteTable("jira_workgroups")]
[LdgSQLiteIndex(nameof(RepoFullName))]
[LdgSQLiteIndex(nameof(WorkgroupKey))]
public partial record class JiraWorkgroupRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }
    public required string WorkgroupKey { get; set; }
    public required string Name { get; set; }
    public string? Webcode { get; set; }
    public string? Listserv { get; set; }
    public required bool Deprecated { get; set; }
}
