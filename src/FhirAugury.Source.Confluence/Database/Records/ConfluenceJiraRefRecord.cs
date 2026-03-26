using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Confluence.Database.Records;

/// <summary>A Jira key reference found in a Confluence page.</summary>
[LdgSQLiteTable("confluence_jira_refs")]
[LdgSQLiteIndex(nameof(JiraKey))]
[LdgSQLiteIndex(nameof(ConfluenceId))]
[LdgSQLiteIndex(nameof(JiraKey), nameof(ConfluenceId))]
public partial record class ConfluenceJiraRefRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string ConfluenceId { get; set; }  // FK to confluence_pages
    public required string JiraKey { get; set; }        // e.g., "FHIR-55001"
    public required string? Context { get; set; }       // surrounding text
}
