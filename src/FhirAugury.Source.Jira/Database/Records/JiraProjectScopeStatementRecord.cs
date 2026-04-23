using CsLightDbGen.SQLiteGenerator;

namespace FhirAugury.Source.Jira.Database.Records;

/// <summary>
/// Project Scope Statement (PSS-*) Jira ticket. Governance-lifecycle row
/// describing a project a workgroup wants to start. Inherits the shared
/// Jira base columns from <see cref="JiraIssueBaseRecord"/> and adds the
/// PSS-specific custom-field columns harvested in the 2026-04-23 field
/// sweep (see scratch/0423-02/plan.md §2.3).
/// </summary>
[LdgSQLiteTable("jira_pss")]
[LdgSQLiteIndex(nameof(ProjectKey), nameof(Key))]
[LdgSQLiteIndex(nameof(Status))]
[LdgSQLiteIndex(nameof(UpdatedAt))]
[LdgSQLiteIndex(nameof(Type))]
[LdgSQLiteIndex(nameof(Priority))]
[LdgSQLiteIndex(nameof(Assignee))]
[LdgSQLiteIndex(nameof(Reporter))]
[LdgSQLiteIndex(nameof(CreatedAt))]
[LdgSQLiteIndex(nameof(ProcessedLocallyAt))]
[LdgSQLiteIndex(nameof(SponsoringWorkGroup), nameof(UpdatedAt))]
[LdgSQLiteIndex(nameof(BallotCycleTarget))]
[LdgSQLiteIndex(nameof(Realm))]
[LdgSQLiteIndex(nameof(SteeringDivision))]
[LdgSQLiteIndex(nameof(ApprovalDate))]
[LdgSQLiteIndex(nameof(NormativeNotification))]
public partial record class JiraProjectScopeStatementRecord : JiraIssueBaseRecord
{
    public string? SponsoringWorkGroup { get; set; }
    public string? SponsoringWorkGroupsLegacy { get; set; }
    public string? CoSponsoringWorkGroups { get; set; }
    public string? CoSponsoringWorkGroupsLegacy { get; set; }
    public string? Realm { get; set; }
    public string? OtherRealm { get; set; }
    public string? SteeringDivision { get; set; }
    public string? ManagementGroups { get; set; }
    public string? ProductFamily { get; set; }
    public string? BallotCycleTarget { get; set; }
    public DateTimeOffset? ApprovalDate { get; set; }
    public DateTimeOffset? RejectionDate { get; set; }
    public DateTimeOffset? OptOutDate { get; set; }
    public string? ProjectCommonName { get; set; }
    public string? ProjectDescription { get; set; }
    public string? ProjectDescriptionPlain { get; set; }
    public string? ProjectNeed { get; set; }
    public string? ProjectNeedPlain { get; set; }
    public string? ProjectDocumentRepositoryUrl { get; set; }
    public string? ProjectFacilitator { get; set; }
    public string? PublishingFacilitator { get; set; }
    public string? VocabularyFacilitator { get; set; }
    public string? OtherInterestedParties { get; set; }
    public string? Implementers { get; set; }
    public string? Stakeholders { get; set; }
    public string? OtherStakeholders { get; set; }
    public string? ProjectDependencies { get; set; }
    public string? ProjectDependenciesPlain { get; set; }
    public string? Accelerators { get; set; }
    public string? NormativeNotification { get; set; }
    public string? ProductInfo { get; set; }
    public string? ExternalContentMajority { get; set; }
    public string? JointCopyright { get; set; }
    public string? ExternalCodeSystems { get; set; }
    public string? IsoStandardToAdopt { get; set; }
    public string? ExcerptText { get; set; }
    public string? UnitOfMeasure { get; set; }
    public string? ExternalDrivers { get; set; }
    public string? BackwardsCompatibility { get; set; }
    public string? ExternalProjectCollaboration { get; set; }
    public string? DevelopersOfExternalContent { get; set; }
    public string? ContactEmail { get; set; }
}
