using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>A directional link between two Jira issues.</summary>
[LdgSQLiteTable("jira_issue_links")]
[LdgSQLiteIndex(nameof(SourceKey))]
[LdgSQLiteIndex(nameof(TargetKey))]
[LdgSQLiteIndex(nameof(LinkType))]
public partial record class JiraIssueLinkRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string SourceKey { get; set; }
    public required string TargetKey { get; set; }
    public required string LinkType { get; set; }
}
