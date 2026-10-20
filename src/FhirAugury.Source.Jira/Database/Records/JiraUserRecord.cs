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
    /// True when <see cref="Username"/> is a Jira account identifier rather
    /// than the synthetic key used for a display-name-only row.
    /// </summary>
    public bool HasAccountUsername { get; set; }

    /// <summary>
    /// True only when Jira, or an explicitly display-name-only caller,
    /// supplied a policy-safe <see cref="DisplayName"/>. Username-derived or
    /// identifier-valued strings are not eligible for public projections.
    /// </summary>
    public bool HasExplicitDisplayName { get; set; }
}
