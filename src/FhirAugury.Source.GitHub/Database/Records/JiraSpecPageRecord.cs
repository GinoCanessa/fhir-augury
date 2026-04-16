using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.GitHub.Database.Records;

/// <summary>Page entry from a JIRA-Spec-Artifacts specification XML file.</summary>
[LdgSQLiteTable("jira_spec_pages")]
[LdgSQLiteIndex(nameof(JiraSpecId))]
[LdgSQLiteIndex(nameof(RepoFullName), nameof(SpecKey))]
[LdgSQLiteIndex(nameof(PageKey))]
public partial record class JiraSpecPageRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string RepoFullName { get; set; }
    public required string SpecKey { get; set; }
    public required int JiraSpecId { get; set; }
    public required string PageKey { get; set; }
    public required string Name { get; set; }
    public string? Url { get; set; }
    public string? Workgroup { get; set; }
    public required bool Deprecated { get; set; }

    /// <summary>Serialized other page URLs (JSON string array), null if none.</summary>
    public string? OtherPageUrls { get; set; }
}
