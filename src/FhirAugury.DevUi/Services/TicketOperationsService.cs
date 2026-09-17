using System.Net;
using System.Text.Json;
using FhirAugury.DevUi.Configuration;
using FhirAugury.DevUi.Models;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Publishing.Tickets;
using Microsoft.Extensions.Options;

namespace FhirAugury.DevUi.Services;

public sealed class TicketOperationsService : IDisposable
{
    private readonly IAuthoringControlClient _authoringClient;
    private readonly IReviewSiteStore _siteStore;
    private readonly ITicketSitePublisher _publisher;
    private readonly TicketWorkflowCatalog _catalog;
    private readonly JiraTicketKeyParser _ticketKeyParser;
    private readonly RunOutcomeClassifier _outcomeClassifier;
    private readonly DevUiOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TicketOperationsService> _logger;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Dictionary<
        string,
        UnknownStartReview> _unknownStarts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _unknownStartsLock = new();
    private readonly Dictionary<
        PublicationRefreshReviewKey,
        TicketPublicationRefreshResult>
        _unknownPublicationRefreshes = [];
    private readonly object _unknownPublicationRefreshesLock = new();
    private bool _disposed;

    public TicketOperationsService(
        IAuthoringControlClient authoringClient,
        IReviewSiteStore siteStore,
        ITicketSitePublisher publisher,
        TicketWorkflowCatalog catalog,
        JiraTicketKeyParser ticketKeyParser,
        RunOutcomeClassifier outcomeClassifier,
        IOptions<DevUiOptions> options,
        TimeProvider timeProvider,
        ILogger<TicketOperationsService> logger)
    {
        _authoringClient = authoringClient ??
            throw new ArgumentNullException(nameof(authoringClient));
        _siteStore = siteStore ??
            throw new ArgumentNullException(nameof(siteStore));
        _publisher = publisher ??
            throw new ArgumentNullException(nameof(publisher));
        _catalog = catalog ??
            throw new ArgumentNullException(nameof(catalog));
        _ticketKeyParser = ticketKeyParser ??
            throw new ArgumentNullException(nameof(ticketKeyParser));
        _outcomeClassifier = outcomeClassifier ??
            throw new ArgumentNullException(nameof(outcomeClassifier));
        _options = options.Value;
        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ??
            throw new ArgumentNullException(nameof(logger));
    }

