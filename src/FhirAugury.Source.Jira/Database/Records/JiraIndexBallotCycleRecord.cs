using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>
/// Per-ballot-cycle roll-up sourced from <c>jira_baldef.BallotCycle</c>
/// and <c>jira_ballot.BallotCycle</c>. <see cref="BallotLevel"/> mirrors
/// <c>jira_baldef.BallotCategory</c> (STU / Normative / Informative /
/// Comment-only); see Phase 6 plan §7.4.
/// </summary>
/// <remarks>
/// Composite uniqueness on (<see cref="BallotCycle"/>,
/// <see cref="BallotLevel"/>) is enforced in code by the index
/// builder's DELETE+INSERT cycle — CsLightDbGen
/// <c>[LdgSQLiteUnique]</c> is single-column only.
/// </remarks>
[LdgSQLiteTable("jira_index_ballot_cycles")]
[LdgSQLiteIndex(nameof(BallotCycle), nameof(BallotLevel))]
public partial record class JiraIndexBallotCycleRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    public required string BallotCycle { get; set; }

    public string? BallotLevel { get; set; }

    public int IssueCount { get; set; } = 0;

    public int AffirmativeVotes { get; set; } = 0;

    public int NegativeVotes { get; set; } = 0;

    public int NegativeWithCommentVotes { get; set; } = 0;

    public int AbstainVotes { get; set; } = 0;
}
