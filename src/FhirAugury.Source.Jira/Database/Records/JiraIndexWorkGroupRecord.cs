using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

[LdgSQLiteTable("jira_index_workgroups")]
public partial record class JiraIndexWorkGroupRecord
{
    [LdgSQLiteKey]
    public required int Id { get; set; }

    [LdgSQLiteUnique]
    public required string Name { get; set; }

    /// <summary>
    /// Optional FK into <c>hl7_workgroups.Id</c> resolved by the index
    /// builder (Name first, then NameClean fallback). Null when no canonical
    /// HL7 work group could be matched.
    /// </summary>
    public int? WorkGroupId { get; set; }

    public required int IssueCount { get; set; }

    public required int IssueCountSubmitted { get; set; }
    public required int IssueCountTriaged { get; set; }
    public required int IssueCountWaitingForInput { get; set; }
    public required int IssueCountNoChange { get; set; }
    public required int IssueCountChangeRequired { get; set; }
    public required int IssueCountPublished { get; set; }
    public required int IssueCountApplied { get; set; }
    public required int IssueCountDuplicate { get; set; }
    public required int IssueCountClosed { get; set; }
    public required int IssueCountBalloted { get; set; }
    public required int IssueCountWithdrawn { get; set; }
    public required int IssueCountDeferred { get; set; }
    public required int IssueCountOther { get; set; }

    // Phase 6 plan §7.2: PSS-flavoured buckets sourced from
    // jira_pss.SponsoringWorkGroup (with SponsoringWorkGroupsLegacy
    // fallback). Defaulted so existing test/code-paths that only set
    // the FHIR buckets continue to compile.
    public int IssueCountSubmittedPss { get; set; } = 0;
    public int IssueCountApprovedPss { get; set; } = 0;
    public int IssueCountRejectedPss { get; set; } = 0;
    public int IssueCountOtherPss { get; set; } = 0;

    // Phase 6 plan §7.2: ballot-disposition buckets sourced from
    // jira_ballot.VoteBallot, attributed to the workgroup of the
    // ballot's package (BALLOT → BallotPackageCode → jira_baldef →
    // linked PSS → SponsoringWorkGroup). Unresolved chains land in a
    // synthetic "(Unattributed)" workgroup row.
    public int AffirmativeVotes { get; set; } = 0;
    public int NegativeVotes { get; set; } = 0;
    public int NegativeWithCommentVotes { get; set; } = 0;
    public int AbstainVotes { get; set; } = 0;
}
