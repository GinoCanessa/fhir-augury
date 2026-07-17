using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>
/// Ballot vote (BALLOT-*) Jira ticket. Per-voter, per-package vote-tracking
/// row; the package-level vote/comment narrative lives on the linked
/// FHIR-* change request, joined via <see cref="RelatedFhirIssue"/> or
/// <c>jira_issue_links</c>. Inherits the shared Jira base columns from
/// <see cref="JiraIssueBaseRecord"/>; see scratch/0423-02/plan.md §2.2
/// for the 2026-04-23 field sweep details.
/// </summary>
[LdgSQLiteTable("jira_ballot")]
[LdgSQLiteIndex(nameof(ProjectKey), nameof(Key))]
[LdgSQLiteIndex(nameof(Status))]
[LdgSQLiteIndex(nameof(UpdatedAt))]
[LdgSQLiteIndex(nameof(Type))]
[LdgSQLiteIndex(nameof(Priority))]
[LdgSQLiteIndex(nameof(Assignee))]
[LdgSQLiteIndex(nameof(Reporter))]
[LdgSQLiteIndex(nameof(CreatedAt))]
[LdgSQLiteIndex(nameof(ProcessedLocallyAt))]
[LdgSQLiteIndex(nameof(BallotPackageCode))]
[LdgSQLiteIndex(nameof(VoteBallot))]
[LdgSQLiteIndex(nameof(VoteItem))]
[LdgSQLiteIndex(nameof(Specification))]
[LdgSQLiteIndex(nameof(Organization))]
[LdgSQLiteIndex(nameof(RelatedFhirIssue))]
[LdgSQLiteIndex(nameof(VoteSameAs))]
public partial record class JiraBallotRecord : JiraIssueBaseRecord
{
    public string? VoteBallot { get; set; }
    public string? VoteItem { get; set; }
    public string? ExternalId { get; set; }
    public string? Organization { get; set; }
    public string? OrganizationCategory { get; set; }
    public string? BallotCategory { get; set; }
    public string? VoteSameAs { get; set; }
    public string? Specification { get; set; }
    public string? Reconciled { get; set; }

    /// <summary>Parsed package code from <c>&lt;summary&gt;</c> (matches <c>jira_baldef.BallotCode</c>).</summary>
    public string? BallotPackageCode { get; set; }

    /// <summary>Voter name parsed from <c>&lt;summary&gt;</c>.</summary>
    public string? Voter { get; set; }

    /// <summary>Cycle component parsed from <see cref="BallotPackageCode"/>.</summary>
    public string? BallotCycle { get; set; }

    /// <summary>
    /// First outward issue link with target matching <c>^FHIR-</c> (link
    /// types observed: <c>is created from</c>, <c>relates to</c>).
    /// Materialised at parse time so it can be indexed.
    /// </summary>
    public string? RelatedFhirIssue { get; set; }
}
