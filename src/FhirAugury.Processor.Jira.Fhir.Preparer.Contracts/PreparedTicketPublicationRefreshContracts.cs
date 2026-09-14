using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;

/// <summary>
/// Stable machine-readable failure codes returned by the publication-refresh
/// endpoint and recorded by its durable finalization stage.
/// </summary>
public static class PreparedTicketPublicationRefreshFailureCodes
{
    public const string SourceRunNotFound = "source-run-not-found";
    public const string InvalidSourceRun = "invalid-source-run";
    public const string AuthoringNotActivated = "authoring-not-activated";
    public const string CutoverInProgress = "cutover-in-progress";
    public const string RevalidationRequired = "revalidation-required";
    public const string MutationFenceUnavailable =
        "mutation-fence-unavailable";
    public const string RunNotActive = "run-not-active";
    public const string SourceRevisionMismatch =
        "source-revision-mismatch";
    public const string SourceGenerationConflict =
        "source-generation-conflict";
    public const string StageFingerprintMismatch =
        "stage-fingerprint-mismatch";
    public const string SourceUnavailable = "source-unavailable";
    public const string TicketNotFound = "ticket-not-found";
    public const string InvalidSourceResponse = "invalid-source-response";
    public const string MissingSourceProvenance =
        "missing-source-provenance";
    public const string UnstableSource = "unstable-source";
    public const string MissingProjectProvenance =
        "missing-project-provenance";
    public const string PeoplePolicyNotCurrent =
        "people-policy-not-current";
}

/// <summary>
/// The newly durable refresh run returned after a successful start.
/// </summary>
public sealed record PreparedTicketPublicationRefreshResult(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items);

/// <summary>
/// Typed failure body for a publication-refresh start request.
/// </summary>
public sealed record PreparedTicketPublicationRefreshFailure(
    string Error,
    string? Detail = null,
    IReadOnlyList<string>? ConflictingRunIds = null,
    string? RunId = null);
