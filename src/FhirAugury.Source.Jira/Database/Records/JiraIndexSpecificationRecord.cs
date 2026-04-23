using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>
/// Roll-up of Specification across <c>jira_issues</c>,
/// <c>jira_baldef</c>, and <c>jira_ballot</c>. <see cref="IssueCount"/>
/// holds the union total; <see cref="IssueCountFhir"/>/
/// <see cref="IssueCountBaldef"/>/<see cref="IssueCountBallot"/> give
/// the per-shape breakdown (Phase 6 plan §7.3).
/// </summary>
[LdgSQLiteTable("jira_index_specifications")]
public partial record class JiraIndexSpecificationRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Name { get; set; }

    public required int IssueCount { get; set; }

    public int IssueCountFhir { get; set; } = 0;

    public int IssueCountBaldef { get; set; } = 0;

    public int IssueCountBallot { get; set; } = 0;
}
