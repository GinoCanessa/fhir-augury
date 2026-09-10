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

    /// <summary>
    /// True only when Jira, or an explicitly display-name-only caller,
    /// supplied <see cref="DisplayName"/>. Username-derived placeholders are
    /// not eligible for public people projections.
    /// </summary>
    public bool HasExplicitDisplayName { get; set; }
}
