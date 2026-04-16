using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>A Jira user referenced by one or more issues.</summary>
[LdgSQLiteTable("jira_users")]
[LdgSQLiteIndex(nameof(DisplayName))]
public partial record class JiraUserRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Username { get; set; }

    public required string DisplayName { get; set; }
}
