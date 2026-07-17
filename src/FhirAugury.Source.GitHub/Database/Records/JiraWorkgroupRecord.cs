using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Workgroup entry from the JIRA-Spec-Artifacts _workgroups.xml file.</summary>
[LdgSQLiteTable("jira_workgroups")]
[LdgSQLiteIndex(nameof(RepoFullName))]
[LdgSQLiteIndex(nameof(WorkgroupKey))]
[LdgSQLiteIndex(nameof(WorkGroupCode))]
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

    /// <summary>
    /// Canonical HL7 work-group <c>code</c> (from <c>hl7_workgroups</c>) the
    /// JIRA-Spec free-text <see cref="Name"/> resolved to. <c>null</c> when
    /// no match was found; preserves <see cref="Name"/> as the surfacing
    /// value for review via <c>/workgroups/unresolved</c>.
    /// </summary>
    public string? WorkGroupCode { get; set; }
}
