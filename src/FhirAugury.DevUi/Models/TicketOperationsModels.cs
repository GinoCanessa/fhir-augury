using FhirAugury.Common.Api;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using FhirAugury.Publishing.Tickets;

namespace FhirAugury.DevUi.Models;

public sealed record TicketWorkflowDefinition(
    string RouteKey,
    string DisplayName,
    string ProcessingServiceName,
    TicketSiteKind SiteKind,
    string SiteTitle,
    string SiteFolder);

public sealed record JiraTicketKeyParseResult(
    IReadOnlyList<string> ValidKeys,
    IReadOnlyList<string> InvalidTokens)
{
    public bool IsValid =>
        InvalidTokens.Count == 0 && ValidKeys.Count > 0;
}

public enum TicketSelectionMode
{
    Configured,
    Explicit,
}

public sealed record TicketRunStartRequest(
    string Workflow,
    TicketSelectionMode SelectionMode,
    string? ExplicitTicketKeys = null,
    bool DatabaseOnly = false);

public enum ServiceReadinessState
{
    Ready,
    Degraded,
    Unavailable,
    NotObserved,
    Disabled,
}

public sealed record ServiceReadinessObservation(
    string Name,
    string ServiceKind,
    ServiceReadinessState State,
    string Message,
    bool IsBlocker,
    DateTimeOffset? CheckedAt,
    ServiceHealthInfo? Source);

public sealed record TicketWorkflowReadiness(
    TicketWorkflowDefinition Workflow,
    DateTimeOffset? CheckedAt,
    ServiceReadinessObservation Orchestrator,
    ServiceReadinessObservation Processor,
    IReadOnlyList<ServiceReadinessObservation> RequiredServices)
{
    public IReadOnlyList<ServiceReadinessObservation> Blockers =>
        new[] { Orchestrator, Processor }
            .Concat(RequiredServices)
            .Where(observation => observation.IsBlocker)
            .ToArray();

    public bool CanStart => Blockers.Count == 0;
}

public enum ProcessorRunOutcome
{
    Active,
    RecoverableError,
    Completed,
    CompletedWithSupersededItems,
    CompletedDatabaseOnly,
    Superseded,
    Abandoned,
}

public enum PublicationOutcome
{
    Unavailable,
    Ready,
    Publishing,
    Published,
    Failed,
}

public sealed record TicketRunOutcome(
    ProcessorRunOutcome Processor,
    PublicationOutcome Publication);

public sealed record ReviewSiteCoordinates(
    TicketWorkflowDefinition Workflow,
    string RunId,
    string SnapshotPairDirectory,
    string SiteRoot,
    string SiteDirectory,
    string SiteManifestPath,
    string SiteUrl);

public sealed record ReviewSitePublication(
    ReviewSiteCoordinates Coordinates,
    TicketSiteManifest Manifest,
    bool Reconstructed);

public sealed record TicketRunDetails(
    TicketWorkflowDefinition Workflow,
    AuthoringRunResponse Response,
    TicketRunOutcome Outcome,
    ReviewSitePublication? Publication,
    string? PublicationError = null,
    PublicationReconciliationStatusResult? Reconciliation = null);

public enum TicketOperationDisposition
{
    Succeeded,
    NoCandidates,
    InvalidInput,
    Conflict,
    NotAllowed,
    Busy,
    OutcomeUnknown,
    Failed,
}

public enum TicketReconciliationOutcome
{
    Pending,
    Succeeded,
    Failed,
}

public sealed record TicketReconciliation(
    TicketReconciliationOutcome Outcome,
    string? Error = null);

public sealed record TicketStartResult(
    TicketOperationDisposition Disposition,
    TicketWorkflowDefinition Workflow,
    AuthoringRunResponse? Run = null,
    JiraTicketKeyParseResult? ParsedKeys = null,
    IReadOnlyList<string>? RelatedRunIds = null,
    IReadOnlyList<AuthoringRunStatus>? InspectionCandidates = null,
    string? Message = null,
    TicketReconciliation? Reconciliation = null,
    string? FailureCode = null)
{
    public IReadOnlyList<string> ConflictingRunIds =>
        RelatedRunIds ?? [];

    public IReadOnlyList<AuthoringRunStatus> Candidates =>
        InspectionCandidates ?? [];
}

public sealed record TicketItemMutationResult(
    TicketOperationDisposition Disposition,
    TicketWorkflowDefinition Workflow,
    string RunId,
    string ItemId,
    AuthoringRunResponse? ReconciledRun,
    string? Message = null,
    TicketReconciliation? Reconciliation = null);

public sealed record TicketPublicationResult(
    TicketOperationDisposition Disposition,
    TicketWorkflowDefinition Workflow,
    string RunId,
    ReviewSitePublication? Publication,
    bool VerifiedPairAvailable,
    string? Message = null,
    IReadOnlyList<string>? Warnings = null);

public sealed record TicketPublicationRefreshResult(
    TicketOperationDisposition Disposition,
    TicketWorkflowDefinition Workflow,
    string SourceRunId,
    AuthoringRunResponse? Run = null,
    IReadOnlyList<string>? RelatedRunIds = null,
    IReadOnlyList<AuthoringRunStatus>? InspectionCandidates = null,
    string? Message = null,
    TicketReconciliation? Reconciliation = null,
    string? FailureCode = null)
{
    public string? RefreshRunId =>
        Run?.Run.RunId;

    public IReadOnlyList<string> ConflictingRunIds =>
        RelatedRunIds ?? [];

    public IReadOnlyList<AuthoringRunStatus> Candidates =>
        InspectionCandidates ?? [];
}

public sealed record TicketPublicationReconciliationResult(
    TicketOperationDisposition Disposition,
    TicketWorkflowDefinition Workflow,
    string RunId,
    PublicationReconciliationStatusResult? Status = null,
    IReadOnlyList<string>? RelatedRunIds = null,
    string? Message = null,
    string? FailureCode = null)
{
    public IReadOnlyList<string> ConflictingRunIds =>
        RelatedRunIds ?? [];
}
