using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>
/// Ballot Definition (BALDEF-*) Jira ticket. Defines a single ballot
/// package (artifact bundle that goes out for a ballot cycle). Inherits
/// the shared Jira base columns from <see cref="JiraIssueBaseRecord"/>
/// and adds BALDEF-specific custom-field columns harvested in the
/// 2026-04-23 field sweep (see scratch/0423-02/plan.md §2.1).
/// </summary>
[LdgSQLiteTable("jira_baldef")]
[LdgSQLiteIndex(nameof(ProjectKey), nameof(Key))]
[LdgSQLiteIndex(nameof(Status))]
[LdgSQLiteIndex(nameof(UpdatedAt))]
[LdgSQLiteIndex(nameof(Type))]
[LdgSQLiteIndex(nameof(Priority))]
[LdgSQLiteIndex(nameof(Assignee))]
[LdgSQLiteIndex(nameof(Reporter))]
[LdgSQLiteIndex(nameof(CreatedAt))]
[LdgSQLiteIndex(nameof(ProcessedLocallyAt))]
[LdgSQLiteIndex(nameof(BallotCycle))]
[LdgSQLiteIndex(nameof(BallotCategory))]
[LdgSQLiteIndex(nameof(Specification), nameof(BallotCycle))]
[LdgSQLiteIndex(nameof(BallotCloses))]
[LdgSQLiteIndex(nameof(ApprovalStatus))]
[LdgSQLiteIndex(nameof(Reconciled))]
public partial record class JiraBaldefRecord : JiraIssueBaseRecord
{
    /// <summary>Raw ballot code, e.g. <c>2019-Sep | FHIR IG LIVD R1</c>.</summary>
    public string? BallotCode { get; set; }

    /// <summary>Cycle component parsed from <see cref="BallotCode"/> (e.g. <c>2019-Sep</c>).</summary>
    public string? BallotCycle { get; set; }

    /// <summary>Package-name component parsed from <see cref="BallotCode"/>.</summary>
    public string? BallotPackageName { get; set; }

    public string? BallotCategory { get; set; }
    public string? Specification { get; set; }
    public string? SpecificationLocation { get; set; }
    public DateTimeOffset? BallotOpens { get; set; }
    public DateTimeOffset? BallotCloses { get; set; }
    public string? ProductFamily { get; set; }
    public string? ApprovalStatus { get; set; }
    public int? VotersTotalEligible { get; set; }
    public int? VotersAffirmative { get; set; }
    public int? VotersNegative { get; set; }
    public int? VotersAbstain { get; set; }
    public string? OrganizationalParticipation { get; set; }
    public string? OrganizationalParticipationPlain { get; set; }
    public string? Reconciled { get; set; }
    public string? RelatedArtifacts { get; set; }
    public string? RelatedPages { get; set; }
}
