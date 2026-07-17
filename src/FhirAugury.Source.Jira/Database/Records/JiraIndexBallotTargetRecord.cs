using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>
/// Roll-up of FHIR-side ballot targets — i.e. the
/// <c>jira_issues.SelectedBallot</c> column. Renamed from
/// <c>jira_index_ballots</c> as part of Phase 6 (2026-04-23 plan §7.5)
/// to disambiguate from <c>jira_index_ballot_cycles</c> (vote-side
/// roll-up over <c>jira_baldef</c>/<c>jira_ballot</c>).
/// </summary>
[LdgSQLiteTable("jira_index_ballot_targets")]
public partial record class JiraIndexBallotTargetRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Name { get; set; }

    public required int IssueCount { get; set; }
}
