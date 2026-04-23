using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>
/// Roll-up of issue Priority across the four Jira issue-shape tables.
/// </summary>
/// <remarks>
/// <see cref="SourceTable"/> defaults to the sentinel <c>"all"</c>: the
/// Phase 6 index builder collapses each Priority into a single row that
/// sums across <c>jira_issues</c>, <c>jira_pss</c>, <c>jira_baldef</c>,
/// <c>jira_ballot</c>. The column exists so consumers can later opt
/// in to per-shape rows (deviation from plan §7.1; see plan progress
/// table). Composite (<see cref="SourceTable"/>, <see cref="Name"/>)
/// uniqueness is enforced in code by the builder's DELETE+INSERT.
/// </remarks>
[LdgSQLiteTable("jira_index_priorities")]
[LdgSQLiteIndex(nameof(SourceTable), nameof(Name))]
public partial record class JiraIndexPriorityRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string Name { get; set; }

    public string SourceTable { get; set; } = "all";

    public required int IssueCount { get; set; }
}
