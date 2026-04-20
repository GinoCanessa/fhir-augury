using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>
/// A Jira project tracked by the source. Persisted so that operator edits
/// (e.g. <see cref="BaselineValue"/>) survive ingestion passes and config
/// reloads. Rows are seeded from <c>JiraServiceOptions.Projects</c> at
/// ingestion start; on subsequent passes only ingestion-owned columns
/// (Enabled, IssueCount, LastSyncAt) are refreshed.
/// </summary>
[LdgSQLiteTable("jira_projects")]
public partial record class JiraProjectRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    /// <summary>The Jira project key (e.g. "FHIR", "BALLOT").</summary>
    [LdgSQLiteUnique]
    public required string Key { get; set; }

    /// <summary>Whether the project is enabled for ingestion (mirrors config).</summary>
    public required bool Enabled { get; set; } = true;

    /// <summary>
    /// Baseline ranking value (0–10, default 5). FTS scores are multiplied by
    /// <c>BaselineValue / 5.0</c>; the default is neutral, <c>0</c> suppresses
    /// the project from ranked output, higher values up-rank it.
    /// </summary>
    public required int BaselineValue { get; set; } = 5;

    /// <summary>Last observed issue count for this project (informational).</summary>
    public required int IssueCount { get; set; } = 0;

    /// <summary>Last successful sync time for this project, if known.</summary>
    public required DateTimeOffset? LastSyncAt { get; set; }
}