    public string GetSnapshotUrl(string workflow, string runId)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        TicketWorkflowDefinition definition = _catalog.Get(workflow);
        return $"{_options.OrchestratorAddress.TrimEnd('/')}/api/v1/processing-services/{Uri.EscapeDataString(definition.ProcessingServiceName)}/authoring/runs/{Uri.EscapeDataString(runId)}/snapshot";
    }

    public async Task<TicketRunDetails> OpenRunAsync(
        string workflow,
        string runId,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        TicketWorkflowDefinition definition =
            _catalog.Get(workflow);
        AuthoringRunResponse response =
            await _authoringClient.GetAsync(
                definition.ProcessingServiceName,
                runId,
                ct);
        ReviewSitePublication? publication = null;
        string? publicationError = null;
        PublicationReconciliationStatusResult?
            reconciliation = null;
        CanonicalEpochRecoveryStatusResult?
            canonicalEpochRecovery = null;
        if (string.Equals(
                response.Run.Purpose,
                PreparedTicketPublicationReconciliationContract.Purpose,
                StringComparison.Ordinal))
        {
            reconciliation = await _authoringClient
                .GetPublicationReconciliationAsync(
                    definition.ProcessingServiceName,
                    runId,
                    ct);
        }
        else if (string.Equals(
            response.Run.Purpose,
            PreparedTicketCanonicalEpochRecoveryContract.Purpose,
            StringComparison.Ordinal))
        {
            canonicalEpochRecovery = await _authoringClient
                .GetCanonicalEpochRecoveryAsync(
                    definition.ProcessingServiceName,
                    runId,
                    ct);
        }
        try
        {
            publication =
                await _siteStore.TryReconstructAsync(
                    definition.RouteKey,
                    runId,
                    ct);
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or
                UnauthorizedAccessException or JsonException or
                NotSupportedException or ArgumentException or
                OperationCanceledException)
        {
            publicationError = ex.Message;
            _logger.LogWarning(
                ex,
                "Could not reconstruct review-site publication for {Workflow}/{RunId}",
                definition.RouteKey,
                runId);
        }
        return new TicketRunDetails(
            definition,
            response,
            _outcomeClassifier.Classify(
                response.Run,
                publication,
                publicationError: publicationError),
            publication,
            publicationError,
            reconciliation,
            canonicalEpochRecovery);
    }

    public async Task<TicketStartResult> StartAsync(
        TicketRunStartRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        TicketWorkflowDefinition workflow =
            _catalog.Get(request.Workflow);
        JiraTicketKeyParseResult? parsedKeys = null;
        IReadOnlyList<string> ticketKeys = [];
        if (request.SelectionMode == TicketSelectionMode.Explicit)
        {
            parsedKeys = _ticketKeyParser.Parse(
                request.ExplicitTicketKeys);
            if (!parsedKeys.IsValid)
            {
                return new TicketStartResult(
                    TicketOperationDisposition.InvalidInput,
                    workflow,
                    ParsedKeys: parsedKeys,
                    Message:
                        "Explicit selection requires at least one valid Jira key and no invalid tokens.");
            }
            ticketKeys = parsedKeys.ValidKeys;
        }
        else if (request.SelectionMode !=
            TicketSelectionMode.Configured)
        {
            return new TicketStartResult(
                TicketOperationDisposition.InvalidInput,
                workflow,
                Message: "Unknown ticket selection mode.");
        }

        if (TryGetUnknownStartReview(
                workflow.RouteKey,
                out UnknownStartReview pendingReview))
        {
            return CreateUnknownStartResult(
                workflow,
                parsedKeys,
                pendingReview);
        }

        if (!await _mutationGate.WaitAsync(0, ct))
        {
            return new TicketStartResult(
                TicketOperationDisposition.Busy,
                workflow,
                ParsedKeys: parsedKeys,
                Message:
                    "Another mutation is already in progress in this UI circuit.");
        }

        try
        {
            ThrowIfDisposed();
            if (TryGetUnknownStartReview(
                    workflow.RouteKey,
                    out pendingReview))
            {
                return CreateUnknownStartResult(
                    workflow,
                    parsedKeys,
                    pendingReview);
            }

            DateTimeOffset submissionBegan =
                _timeProvider.GetUtcNow();
            try
            {
                AuthoringStartResult started =
                    await StartAuthoringAsync(
                        workflow,
                        ticketKeys,
                        request.DatabaseOnly,
                        ct);
                return started.NoCandidates
                    ? new TicketStartResult(
                        TicketOperationDisposition.NoCandidates,
                        workflow,
                        ParsedKeys: parsedKeys,
                        Message:
                            "The configured selection produced no authoring candidates.")
                    : new TicketStartResult(
                        TicketOperationDisposition.Succeeded,
                        workflow,
                        started.Run,
                        parsedKeys);
            }
            catch (AuthoringControlException ex)
                when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                return new TicketStartResult(
                    TicketOperationDisposition.Conflict,
                    workflow,
                    ParsedKeys: parsedKeys,
                    RelatedRunIds: ex.RelatedRunIds,
                    Message: FormatAuthoringControlFailure(ex),
                    FailureCode: ex.ErrorCode);
            }
            catch (AuthoringMutationOutcomeUnknownException ex)
            {
                UnknownStartReview review = new(
                    [],
                    new TicketReconciliation(
                        TicketReconciliationOutcome.Pending));
                SetUnknownStartReview(
                    workflow.RouteKey,
                    review);
                try
                {
                    review = await ReconcileUnknownStartAsync(
                        workflow,
                        submissionBegan,
                        ct);
                    SetUnknownStartReview(
                        workflow.RouteKey,
                        review);
                }
                catch (OperationCanceledException cancellation)
                    when (ct.IsCancellationRequested)
                {
                    SetUnknownStartReview(
                        workflow.RouteKey,
                        new UnknownStartReview(
                            [],
                            new TicketReconciliation(
                                TicketReconciliationOutcome.Failed,
                                cancellation.Message)));
                    throw;
                }
                _logger.LogWarning(
                    ex,
                    "Start outcome is unknown for {ProcessingService}; reconciliation is {ReconciliationOutcome} with {CandidateCount} candidates",
                    workflow.ProcessingServiceName,
                    review.Reconciliation.Outcome,
                    review.Candidates.Count);
                return CreateUnknownStartResult(
                    workflow,
                    parsedKeys,
                    review);
            }
            catch (AuthoringControlException ex)
            {
                return new TicketStartResult(
                    TicketOperationDisposition.Failed,
                    workflow,
                    ParsedKeys: parsedKeys,
                    RelatedRunIds: ex.RelatedRunIds,
                    Message: FormatAuthoringControlFailure(ex),
                    FailureCode: ex.ErrorCode);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public bool RequiresUnknownStartReview(string workflow)
    {
        TicketWorkflowDefinition definition =
            _catalog.Get(workflow);
        lock (_unknownStartsLock)
        {
            return _unknownStarts.ContainsKey(
                definition.RouteKey);
        }
    }

    public void AcknowledgeUnknownStartReview(string workflow)
    {
        TicketWorkflowDefinition definition =
            _catalog.Get(workflow);
        lock (_unknownStartsLock)
        {
            _unknownStarts.Remove(definition.RouteKey);
        }
    }

    public TicketPublicationRefreshResult?
        GetUnknownPublicationRefreshReview(
            string workflow,
            string sourceRunId)
    {
        TicketWorkflowDefinition definition =
            _catalog.Get(workflow);
        return TryGetUnknownPublicationRefreshReview(
            definition,
            sourceRunId,
            out TicketPublicationRefreshResult review)
            ? review
            : null;
    }

    public bool RequiresUnknownPublicationRefreshReview(
        string workflow,
        string sourceRunId)
    {
        TicketWorkflowDefinition definition =
            _catalog.Get(workflow);
        return TryGetUnknownPublicationRefreshReview(
            definition,
            sourceRunId,
            out _);
    }

    public void AcknowledgeUnknownPublicationRefreshReview(
        string workflow,
        string sourceRunId)
    {
        TicketWorkflowDefinition definition =
            _catalog.Get(workflow);
        lock (_unknownPublicationRefreshesLock)
        {
            _unknownPublicationRefreshes.Remove(
                new PublicationRefreshReviewKey(
                    definition.RouteKey,
                    sourceRunId));
        }
    }

    public Task<TicketItemMutationResult> RetryItemAsync(
        string workflow,
        string runId,
        string itemId,
        CancellationToken ct = default) =>
        MutateItemAsync(
            workflow,
            runId,
            itemId,
            supersessionReason: null,
            ct);

    public Task<TicketItemMutationResult> SupersedeItemAsync(
        string workflow,
        string runId,
        string itemId,
        string reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TicketWorkflowDefinition definition =
                _catalog.Get(workflow);
            return Task.FromResult(new TicketItemMutationResult(
                TicketOperationDisposition.InvalidInput,
                definition,
                runId,
                itemId,
                ReconciledRun: null,
                Message: "A supersession reason is required."));
        }
        return MutateItemAsync(
            workflow,
            runId,
            itemId,
            reason.Trim(),
            ct);
    }

    public async Task<TicketPublicationReconciliationResult>
        StartPublicationReconciliationAsync(
            string workflow,
            string sourceRunId,
            CancellationToken ct = default)
    {
        ThrowIfDisposed();
        TicketWorkflowDefinition definition = _catalog.Get(workflow);
        if (!IsPrepareWorkflow(definition))
        {
            return new(
                TicketOperationDisposition.NotAllowed,
                definition,
                sourceRunId,
                Message:
                    "Publication reconciliation is available only for the Prepare workflow.");
        }
        if (!await _mutationGate.WaitAsync(0, ct))
        {
            return new(
                TicketOperationDisposition.Busy,
                definition,
                sourceRunId,
                Message:
                    "Another mutation is already in progress in this UI circuit.");
        }

        try
        {
            PublicationReconciliationStartResult started =
                await _authoringClient.StartPublicationReconciliationAsync(
                    definition.ProcessingServiceName,
                    sourceRunId,
                    ct);
            PublicationReconciliationStatusResult status =
                await _authoringClient.GetPublicationReconciliationAsync(
                    definition.ProcessingServiceName,
                    started.Run.RunId,
                    ct);
            return new(
                TicketOperationDisposition.Succeeded,
                definition,
                started.Run.RunId,
                status,
                Message:
                    "Publication reconciliation started. Changed tickets will be re-authored while unchanged accepted output is carried forward.");
        }
        catch (AuthoringControlException ex)
            when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return new(
                TicketOperationDisposition.Conflict,
                definition,
                sourceRunId,
                RelatedRunIds: ex.RelatedRunIds,
                Message: FormatAuthoringControlFailure(ex),
                FailureCode: ex.ErrorCode);
        }
        catch (Exception ex) when (IsOperationFailure(ex))
        {
            return new(
                TicketOperationDisposition.Failed,
                definition,
                sourceRunId,
                Message: ex.Message);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public Task<TicketPublicationReconciliationResult>
        RetryPublicationReconciliationAsync(
            string workflow,
            string runId,
            CancellationToken ct = default) =>
        MutatePublicationReconciliationAsync(
            workflow,
            runId,
            reason: null,
            ReconciliationMutation.Retry,
            ct);

    public Task<TicketPublicationReconciliationResult>
        CancelPublicationReconciliationAsync(
            string workflow,
            string runId,
            string reason,
            CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TicketWorkflowDefinition definition = _catalog.Get(workflow);
            return Task.FromResult(new TicketPublicationReconciliationResult(
                TicketOperationDisposition.InvalidInput,
                definition,
                runId,
                Message: "A cancellation reason is required.",
                FailureCode:
                    PreparedTicketPublicationReconciliationFailureCodes
                        .CancellationNotAllowed));
        }
        return MutatePublicationReconciliationAsync(
            workflow,
            runId,
            reason.Trim(),
            ReconciliationMutation.Cancel,
            ct);
    }

    public Task<TicketPublicationReconciliationResult>
        AbandonPublicationReconciliationAsync(
            string workflow,
            string runId,
            string reason,
            CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TicketWorkflowDefinition definition = _catalog.Get(workflow);
            return Task.FromResult(new TicketPublicationReconciliationResult(
                TicketOperationDisposition.InvalidInput,
                definition,
                runId,
                Message: "An abandonment reason is required."));
        }
        return MutatePublicationReconciliationAsync(
            workflow,
            runId,
            reason.Trim(),
            ReconciliationMutation.Abandon,
            ct);
    }

    private async Task<TicketPublicationReconciliationResult>
        MutatePublicationReconciliationAsync(
            string workflow,
            string runId,
            string? reason,
            ReconciliationMutation mutation,
            CancellationToken ct)
    {
        ThrowIfDisposed();
        TicketWorkflowDefinition definition = _catalog.Get(workflow);
        if (!IsPrepareWorkflow(definition))
        {
            return new(
                TicketOperationDisposition.NotAllowed,
                definition,
                runId,
                Message:
                    "Publication reconciliation is available only for the Prepare workflow.");
        }
        if (!await _mutationGate.WaitAsync(0, ct))
        {
            return new(
                TicketOperationDisposition.Busy,
                definition,
                runId,
                Message:
                    "Another mutation is already in progress in this UI circuit.");
        }

        try
        {
            if (mutation == ReconciliationMutation.Cancel)
            {
                PublicationReconciliationStatusResult current =
                    await _authoringClient.GetPublicationReconciliationAsync(
                        definition.ProcessingServiceName,
                        runId,
                        ct);
                if (!string.Equals(
                        current.Promotion.State,
                        PreparedTicketPublicationReconciliationPromotionStateValues
                            .Staged,
                        StringComparison.Ordinal))
                {
                    return new(
                        TicketOperationDisposition.NotAllowed,
                        definition,
                        runId,
                        current,
                        Message:
                            "Cancellation is available only while reconciliation is staged before trusted or canonical promotion.",
                        FailureCode:
                            PreparedTicketPublicationReconciliationFailureCodes
                                .CancellationNotAllowed);
                }
            }

            PublicationReconciliationStatusResult status;
            if (mutation == ReconciliationMutation.Abandon)
            {
                status = (await _authoringClient
                    .AbandonPublicationReconciliationAsync(
                        definition.ProcessingServiceName,
                        runId,
                        reason!,
                        ct)).Status;
            }
            else if (mutation == ReconciliationMutation.Cancel)
            {
                status = (await _authoringClient
                    .CancelPublicationReconciliationAsync(
                        definition.ProcessingServiceName,
                        runId,
                        reason!,
                        ct)).Status;
            }
            else
            {
                status = (await _authoringClient
                    .RetryPublicationReconciliationAsync(
                        definition.ProcessingServiceName,
                        runId,
                        ct)).Status;
            }
            return new(
                TicketOperationDisposition.Succeeded,
                definition,
                runId,
                status,
                Message: mutation switch
                {
                    ReconciliationMutation.Cancel =>
                        "Staged reconciliation was cancelled before promotion.",
                    ReconciliationMutation.Abandon =>
                        "Reconciliation was abandoned without a replacement publication.",
                    _ => "Reconciliation recovery was retried.",
                });
        }
        catch (AuthoringControlException ex)
            when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return new(
                TicketOperationDisposition.Conflict,
                definition,
                runId,
                RelatedRunIds: ex.RelatedRunIds,
                Message: ex.Detail ?? ex.Message,
                FailureCode: ex.ErrorCode);
        }
        catch (Exception ex) when (IsOperationFailure(ex))
        {
            return new(
                TicketOperationDisposition.Failed,
                definition,
                runId,
                Message: ex.Message);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private static bool IsPrepareWorkflow(
        TicketWorkflowDefinition definition) =>
        definition.SiteKind == TicketSiteKind.Discussion &&
        string.Equals(
            definition.ProcessingServiceName,
            "Preparer",
            StringComparison.Ordinal);

    public async Task<TicketCanonicalEpochRecoveryResult>
        StartCanonicalEpochRecoveryAsync(
            string workflow,
            string sourceRunId,
            CancellationToken ct = default)
    {
        ThrowIfDisposed();
        TicketWorkflowDefinition definition = _catalog.Get(workflow);
        if (!IsPrepareWorkflow(definition))
        {
            return new(
                TicketOperationDisposition.NotAllowed,
                definition,
                sourceRunId,
                Message:
                    "Canonical-epoch recovery is available only for the Prepare workflow.");
        }
        if (!await _mutationGate.WaitAsync(0, ct))
        {
            return new(
                TicketOperationDisposition.Busy,
                definition,
                sourceRunId,
                Message:
                    "Another mutation is already in progress in this UI circuit.");
        }

        try
        {
            CanonicalEpochRecoveryStartResult started =
                await _authoringClient.StartCanonicalEpochRecoveryAsync(
                    definition.ProcessingServiceName,
                    sourceRunId,
                    ct);
            return new(
                TicketOperationDisposition.Succeeded,
                definition,
                started.Status.Run.RunId,
                started.Status,
                Message: started.ExistingRun
                    ? "The existing active or retryable canonical-epoch recovery was opened."
                    : "Canonical-epoch recovery started from the frozen current corpus and grouping.");
        }
        catch (AuthoringControlException ex)
            when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return new(
                TicketOperationDisposition.Conflict,
                definition,
                sourceRunId,
                RelatedRunIds: ex.RelatedRunIds,
                Message: ex.Detail ?? ex.Message,
                FailureCode: ex.ErrorCode);
        }
        catch (Exception ex) when (IsOperationFailure(ex))
        {
            return new(
                TicketOperationDisposition.Failed,
                definition,
                sourceRunId,
                Message: ex.Message);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<TicketCanonicalEpochRecoveryResult>
        RetryCanonicalEpochRecoveryAsync(
            string workflow,
            string runId,
            CancellationToken ct = default)
    {
        ThrowIfDisposed();
        TicketWorkflowDefinition definition = _catalog.Get(workflow);
        if (!IsPrepareWorkflow(definition))
        {
            return new(
                TicketOperationDisposition.NotAllowed,
                definition,
                runId,
                Message:
                    "Canonical-epoch recovery is available only for the Prepare workflow.");
        }
        if (!await _mutationGate.WaitAsync(0, ct))
        {
            return new(
                TicketOperationDisposition.Busy,
                definition,
                runId,
                Message:
                    "Another mutation is already in progress in this UI circuit.");
        }

        try
        {
            CanonicalEpochRecoveryRetryResult retried =
                await _authoringClient.RetryCanonicalEpochRecoveryAsync(
                    definition.ProcessingServiceName,
                    runId,
                    ct);
            return new(
                TicketOperationDisposition.Succeeded,
                definition,
                runId,
                retried.Status,
                Message: "Canonical-epoch recovery resumed from its durable journal.");
        }
        catch (AuthoringControlException ex)
            when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return new(
                TicketOperationDisposition.Conflict,
                definition,
                runId,
                RelatedRunIds: ex.RelatedRunIds,
                Message: ex.Detail ?? ex.Message,
                FailureCode: ex.ErrorCode);
        }
        catch (Exception ex) when (IsOperationFailure(ex))
        {
            return new(
                TicketOperationDisposition.Failed,
                definition,
                runId,
                Message: ex.Message);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private enum ReconciliationMutation
    {
        Retry,
        Cancel,
        Abandon,
    }

    private static bool IsOperationFailure(Exception ex) =>
        ex is AuthoringControlException or
            AuthoringMutationOutcomeUnknownException or
            HttpRequestException or IOException or
            InvalidOperationException or JsonException or
            NotSupportedException or ArgumentException or
            TimeoutException or OperationCanceledException;

    private static string FormatAuthoringControlFailure(
        AuthoringControlException exception)
    {
        string message = exception.Detail ?? exception.Message;
        return string.Equals(
            exception.ErrorCode,
            PreparedTicketPublicationReconciliationFailureCodes
                .CanonicalUnpublishedRestriction,
            StringComparison.Ordinal)
            ? $"{exception.ErrorCode}: {message}"
            : message;
    }

    public async Task<TicketPublicationRefreshResult>
        RefreshPublicationAsync(
            string workflow,
            string sourceRunId,
            CancellationToken ct = default)
    {
        ThrowIfDisposed();
        TicketWorkflowDefinition definition =
            _catalog.Get(workflow);
        if (TryGetUnknownPublicationRefreshReview(
                definition,
                sourceRunId,
                out TicketPublicationRefreshResult pendingReview))
        {
            return pendingReview;
        }

        if (!await _mutationGate.WaitAsync(0, ct))
        {
            return new TicketPublicationRefreshResult(
                TicketOperationDisposition.Busy,
                definition,
                sourceRunId,
                Message:
                    "Another mutation is already in progress in this UI circuit.");
        }

        try
        {
            ThrowIfDisposed();
            if (TryGetUnknownPublicationRefreshReview(
                    definition,
                    sourceRunId,
                    out pendingReview))
            {
                return pendingReview;
            }

            if (definition.SiteKind != TicketSiteKind.Discussion ||
                !string.Equals(
                    definition.ProcessingServiceName,
                    "Preparer",
                    StringComparison.Ordinal))
            {
                return new TicketPublicationRefreshResult(
                    TicketOperationDisposition.NotAllowed,
                    definition,
                    sourceRunId,
                    Message:
                        "Publication refresh is available only for the Prepare workflow.");
            }

            ReviewSitePublication? publication =
                await _siteStore.TryReconstructAsync(
                    definition.RouteKey,
                    sourceRunId,
                    ct);
            if (publication is null)
            {
                return new TicketPublicationRefreshResult(
                    TicketOperationDisposition.NotAllowed,
                    definition,
                    sourceRunId,
                    Message:
                        "No existing Discussion publication was found. Generate the first review site from this run instead.");
            }

            AuthoringRunResponse latest =
                await _authoringClient.GetAsync(
                    definition.ProcessingServiceName,
                    sourceRunId,
                    ct);
            if (string.Equals(
                    latest.Run.Purpose,
                    PreparedTicketPublicationContract
                        .PublicationRefreshPurpose,
                    StringComparison.Ordinal))
            {
                return new TicketPublicationRefreshResult(
                    TicketOperationDisposition.NotAllowed,
                    definition,
                    sourceRunId,
                    Message:
                        "A publication-refresh run cannot be used as another refresh source.");
            }

            ProcessorRunOutcome processorOutcome =
                _outcomeClassifier.ClassifyProcessor(latest.Run);
            string? ineligibleReason = processorOutcome switch
            {
                ProcessorRunOutcome.Active or
                ProcessorRunOutcome.RecoverableError =>
                    "Publication refresh requires a terminal source run.",
                ProcessorRunOutcome.CompletedDatabaseOnly =>
                    "A database-only run has no immutable snapshot to refresh.",
                ProcessorRunOutcome.Superseded =>
                    "A superseded run cannot be used as a publication-refresh source.",
                ProcessorRunOutcome.Abandoned =>
                    "An abandoned run cannot be used as a publication-refresh source.",
                ProcessorRunOutcome.Completed or
                ProcessorRunOutcome.CompletedWithSupersededItems
                    when !latest.Run.DatabaseOnly => null,
                _ =>
                    "Publication refresh requires a completed snapshot-producing source run.",
            };
            if (ineligibleReason is not null)
            {
                return new TicketPublicationRefreshResult(
                    TicketOperationDisposition.NotAllowed,
                    definition,
                    sourceRunId,
                    Message: ineligibleReason);
            }

            DateTimeOffset submissionBegan =
                _timeProvider.GetUtcNow();
            try
            {
                AuthoringRunResponse refreshRun =
                    await _authoringClient
                        .StartPublicationRefreshAsync(
                            definition.ProcessingServiceName,
                            sourceRunId,
                            ct);
                return new TicketPublicationRefreshResult(
                    TicketOperationDisposition.Succeeded,
                    definition,
                    sourceRunId,
                    refreshRun,
                    Message:
                        "Publication refresh started. Follow the returned run before publishing its verified snapshot pair.");
            }
            catch (AuthoringControlException ex)
                when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                return new TicketPublicationRefreshResult(
                    TicketOperationDisposition.Conflict,
                    definition,
                    sourceRunId,
                    RelatedRunIds: ex.RelatedRunIds,
                    Message: FormatAuthoringControlFailure(ex),
                    FailureCode: ex.ErrorCode);
            }
            catch (AuthoringMutationOutcomeUnknownException ex)
            {
                TicketPublicationRefreshResult reconciliation =
                    new(
                        TicketOperationDisposition.OutcomeUnknown,
                        definition,
                        sourceRunId,
                        Message:
                            "Refresh outcome unknown. Read reconciliation is still pending; explicit operator review is required before trying again.",
                        Reconciliation: new TicketReconciliation(
                            TicketReconciliationOutcome.Pending));
                SetUnknownPublicationRefreshReview(
                    definition,
                    sourceRunId,
                    reconciliation);
                try
                {
                    reconciliation =
                        await ReconcilePublicationRefreshAsync(
                            definition,
                            sourceRunId,
                            submissionBegan,
                            ct);
                    SetUnknownPublicationRefreshReview(
                        definition,
                        sourceRunId,
                        reconciliation);
                }
                catch (OperationCanceledException cancellation)
                    when (ct.IsCancellationRequested)
                {
                    SetUnknownPublicationRefreshReview(
                        definition,
                        sourceRunId,
                        new TicketPublicationRefreshResult(
                            TicketOperationDisposition.OutcomeUnknown,
                            definition,
                            sourceRunId,
                            Message:
                                "Refresh outcome unknown. The mutation was not replayed, read reconciliation was canceled, and explicit operator review is required before trying again.",
                            Reconciliation:
                                new TicketReconciliation(
                                    TicketReconciliationOutcome.Failed,
                                    cancellation.Message)));
                    throw;
                }
                _logger.LogWarning(
                    ex,
                    "Publication refresh outcome is unknown for {ProcessingService}/{SourceRunId}; the mutation was not replayed and reconciliation found {CandidateCount} candidates",
                    definition.ProcessingServiceName,
                    sourceRunId,
                    reconciliation.Candidates.Count);
                return reconciliation;
            }
            catch (AuthoringControlException ex)
            {
                return new TicketPublicationRefreshResult(
                    TicketOperationDisposition.Failed,
                    definition,
                    sourceRunId,
                    RelatedRunIds: ex.RelatedRunIds,
                    Message: FormatAuthoringControlFailure(ex),
                    FailureCode: ex.ErrorCode);
            }
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is AuthoringControlException or
                HttpRequestException or IOException or
                InvalidOperationException or
                UnauthorizedAccessException or JsonException or
                NotSupportedException or ArgumentException or
                TimeoutException or OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Publication refresh could not be started for {Workflow}/{SourceRunId}",
                definition.RouteKey,
                sourceRunId);
            return new TicketPublicationRefreshResult(
                TicketOperationDisposition.Failed,
                definition,
                sourceRunId,
                Message: ex.Message);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task<TicketPublicationResult> PublishAsync(
        string workflow,
        string runId,
        bool force = false,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        TicketWorkflowDefinition definition =
            _catalog.Get(workflow);
        if (!await _mutationGate.WaitAsync(0, ct))
        {
            return new TicketPublicationResult(
                TicketOperationDisposition.Busy,
                definition,
                runId,
                Publication: null,
                VerifiedPairAvailable: false,
                Message:
                    "Another mutation is already in progress in this UI circuit.");
        }

        VerifiedAuthoringSnapshotPair? pair = null;
        try
        {
            AuthoringRunResponse latest =
                await _authoringClient.GetAsync(
                    definition.ProcessingServiceName,
                    runId,
                    ct);
            ProcessorRunOutcome processorOutcome =
                _outcomeClassifier.ClassifyProcessor(
                    latest.Run);
            if (processorOutcome is not (
                    ProcessorRunOutcome.Completed or
                    ProcessorRunOutcome
                        .CompletedWithSupersededItems) ||
                latest.Run.DatabaseOnly)
            {
                return new TicketPublicationResult(
                    TicketOperationDisposition.NotAllowed,
                    definition,
                    runId,
                    Publication: null,
                    VerifiedPairAvailable: false,
                    Message:
                        "Publication is available only for a completed snapshot-producing run.");
            }

            ReviewSiteCoordinates coordinates =
                _siteStore.GetCoordinates(
                    definition.RouteKey,
                    runId);
            _siteStore.RevalidateForPublication(coordinates);
            pair = await _siteStore.TryOpenVerifiedPairAsync(
                coordinates,
                ct);
            if (pair is null)
            {
                pair =
                    await _authoringClient.DownloadSnapshotPairAsync(
                        definition.ProcessingServiceName,
                        runId,
                        coordinates.SnapshotPairDirectory,
                        ct);
            }

            _siteStore.RevalidateForPublication(coordinates);
            if (!string.Equals(
                    pair.ServiceName,
                    definition.ProcessingServiceName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    pair.RunId,
                    runId,
                    StringComparison.Ordinal) ||
                !DevUiPathGuard.PathsEqual(
                    pair.DirectoryPath,
                    coordinates.SnapshotPairDirectory))
            {
                throw new InvalidOperationException(
                    "Downloaded snapshot pair does not match the workflow's approved run-scoped cache directory.");
            }
            TicketSitePublishResult result =
                await _publisher.PublishAsync(
                    new TicketSitePublishRequest(
                        pair,
                        definition.SiteKind,
                        coordinates.SiteRoot,
                        definition.SiteTitle,
                        Filters: TicketSiteFilters.None,
                        Force: force),
                    ct);
            ReviewSitePublication publication =
                await _siteStore.ValidatePublishedSiteAsync(
                    coordinates,
                    pair,
                    ct);
            return new TicketPublicationResult(
                TicketOperationDisposition.Succeeded,
                definition,
                runId,
                publication,
                VerifiedPairAvailable: true,
                Warnings: result.Warnings);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is TicketSitePublishException or
                AuthoringControlException or
                HttpRequestException or IOException or
                UnauthorizedAccessException or
                InvalidOperationException or ArgumentException or
                NotSupportedException)
        {
            _logger.LogWarning(
                ex,
                "Review-site publication failed for {Workflow}/{RunId}",
                definition.RouteKey,
                runId);
            return new TicketPublicationResult(
                TicketOperationDisposition.Failed,
                definition,
                runId,
                Publication: null,
                VerifiedPairAvailable: pair is not null,
                Message: ex.Message);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task<TicketPublicationRefreshResult>
        ReconcilePublicationRefreshAsync(
            TicketWorkflowDefinition workflow,
            string sourceRunId,
            DateTimeOffset submissionBegan,
            CancellationToken ct)
    {
        try
        {
            AuthoringRunListResponse list =
                await _authoringClient.ListAsync(
                    workflow.ProcessingServiceName,
                    _options.RecentRunLimit,
                    ct);
            IReadOnlyList<AuthoringRunStatus> candidates =
                Array.AsReadOnly(
                    list.Runs
                        .Where(run =>
                            run.CreatedAt >= submissionBegan &&
                            string.Equals(
                                run.Purpose,
                                PreparedTicketPublicationContract
                                    .PublicationRefreshPurpose,
                                StringComparison.Ordinal) &&
                            string.Equals(
                                run.SourceRunId,
                                sourceRunId,
                                StringComparison.Ordinal))
                        .ToArray());
            string message = candidates.Count switch
            {
                0 =>
                    "Refresh outcome unknown. The mutation was not replayed, and no matching refresh run was found in the bounded recent-run read. Operator review is required before trying again.",
                1 =>
                    "Refresh outcome unknown. The mutation was not replayed; review the single matching refresh run before continuing.",
                _ =>
                    "Refresh outcome unknown. The mutation was not replayed, and multiple matching refresh runs require operator review.",
            };
            if (list.Truncated)
            {
                message +=
                    " The recent-run response was truncated.";
            }
            return new TicketPublicationRefreshResult(
                TicketOperationDisposition.OutcomeUnknown,
                workflow,
                sourceRunId,
                InspectionCandidates: candidates,
                Message: message,
                Reconciliation: new TicketReconciliation(
                    TicketReconciliationOutcome.Succeeded));
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            IsReadReconciliationFailure(ex))
        {
            _logger.LogWarning(
                ex,
                "Could not reconcile publication refresh for {ProcessingService}/{SourceRunId}",
                workflow.ProcessingServiceName,
                sourceRunId);
            return new TicketPublicationRefreshResult(
                TicketOperationDisposition.OutcomeUnknown,
                workflow,
                sourceRunId,
                Message:
                    "Refresh outcome unknown. The mutation was not replayed, read reconciliation failed, and operator review is required before trying again.",
                Reconciliation: new TicketReconciliation(
                    TicketReconciliationOutcome.Failed,
                    ex.Message));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _mutationGate.Dispose();
    }

    private async Task<TicketItemMutationResult> MutateItemAsync(
        string workflow,
        string runId,
        string itemId,
        string? supersessionReason,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        TicketWorkflowDefinition definition =
            _catalog.Get(workflow);
        if (!await _mutationGate.WaitAsync(0, ct))
        {
            return new TicketItemMutationResult(
                TicketOperationDisposition.Busy,
                definition,
                runId,
                itemId,
                ReconciledRun: null,
                Message:
                    "Another mutation is already in progress in this UI circuit.");
        }

        try
        {
            AuthoringRunResponse latest =
                await _authoringClient.GetAsync(
                    definition.ProcessingServiceName,
                    runId,
                    ct);
            AuthoringRunItemStatus? item =
                latest.Items.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.ItemId,
                        itemId,
                        StringComparison.Ordinal));
            bool allowed = supersessionReason is null
                ? item?.AllowedActions?.CanRetryNow == true
                : !string.Equals(
                    latest.Run.Purpose,
                    PreparedTicketPublicationReconciliationContract.Purpose,
                    StringComparison.Ordinal) &&
                  item?.AllowedActions?.CanSupersede == true;
            if (!allowed)
            {
                return new TicketItemMutationResult(
                    TicketOperationDisposition.NotAllowed,
                    definition,
                    runId,
                    itemId,
                    latest,
                    item is null
                        ? "The item is not present in the latest run status."
                        : supersessionReason is not null &&
                          string.Equals(
                              latest.Run.Purpose,
                              PreparedTicketPublicationReconciliationContract
                                  .Purpose,
                              StringComparison.Ordinal)
                            ? "Publication reconciliation items cannot be superseded; cancel the staged reconciliation instead."
                        : "The processor does not currently allow this action.");
            }

            try
            {
                if (supersessionReason is null)
                {
                    await _authoringClient.RetryAsync(
                        definition.ProcessingServiceName,
                        runId,
                        itemId,
                        ct);
                }
                else
                {
                    await _authoringClient.SupersedeAsync(
                        definition.ProcessingServiceName,
                        runId,
                        itemId,
                        supersessionReason,
                        ct);
                }

                AuthoringRunResponse refreshed =
                    await _authoringClient.GetAsync(
                        definition.ProcessingServiceName,
                        runId,
                        ct);
                return new TicketItemMutationResult(
                    TicketOperationDisposition.Succeeded,
                    definition,
                    runId,
                    itemId,
                    refreshed);
            }
            catch (AuthoringControlException ex)
                when (ex.StatusCode == HttpStatusCode.Conflict)
            {
                RunReconciliation reconciliation =
                    await ReconcileRunAsync(
                        definition,
                        runId,
                        ct);
                return new TicketItemMutationResult(
                    TicketOperationDisposition.Conflict,
                    definition,
                    runId,
                    itemId,
                    reconciliation.Run,
                    AppendReconciliationFailure(
                        ex.Detail ?? ex.Message,
                        reconciliation.Status),
                    reconciliation.Status);
            }
            catch (AuthoringMutationOutcomeUnknownException ex)
            {
                RunReconciliation reconciliation =
                    await ReconcileRunAsync(
                        definition,
                        runId,
                        ct);
                _logger.LogWarning(
                    ex,
                    "Authoring item mutation outcome is unknown for {ProcessingService}/{RunId}/{ItemId}; the mutation was not replayed",
                    definition.ProcessingServiceName,
                    runId,
                    itemId);
                return new TicketItemMutationResult(
                    TicketOperationDisposition.OutcomeUnknown,
                    definition,
                    runId,
                    itemId,
                    reconciliation.Run,
                    reconciliation.Status.Outcome ==
                        TicketReconciliationOutcome.Succeeded
                        ? "Outcome unknown. The mutation was not replayed; the displayed run was refreshed with a read."
                        : "Outcome unknown. The mutation was not replayed, and read reconciliation failed.",
                    reconciliation.Status);
            }
            catch (AuthoringControlException ex)
            {
                return new TicketItemMutationResult(
                    TicketOperationDisposition.Failed,
                    definition,
                    runId,
                    itemId,
                    latest,
                    ex.Detail ?? ex.Message);
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private Task<AuthoringStartResult> StartAuthoringAsync(
        TicketWorkflowDefinition workflow,
        IReadOnlyList<string> ticketKeys,
        bool databaseOnly,
        CancellationToken ct) =>
        workflow.ProcessingServiceName switch
        {
            "Preparer" => _authoringClient.StartAsync(
                workflow.ProcessingServiceName,
                new PreparedTicketAuthoringRunRequest(
                    ticketKeys,
                    databaseOnly),
                ct),
            "Planner" => _authoringClient.StartAsync(
                workflow.ProcessingServiceName,
                new PlannedTicketAuthoringRunRequest(
                    ticketKeys,
                    databaseOnly),
                ct),
            _ => throw new InvalidOperationException(
                $"Unsupported ticket processing service '{workflow.ProcessingServiceName}'."),
        };

    private async Task<UnknownStartReview>
        ReconcileUnknownStartAsync(
            TicketWorkflowDefinition workflow,
            DateTimeOffset submissionBegan,
            CancellationToken ct)
    {
        try
        {
            AuthoringRunListResponse list =
                await _authoringClient.ListAsync(
                    workflow.ProcessingServiceName,
                    _options.RecentRunLimit,
                    ct);
            IReadOnlyList<AuthoringRunStatus> candidates =
                Array.AsReadOnly(
                    list.Runs
                        .Where(run =>
                            run.CreatedAt >= submissionBegan)
                        .ToArray());
            return new UnknownStartReview(
                candidates,
                new TicketReconciliation(
                    TicketReconciliationOutcome.Succeeded));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
            when (!ct.IsCancellationRequested)
        {
            return FailedUnknownStartReconciliation(
                workflow,
                ex);
        }
        catch (Exception ex) when (
            IsReadReconciliationFailure(ex))
        {
            return FailedUnknownStartReconciliation(
                workflow,
                ex);
        }
    }

    private async Task<RunReconciliation> ReconcileRunAsync(
        TicketWorkflowDefinition workflow,
        string runId,
        CancellationToken ct)
    {
        try
        {
            AuthoringRunResponse run =
                await _authoringClient.GetAsync(
                    workflow.ProcessingServiceName,
                    runId,
                    ct);
            return new RunReconciliation(
                run,
                new TicketReconciliation(
                    TicketReconciliationOutcome.Succeeded));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
            when (!ct.IsCancellationRequested)
        {
            return FailedRunReconciliation(
                workflow,
                runId,
                ex);
        }
        catch (Exception ex) when (
            IsReadReconciliationFailure(ex))
        {
            return FailedRunReconciliation(
                workflow,
                runId,
                ex);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private bool TryGetUnknownStartReview(
        string workflow,
        out UnknownStartReview review)
    {
        lock (_unknownStartsLock)
        {
            return _unknownStarts.TryGetValue(
                workflow,
                out review!);
        }
    }

    private void SetUnknownStartReview(
        string workflow,
        UnknownStartReview review)
    {
        lock (_unknownStartsLock)
        {
            _unknownStarts[workflow] = review;
        }
    }

    private bool TryGetUnknownPublicationRefreshReview(
        TicketWorkflowDefinition workflow,
        string sourceRunId,
        out TicketPublicationRefreshResult review)
    {
        lock (_unknownPublicationRefreshesLock)
        {
            return _unknownPublicationRefreshes.TryGetValue(
                new PublicationRefreshReviewKey(
                    workflow.RouteKey,
                    sourceRunId),
                out review!);
        }
    }

    private void SetUnknownPublicationRefreshReview(
        TicketWorkflowDefinition workflow,
        string sourceRunId,
        TicketPublicationRefreshResult review)
    {
        lock (_unknownPublicationRefreshesLock)
        {
            _unknownPublicationRefreshes[
                new PublicationRefreshReviewKey(
                    workflow.RouteKey,
                    sourceRunId)] = review;
        }
    }

    private TicketStartResult CreateUnknownStartResult(
        TicketWorkflowDefinition workflow,
        JiraTicketKeyParseResult? parsedKeys,
        UnknownStartReview review)
    {
        string message = review.Reconciliation.Outcome switch
        {
            TicketReconciliationOutcome.Pending =>
                "Start outcome unknown. Read reconciliation is still pending; explicitly review the workflow before starting again.",
            TicketReconciliationOutcome.Failed =>
                "Start outcome unknown. The request was not replayed, read reconciliation failed, and explicit operator review is required before starting again.",
            _ =>
                "Start outcome unknown. The request was not replayed; inspect runs created since submission began and explicitly acknowledge the result before starting again.",
        };
        return new TicketStartResult(
            TicketOperationDisposition.OutcomeUnknown,
            workflow,
            ParsedKeys: parsedKeys,
            InspectionCandidates: review.Candidates,
            Message: message,
            Reconciliation: review.Reconciliation);
    }

    private UnknownStartReview FailedUnknownStartReconciliation(
        TicketWorkflowDefinition workflow,
        Exception error)
    {
        _logger.LogWarning(
            error,
            "Could not reconcile unknown start outcome for {ProcessingService}",
            workflow.ProcessingServiceName);
        return new UnknownStartReview(
            [],
            new TicketReconciliation(
                TicketReconciliationOutcome.Failed,
                error.Message));
    }

    private RunReconciliation FailedRunReconciliation(
        TicketWorkflowDefinition workflow,
        string runId,
        Exception error)
    {
        _logger.LogWarning(
            error,
            "Could not reconcile authoring run {ProcessingService}/{RunId}",
            workflow.ProcessingServiceName,
            runId);
        return new RunReconciliation(
            Run: null,
            new TicketReconciliation(
                TicketReconciliationOutcome.Failed,
                error.Message));
    }

    private static string AppendReconciliationFailure(
        string message,
        TicketReconciliation reconciliation) =>
        reconciliation.Outcome ==
            TicketReconciliationOutcome.Failed
            ? $"{message} Read reconciliation failed: {reconciliation.Error}"
            : message;

    private static bool IsReadReconciliationFailure(
        Exception error) =>
        error is HttpRequestException or IOException or
            InvalidOperationException or TimeoutException or
            OperationCanceledException;

    private sealed record UnknownStartReview(
        IReadOnlyList<AuthoringRunStatus> Candidates,
        TicketReconciliation Reconciliation);

    private readonly record struct PublicationRefreshReviewKey(
        string Workflow,
        string SourceRunId);

    private sealed record RunReconciliation(
        AuthoringRunResponse? Run,
        TicketReconciliation Status);
}
