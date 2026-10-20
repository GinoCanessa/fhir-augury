using System.Text.Json.Serialization;
using FhirAugury.Common.Text;

namespace FhirAugury.Source.Jira.Api;

/// <summary>Source-local, deliberate maintenance. Neither mode ingests issues.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JiraPublicPeoplePreviewRequest
{
    public IReadOnlyList<string> Keys { get; init; } = [];
    public string EvidenceMode { get; init; } = JiraPublicPeopleEvidenceModes.CacheOnly;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JiraPublicPeopleApplyRequest
{
    public string PreviewToken { get; init; } = "";
    public bool AcknowledgeSharedUserImpact { get; init; }
}

public static class JiraPublicPeopleEvidenceModes
{
    public const string CacheOnly = "cache-only";
    public const string Upstream = "upstream";
}

/// <summary>Diagnostics deliberately contain no identity or display-name values.</summary>
public sealed record JiraPublicPeopleRoleResult(
    string Role,
    int? RequesterIndex,
    IReadOnlyList<string> Reasons,
    bool WillAddBinding = false,
    bool WillChangePublicName = false);

public sealed record JiraPublicPeopleTicketResult(
    string Key,
    string Shape,
    IReadOnlyList<JiraPublicPeopleRoleResult> Roles);

/// <summary>
/// Includes exact bindings and compatibility-lookup effects, not just rows to
/// write. A shared user's display change never rebinds a nonselected ticket.
/// </summary>
public sealed record JiraPublicPeopleAffectedTicket(
    string Key,
    string Shape,
    bool IsSelected,
    int ReporterRoles,
    int AssigneeRoles,
    int InPersonRequesterRoles,
    int VoteMoverRoles,
    int VoteSeconderRoles);

public sealed record JiraPublicPeopleChanges(
    int UsersCreated = 0,
    int UsersUpdated = 0,
    int IssueRowsUpdated = 0,
    int RequesterAssociationsAdded = 0)
{
    [JsonIgnore]
    public bool HasChanges =>
        UsersCreated + UsersUpdated + IssueRowsUpdated + RequesterAssociationsAdded > 0;
}

public sealed record JiraPublicPeoplePreviewResponse
{
    public required string Code { get; init; }
    public string? PreviewToken { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public long? ContentRevision { get; init; }
    public int ObservationVersion { get; init; } = 1;
    public int PublicDisplayNamePolicyVersion { get; init; } = PublicDisplayNamePolicy.CurrentVersion;
    public bool CanApply => PreviewToken is not null;
    public bool RequiresSharedUserImpactAcknowledgement { get; init; }
    public IReadOnlyList<JiraPublicPeopleTicketResult> Tickets { get; init; } = [];
    public IReadOnlyList<JiraPublicPeopleAffectedTicket> AffectedTickets { get; init; } = [];
    public JiraPublicPeopleChanges Changes { get; init; } = new();
}

public sealed record JiraPublicPeopleApplyResponse(
    string Code,
    long? ContentRevision = null,
    JiraPublicPeopleChanges? Changes = null);

public static class JiraPublicPeopleCodes
{
    public const string PreviewReady = "preview-ready";
    public const string Applied = "applied";
    public const string NoChange = "no-change";
    public const string InvalidRequest = "invalid-request";
    public const string SourceBusy = "source-busy";
    public const string PreviewCapacity = "preview-capacity";
    public const string PreviewExpired = "preview-expired";
    public const string StalePreview = "stale-preview";
    public const string SharedImpactAcknowledgementRequired = "shared-impact-acknowledgement-required";
    public const string SharedImpactChanged = "shared-impact-changed";
    public const string SharedImpactTooLarge = "shared-impact-too-large";
    public const string UpstreamUnavailable = "upstream-evidence-unavailable";
    public const string UnknownCacheOrigin = "unknown-cache-origin";
    public const string MissingObservation = "missing-observation";
    public const string MissingRevision = "missing-explicit-revision";
    public const string RevisionMismatch = "revision-mismatch";
    public const string ConflictingObservations = "conflicting-observations";
    public const string IdentityConflict = "identity-conflict";
    public const string IssueNotFound = "issue-not-found";
    public const string MissingField = "missing-field";
    public const string MalformedValue = "malformed-value";
    public const string RoleAbsent = "role-absent";
    public const string MissingIdentityBinding = "missing-identity-binding";
    public const string MissingExplicitNameEvidence = "missing-explicit-name-evidence";
    public const string PolicyRejected = "policy-rejected";
    public const string AvailablePublicName = "available-public-name";
    public const string Cancelled = "cancelled-before-commit";
    public const string WriteFailed = "conditional-write-failed";
    public const string InternalError = "internal-error";

    public static int HttpStatus(string code) => code switch
    {
        PreviewReady or Applied or NoChange => 200,
        InvalidRequest => 400,
        PreviewExpired => 410,
        InternalError => 500,
        UpstreamUnavailable => 503,
        _ => 409,
    };
}
