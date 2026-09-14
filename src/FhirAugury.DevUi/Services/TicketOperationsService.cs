using System.Net;
using System.Text.Json;
using FhirAugury.DevUi.Configuration;
using FhirAugury.DevUi.Models;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
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
            publicationError);
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
                    Message: ex.Detail ?? ex.Message);
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
                    Message: ex.Detail ?? ex.Message);
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
                : item?.AllowedActions?.CanSupersede == true;
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

    private sealed record RunReconciliation(
        AuthoringRunResponse? Run,
        TicketReconciliation Status);
}
