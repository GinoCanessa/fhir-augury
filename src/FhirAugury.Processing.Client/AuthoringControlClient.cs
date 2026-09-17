using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Processing.Contracts;

namespace FhirAugury.Processing.Client;

public sealed record AuthoringStartResult(
    AuthoringRunResponse? Run)
{
    public bool NoCandidates => Run is null;
}

public sealed record AuthoringRetryResponse(
    string ItemId,
    bool RequiresAuthoring);

public sealed record PublicationReconciliationItemDecision(
    string TicketKey,
    string Disposition,
    string BaselineSourceRevision,
    string CurrentSourceRevision,
    string BaselineReceiptId,
    string BaselineRunItemId,
    string BaselineContributingRunId,
    string BaselineAuthoredFingerprint,
    string BaselineGroupingFingerprint,
    string? ItemKind = null,
    string? ExpectedSourceRevision = null);

public sealed record PublicationReconciliationComparison(
    int ContractVersion,
    string SourceRunId,
    string SourceSnapshotId,
    string SourceSnapshotSha256,
    string StableJiraGeneration,
    DateTimeOffset CapturedAt,
    string CorpusFingerprint,
    IReadOnlyList<PublicationReconciliationItemDecision> Items);

public sealed record PublicationReconciliationGroupingImpact(
    string PartitionKey,
    IReadOnlyList<string> RevisedTicketKeys,
    string BaselineCorpusFingerprint,
    string BaselineOutputFingerprint,
    string BaselineProtectedRowsFingerprint,
    string? StagedCorpusFingerprint = null,
    string? StagedOutputFingerprint = null,
    string? StagedProtectedRowsFingerprint = null,
    bool Complete = false);

public sealed record PublicationReconciliationPromotionStatus(
    string State,
    string? JournalState,
    bool MutationFenceHeld,
    DateTimeOffset? LastRecoveryAttemptAt = null,
    string? FailureCode = null,
    string? FailureDetail = null,
    DateTimeOffset? AbandonedAt = null,
    string? AbandonmentReason = null,
    DateTimeOffset? CancelledAt = null,
    string? CancellationReason = null);

public sealed record PublicationReconciliationProof(
    int ContractVersion,
    string Purpose,
    string SourceRunId,
    string SourceSnapshotId,
    string StableJiraGeneration,
    int AcceptedTicketCount,
    int CarryForwardTicketCount,
    int ReAuthorTicketCount,
    string CorpusFingerprint,
    string GroupingImpactFingerprint,
    DateTimeOffset CapturedAt,
    // Nullable only for audit-only v1/v2 JSON and constructor compatibility.
    string? GroupingFingerprint = null);

public sealed record PublicationReconciliationStartResult(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items,
    PublicationReconciliationComparison Comparison,
    AuthoringRunReconciliationCounts Counts);

public sealed record PublicationReconciliationStatusResult(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items,
    PublicationReconciliationComparison Comparison,
    AuthoringRunReconciliationCounts Counts,
    IReadOnlyList<PublicationReconciliationGroupingImpact> GroupingImpacts,
    PublicationReconciliationPromotionStatus Promotion,
    IReadOnlyList<string> InvalidatedTicketKeys,
    PublicationReconciliationProof? PublicationProof = null,
    string? FailureCode = null,
    string? FailureDetail = null,
    CanonicalEpochRecoveryLink? CanonicalEpochRecovery = null);

public sealed record PublicationReconciliationRetryResult(
    PublicationReconciliationStatusResult Status,
    bool RecoveryStarted);

public sealed record PublicationReconciliationCancelResult(
    PublicationReconciliationStatusResult Status,
    DateTimeOffset CancelledAt,
    string Reason);

public sealed record PublicationReconciliationAbandonResult(
    PublicationReconciliationStatusResult Status,
    DateTimeOffset AbandonedAt,
    string Reason);

public sealed record CanonicalEpochRecoverySourceAbandonment(
    string RunId,
    long AuthoringEpoch,
    string PromotionState,
    DateTimeOffset AbandonedAt,
    string Reason);

public sealed record CanonicalEpochRecoveryFrozenState(
    int AcceptedTicketCount,
    int GroupingPartitionCount,
    string CorpusFingerprint,
    string GroupingFingerprint,
    string RecipeFingerprint,
    DateTimeOffset CapturedAt);

public sealed record CanonicalEpochRecoveryProof(
    int ContractVersion,
    string Purpose,
    string RunId,
    string SourceRunId,
    long AuthoringEpoch,
    DateTimeOffset AbandonedAt,
    string CorpusFingerprint,
    string GroupingFingerprint,
    string RecipeFingerprint,
    DateTimeOffset CapturedAt);

public sealed record CanonicalEpochRecoveryLink(
    string RunId,
    string SourceRunId,
    long AuthoringEpoch,
    string State,
    string? SnapshotId = null,
    string? SnapshotSha256 = null,
    DateTimeOffset? ResolvedAt = null);

public sealed record CanonicalEpochRecoveryLifecycleStatus(
    string State,
    bool MutationFenceHeld,
    DateTimeOffset? LastRecoveryAttemptAt = null,
    string? FailureCode = null,
    string? FailureDetail = null);

public sealed record CanonicalEpochRecoveryStatusResult(
    AuthoringRunStatus Run,
    IReadOnlyList<AuthoringRunItemStatus> Items,
    CanonicalEpochRecoverySourceAbandonment SourceAbandonment,
    CanonicalEpochRecoveryFrozenState Frozen,
    CanonicalEpochRecoveryLifecycleStatus Recovery,
    CanonicalEpochRecoveryProof? PublicationProof = null,
    AuthoringSnapshotDescriptor? Snapshot = null);

public sealed record CanonicalEpochRecoveryStartResult(
    CanonicalEpochRecoveryStatusResult Status,
    bool ExistingRun);

public sealed record CanonicalEpochRecoveryRetryResult(
    CanonicalEpochRecoveryStatusResult Status,
    bool RecoveryStarted);

public interface IAuthoringControlClient
{
    Task<AuthoringStartResult> StartAsync<TRequest>(
        string serviceName,
        TRequest request,
        CancellationToken ct);

    Task<AuthoringRunResponse> StartPublicationRefreshAsync(
        string serviceName,
        string sourceRunId,
        CancellationToken ct);

    Task<PublicationReconciliationStartResult>
        StartPublicationReconciliationAsync(
            string serviceName,
            string sourceRunId,
            CancellationToken ct) =>
        Task.FromException<PublicationReconciliationStartResult>(
            new NotSupportedException(
                "Publication reconciliation start is not supported by this authoring client."));

    Task<PublicationReconciliationStatusResult>
        GetPublicationReconciliationAsync(
            string serviceName,
            string runId,
            CancellationToken ct) =>
        Task.FromException<PublicationReconciliationStatusResult>(
            new NotSupportedException(
                "Publication reconciliation status is not supported by this authoring client."));

    Task<PublicationReconciliationRetryResult>
        RetryPublicationReconciliationAsync(
            string serviceName,
            string runId,
            CancellationToken ct) =>
        Task.FromException<PublicationReconciliationRetryResult>(
            new NotSupportedException(
                "Publication reconciliation recovery is not supported by this authoring client."));

    Task<PublicationReconciliationCancelResult>
        CancelPublicationReconciliationAsync(
            string serviceName,
            string runId,
            string reason,
            CancellationToken ct) =>
        Task.FromException<PublicationReconciliationCancelResult>(
            new NotSupportedException(
                "Publication reconciliation cancellation is not supported by this authoring client."));

    Task<PublicationReconciliationAbandonResult>
        AbandonPublicationReconciliationAsync(
            string serviceName,
            string runId,
            string reason,
            CancellationToken ct) =>
        Task.FromException<PublicationReconciliationAbandonResult>(
            new NotSupportedException(
                "Publication reconciliation abandonment is not supported by this authoring client."));

    Task<CanonicalEpochRecoveryStartResult>
        StartCanonicalEpochRecoveryAsync(
            string serviceName,
            string sourceRunId,
            CancellationToken ct) =>
        Task.FromException<CanonicalEpochRecoveryStartResult>(
            new NotSupportedException(
                "Canonical-epoch recovery start is not supported by this authoring client."));

    Task<CanonicalEpochRecoveryStatusResult>
        GetCanonicalEpochRecoveryAsync(
            string serviceName,
            string runId,
            CancellationToken ct) =>
        Task.FromException<CanonicalEpochRecoveryStatusResult>(
            new NotSupportedException(
                "Canonical-epoch recovery status is not supported by this authoring client."));

    Task<CanonicalEpochRecoveryRetryResult>
        RetryCanonicalEpochRecoveryAsync(
            string serviceName,
            string runId,
            CancellationToken ct) =>
        Task.FromException<CanonicalEpochRecoveryRetryResult>(
            new NotSupportedException(
                "Canonical-epoch recovery retry is not supported by this authoring client."));

    Task<AuthoringRunListResponse> ListAsync(
        string serviceName,
        int? limit,
        CancellationToken ct);

    Task<AuthoringRunResponse> GetAsync(
        string serviceName,
        string runId,
        CancellationToken ct);

    Task<AuthoringRetryResponse> RetryAsync(
        string serviceName,
        string runId,
        string itemId,
        CancellationToken ct);

    Task<AuthoringItemSupersedeResult> SupersedeAsync(
        string serviceName,
        string runId,
        string itemId,
        string reason,
        CancellationToken ct);

    Task<VerifiedAuthoringSnapshotPair> DownloadSnapshotPairAsync(
        string serviceName,
        string runId,
        string pairDirectory,
        CancellationToken ct);
}

public sealed class AuthoringControlClient : IAuthoringControlClient
{
    private const int DefaultStreamRetries = 3;
    private const string PublicationRefreshPurpose =
        "publication-refresh";
    private const string PublicationReconciliationPurpose =
        "publication-reconciliation";
    private const int PublicationReconciliationContractVersion = 3;
    private const string CanonicalEpochRecoveryPurpose =
        "canonical-epoch-recovery";

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

    private readonly HttpClient _httpClient;
    private readonly int _maxReadRetries;
    private readonly int _maxStreamRetries;
    private readonly TimeSpan _streamRetryDelay;
    private readonly AuthoringSnapshotPairPromotionHooks? _promotionHooks;

    public AuthoringControlClient(HttpClient httpClient)
        : this(
            httpClient,
            HttpRetryHelper.DefaultMaxRetries,
            DefaultStreamRetries,
            TimeSpan.FromMilliseconds(100),
            promotionHooks: null)
    {
    }

    internal AuthoringControlClient(
        HttpClient httpClient,
        int maxReadRetries,
        int maxStreamRetries,
        TimeSpan streamRetryDelay,
        AuthoringSnapshotPairPromotionHooks? promotionHooks)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (httpClient.BaseAddress is null ||
            !httpClient.BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException(
                "The authoring control HttpClient must have an absolute Orchestrator base address.",
                nameof(httpClient));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(maxReadRetries);
        ArgumentOutOfRangeException.ThrowIfNegative(maxStreamRetries);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            streamRetryDelay,
            TimeSpan.Zero);

        _httpClient = httpClient;
        _maxReadRetries = maxReadRetries;
        _maxStreamRetries = maxStreamRetries;
        _streamRetryDelay = streamRetryDelay;
        _promotionHooks = promotionHooks;
    }

    public async Task<AuthoringStartResult> StartAsync<TRequest>(
        string serviceName,
        TRequest request,
        CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentNullException.ThrowIfNull(request);
        string path = ControlPath(service);
        MutationResponse response = await SendMutationAsync(
            "start",
            service,
            runId: null,
            itemId: null,
            path,
            request,
            ct);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return new AuthoringStartResult(null);
        }

        AuthoringRunResponse run = Deserialize<AuthoringRunResponse>(
            response.Content,
            path);
        ValidateRunResponse(run);
        return new AuthoringStartResult(run);
    }

    public async Task<AuthoringRunResponse>
        StartPublicationRefreshAsync(
            string serviceName,
            string sourceRunId,
            CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRunId);
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(sourceRunId)}/publication-refresh";
        MutationResponse raw = await SendMutationAsync(
            PublicationRefreshPurpose,
            service,
            sourceRunId,
            itemId: null,
            path,
            body: null,
            ct);
        AuthoringRunResponse response =
            Deserialize<AuthoringRunResponse>(raw.Content, path);
        ValidateRunResponse(response);
        if (!string.Equals(
                response.Run.Purpose,
                PublicationRefreshPurpose,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Publication refresh response purpose '{response.Run.Purpose}' does not match '{PublicationRefreshPurpose}'.");
        }
        if (!string.Equals(
                response.Run.SourceRunId,
                sourceRunId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Publication refresh response source run '{response.Run.SourceRunId}' does not match requested run '{sourceRunId}'.");
        }

        string expectedProcessorKind =
            AuthoringServiceBinding.GetProcessorKind(service);
        if (!string.Equals(
                response.Run.ProcessorKind,
                expectedProcessorKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Publication refresh response processor '{response.Run.ProcessorKind}' does not match authoring service '{service}'.");
        }
        return response;
    }

    public async Task<PublicationReconciliationStartResult>
        StartPublicationReconciliationAsync(
            string serviceName,
            string sourceRunId,
            CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRunId);
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(sourceRunId)}/publication-reconciliation";
        MutationResponse raw = await SendMutationAsync(
            PublicationReconciliationPurpose,
            service,
            sourceRunId,
            itemId: null,
            path,
            body: null,
            ct);
        PublicationReconciliationStartResult response =
            Deserialize<PublicationReconciliationStartResult>(
                raw.Content,
                path);
        ValidateReconciliationRun(
            response.Run,
            response.Items,
            service,
            expectedRunId: null,
            sourceRunId);
        ValidateComparisonAndCounts(
            response.Comparison,
            response.Counts,
            sourceRunId);
        EnsureCurrentReconciliationComparison(response.Comparison);
        return response;
    }

    public async Task<PublicationReconciliationStatusResult>
        GetPublicationReconciliationAsync(
            string serviceName,
            string runId,
            CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(runId)}/publication-reconciliation";
        PublicationReconciliationStatusResult response =
            await SendReadJsonAsync<PublicationReconciliationStatusResult>(
                path,
                ct);
        ValidateReconciliationStatus(response, service, runId);
        return response;
    }

    public async Task<PublicationReconciliationRetryResult>
        RetryPublicationReconciliationAsync(
            string serviceName,
            string runId,
            CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(runId)}/publication-reconciliation/retry";
        MutationResponse raw = await SendMutationAsync(
            "publication-reconciliation-retry",
            service,
            runId,
            itemId: null,
            path,
            body: null,
            ct);
        PublicationReconciliationRetryResult response =
            Deserialize<PublicationReconciliationRetryResult>(
                raw.Content,
                path);
        ValidateReconciliationStatus(
            response.Status,
            service,
            runId,
            requireCurrentEvidence: true);
        return response;
    }

    public async Task<PublicationReconciliationCancelResult>
        CancelPublicationReconciliationAsync(
            string serviceName,
            string runId,
            string reason,
            CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string normalizedReason = reason.Trim();
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(runId)}/publication-reconciliation/cancel";
        MutationResponse raw = await SendMutationAsync(
            "publication-reconciliation-cancel",
            service,
            runId,
            itemId: null,
            path,
            new { reason = normalizedReason },
            ct);
        PublicationReconciliationCancelResult response =
            Deserialize<PublicationReconciliationCancelResult>(
                raw.Content,
                path);
        ValidateReconciliationStatus(response.Status, service, runId);
        if (response.CancelledAt == default ||
            string.IsNullOrWhiteSpace(response.Reason) ||
            !string.Equals(
                response.Status.Promotion.State,
                "cancelled",
                StringComparison.Ordinal) ||
            !string.Equals(
                response.Status.Run.Status,
                "superseded",
                StringComparison.Ordinal) ||
            response.Status.Promotion.CancelledAt != response.CancelledAt ||
            !string.Equals(
                response.Status.Promotion.CancellationReason,
                response.Reason,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Publication reconciliation cancellation response contains inconsistent audit data.");
        }
        return response;
    }

    public async Task<PublicationReconciliationAbandonResult>
        AbandonPublicationReconciliationAsync(
            string serviceName,
            string runId,
            string reason,
            CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(runId)}/publication-reconciliation/abandon";
        MutationResponse raw = await SendMutationAsync(
            "publication-reconciliation-abandon",
            service,
            runId,
            itemId: null,
            path,
            new { reason },
            ct);
        PublicationReconciliationAbandonResult response =
            Deserialize<PublicationReconciliationAbandonResult>(
                raw.Content,
                path);
        ValidateReconciliationStatus(response.Status, service, runId);
        if (response.AbandonedAt == default ||
            !string.Equals(response.Reason, reason, StringComparison.Ordinal) ||
            !string.Equals(
                response.Status.Promotion.State,
                "canonical-unpublished",
                StringComparison.Ordinal) ||
            !string.Equals(
                response.Status.Run.Status,
                "abandoned",
                StringComparison.Ordinal) ||
            response.Status.Promotion.MutationFenceHeld ||
            response.Status.Promotion.AbandonedAt != response.AbandonedAt ||
            !string.Equals(
                response.Status.Promotion.AbandonmentReason,
                response.Reason,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Publication reconciliation abandonment response contains inconsistent audit data.");
        }
        return response;
    }

    public async Task<CanonicalEpochRecoveryStartResult>
        StartCanonicalEpochRecoveryAsync(
            string serviceName,
            string sourceRunId,
            CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRunId);
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(sourceRunId)}/canonical-epoch-recovery";
        MutationResponse raw = await SendMutationAsync(
            CanonicalEpochRecoveryPurpose,
            service,
            sourceRunId,
            itemId: null,
            path,
            body: null,
            ct);
        CanonicalEpochRecoveryStartResult response =
            Deserialize<CanonicalEpochRecoveryStartResult>(
                raw.Content,
                path);
        ValidateCanonicalEpochRecoveryStatus(
            response.Status,
            service,
            expectedRunId: null,
            expectedSourceRunId: sourceRunId);
        return response;
    }

    public async Task<CanonicalEpochRecoveryStatusResult>
        GetCanonicalEpochRecoveryAsync(
            string serviceName,
            string runId,
            CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(runId)}/canonical-epoch-recovery";
        CanonicalEpochRecoveryStatusResult response =
            await SendReadJsonAsync<CanonicalEpochRecoveryStatusResult>(
                path,
                ct);
        ValidateCanonicalEpochRecoveryStatus(
            response,
            service,
            runId,
            expectedSourceRunId: null);
        return response;
    }

    public async Task<CanonicalEpochRecoveryRetryResult>
        RetryCanonicalEpochRecoveryAsync(
            string serviceName,
            string runId,
            CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(runId)}/canonical-epoch-recovery/retry";
        MutationResponse raw = await SendMutationAsync(
            "canonical-epoch-recovery-retry",
            service,
            runId,
            itemId: null,
            path,
            body: null,
            ct);
        CanonicalEpochRecoveryRetryResult response =
            Deserialize<CanonicalEpochRecoveryRetryResult>(
                raw.Content,
                path);
        ValidateCanonicalEpochRecoveryStatus(
            response.Status,
            service,
            runId,
            expectedSourceRunId: null);
        return response;
    }

    public async Task<AuthoringRunListResponse> ListAsync(
        string serviceName,
        int? limit,
        CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                limit,
                "Authoring run list limit must be between 1 and 100.");
        }

        string path = ControlPath(service);
        if (limit.HasValue)
        {
            path +=
                $"?limit={limit.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        AuthoringRunListResponse response =
            await SendReadJsonAsync<AuthoringRunListResponse>(path, ct);
        if (response.Runs.Any(run => string.IsNullOrWhiteSpace(run.RunId)))
        {
            throw new InvalidOperationException(
                "Authoring run list contains an empty run identifier.");
        }
        return response;
    }

    public async Task<AuthoringRunResponse> GetAsync(
        string serviceName,
        string runId,
        CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(runId)}";
        AuthoringRunResponse response =
            await SendReadJsonAsync<AuthoringRunResponse>(path, ct);
        ValidateRunResponse(response, runId);
        return response;
    }

    public async Task<AuthoringRetryResponse> RetryAsync(
        string serviceName,
        string runId,
        string itemId,
        CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(runId)}/items/{Uri.EscapeDataString(itemId)}/retry";
        MutationResponse raw = await SendMutationAsync(
            "retry",
            service,
            runId,
            itemId,
            path,
            body: null,
            ct);
        AuthoringRetryResponse response =
            Deserialize<AuthoringRetryResponse>(raw.Content, path);
        if (!string.Equals(response.ItemId, itemId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Retry response item '{response.ItemId}' does not match requested item '{itemId}'.");
        }
        return response;
    }

    public async Task<AuthoringItemSupersedeResult> SupersedeAsync(
        string serviceName,
        string runId,
        string itemId,
        string reason,
        CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        string path =
            $"{ControlPath(service)}/{Uri.EscapeDataString(runId)}/items/{Uri.EscapeDataString(itemId)}/supersede";
        MutationResponse raw = await SendMutationAsync(
            "supersede",
            service,
            runId,
            itemId,
            path,
            new AuthoringItemSupersedeRequest(reason),
            ct);
        AuthoringItemSupersedeResult response =
            Deserialize<AuthoringItemSupersedeResult>(raw.Content, path);
        if (!string.Equals(response.RunId, runId, StringComparison.Ordinal) ||
            !string.Equals(response.ItemId, itemId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Supersede response coordinates '{response.RunId}/{response.ItemId}' do not match requested item '{runId}/{itemId}'.");
        }
        return response;
    }

    public async Task<VerifiedAuthoringSnapshotPair>
        DownloadSnapshotPairAsync(
            string serviceName,
            string runId,
            string pairDirectory,
            CancellationToken ct)
    {
        string service = AuthoringServiceBinding.Normalize(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairDirectory);

        string targetDirectory = Path.GetFullPath(pairDirectory);
        AuthoringSnapshotPairPaths paths =
            AuthoringSnapshotPairPaths.Create(targetDirectory);
        VerifiedAuthoringSnapshotPair? existing =
            await AuthoringSnapshotPairPromoter.RecoverAndVerifyAsync(
                paths,
                service,
                runId,
                ct);
        if (existing is not null)
        {
            return existing;
        }

        string basePath =
            $"{ControlPath(service)}/{Uri.EscapeDataString(runId)}/snapshot";
        byte[] descriptorBytes = await SendReadBytesAsync(basePath, ct);
        AuthoringSnapshotDescriptor descriptor =
            AuthoringSnapshotPairVerifier.ParseAndValidateDescriptor(
                descriptorBytes,
                service,
                runId);
        string descriptorFileName =
            $"{descriptor.FileName}.descriptor.json";
        AuthoringSnapshotPairVerifier.ValidateDurablePairFileNames(
            descriptor.FileName,
            descriptorFileName);

        for (int attempt = 0; ; attempt++)
        {
            string stagingDirectory = paths.CreateStagingDirectory();
            try
            {
                VerifiedAuthoringSnapshotPair staged =
                    await DownloadStagedPairAsync(
                        service,
                        runId,
                        basePath,
                        descriptor,
                        descriptorBytes,
                        descriptorFileName,
                        stagingDirectory,
                        ct);
                return await AuthoringSnapshotPairPromoter.PromoteAsync(
                    paths,
                    staged,
                    _promotionHooks,
                    ct);
            }
            catch (AuthoringSnapshotPairSimulatedCrashException)
            {
                throw;
            }
            catch (SnapshotStreamException)
                when (attempt < _maxStreamRetries)
            {
                AuthoringSnapshotPairFileSystem.DeleteDirectoryIfExists(
                    stagingDirectory);
                if (_streamRetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_streamRetryDelay, ct);
                }
            }
            catch
            {
                if (!File.Exists(paths.StatePath))
                {
                    AuthoringSnapshotPairFileSystem.TryDeleteDirectory(
                        stagingDirectory);
                }
                throw;
            }
        }
    }

    private async Task<VerifiedAuthoringSnapshotPair>
        DownloadStagedPairAsync(
            string serviceName,
            string runId,
            string basePath,
            AuthoringSnapshotDescriptor descriptor,
            byte[] descriptorBytes,
            string descriptorFileName,
            string stagingDirectory,
            CancellationToken ct)
    {
        Directory.CreateDirectory(stagingDirectory);
        string ownershipMarker = Path.Combine(
            stagingDirectory,
            AuthoringSnapshotPairPaths.StagingMarkerFileName);
        await AuthoringSnapshotPairFileSystem.WriteBytesDurablyAsync(
            ownershipMarker,
            Encoding.UTF8.GetBytes("fhir-augury-authoring-pair"),
            ct);

        string descriptorPath =
            Path.Combine(stagingDirectory, descriptorFileName);
        await AuthoringSnapshotPairFileSystem.WriteBytesDurablyAsync(
            descriptorPath,
            descriptorBytes,
            ct);

        string databasePath =
            Path.Combine(stagingDirectory, descriptor.FileName);
        (long sizeBytes, string sha256) = await DownloadSnapshotBytesAsync(
            $"{basePath}/bytes",
            databasePath,
            ct);
        if (sizeBytes != descriptor.SizeBytes ||
            !string.Equals(
                sha256,
                descriptor.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Downloaded snapshot bytes do not match the trusted descriptor.");
        }

        string descriptorSha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(descriptorBytes))
            .ToLowerInvariant();
        AuthoringSnapshotPairManifest manifest = new(
            AuthoringSnapshotPairManifest.CurrentFormatVersion,
            serviceName,
            runId,
            descriptor.SnapshotId,
            descriptorFileName,
            descriptor.FileName,
            sizeBytes,
            descriptorSha256,
            sha256);
        File.Delete(ownershipMarker);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            JsonOptions);
        await AuthoringSnapshotPairFileSystem.WriteBytesDurablyAsync(
            Path.Combine(
                stagingDirectory,
                AuthoringSnapshotPairManifest.ReadyFileName),
            manifestBytes,
            ct);

        return await new AuthoringSnapshotPairVerifier()
            .VerifyReadyPairAsync(
                serviceName,
                runId,
                stagingDirectory,
                ct);
    }

    private async Task<(long SizeBytes, string Sha256)>
        DownloadSnapshotBytesAsync(
            string path,
            string destinationPath,
            CancellationToken ct)
    {
        using HttpResponseMessage response =
            await SendReadResponseAsync(
                path,
                bufferSuccessfulContent: false,
                ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateControlExceptionAsync(response, path, ct);
        }

        Stream input;
        try
        {
            input = await response.Content.ReadAsStreamAsync(ct);
        }
        catch (Exception ex) when (
            ex is HttpRequestException or IOException ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new SnapshotStreamException(
                "Snapshot response stream could not be opened.",
                ex);
        }

        await using (input)
        await using (FileStream output = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (System.Security.Cryptography.IncrementalHash hash =
               System.Security.Cryptography.IncrementalHash.CreateHash(
                   System.Security.Cryptography.HashAlgorithmName.SHA256))
        {
            byte[] buffer = new byte[128 * 1024];
            long sizeBytes = 0;
            while (true)
            {
                int read;
                try
                {
                    read = await input.ReadAsync(buffer, ct);
                }
                catch (Exception ex) when (
                    ex is HttpRequestException or IOException ||
                    ex is OperationCanceledException &&
                    !ct.IsCancellationRequested)
                {
                    throw new SnapshotStreamException(
                        "Snapshot response stream ended unexpectedly.",
                        ex);
                }
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                hash.AppendData(buffer, 0, read);
                sizeBytes = checked(sizeBytes + read);
            }
            await output.FlushAsync(ct);
            output.Flush(flushToDisk: true);
            string sha256 = Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
            return (sizeBytes, sha256);
        }
    }

    private async Task<T> SendReadJsonAsync<T>(
        string path,
        CancellationToken ct)
    {
        byte[] bytes = await SendReadBytesAsync(path, ct);
        return Deserialize<T>(bytes, path);
    }

    private async Task<byte[]> SendReadBytesAsync(
        string path,
        CancellationToken ct)
    {
        using HttpResponseMessage response =
            await SendReadResponseAsync(
                path,
                bufferSuccessfulContent: true,
                ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateControlExceptionAsync(response, path, ct);
        }
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<HttpResponseMessage> SendReadResponseAsync(
        string path,
        bool bufferSuccessfulContent,
        CancellationToken ct)
    {
        try
        {
            return await HttpRetryHelper.ExecuteWithRetryAsync(
                async token =>
                {
                    HttpRequestMessage request =
                        new(HttpMethod.Get, path);
                    HttpResponseMessage response =
                        await SendAndDisposeRequestAsync(
                            request,
                            token);
                    if (bufferSuccessfulContent &&
                        response.IsSuccessStatusCode)
                    {
                        try
                        {
                            await response.Content.LoadIntoBufferAsync(
                                token);
                        }
                        catch (IOException ex)
                        {
                            response.Dispose();
                            throw new HttpRequestException(
                                "Authoring response body was interrupted.",
                                ex);
                        }
                        catch
                        {
                            response.Dispose();
                            throw;
                        }
                    }
                    return response;
                },
                ct,
                _maxReadRetries,
                "Orchestrator authoring control");
        }
        catch (HttpRequestException ex)
            when (ex.StatusCode is HttpStatusCode statusCode)
        {
            throw new AuthoringControlException(
                statusCode,
                $"http-{(int)statusCode}",
                ex.Message,
                retryAfterHeader: null,
                retryAfter: null,
                relatedRunIds: [],
                path,
                ex);
        }
    }

    private async Task<HttpResponseMessage> SendAndDisposeRequestAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        using (request)
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
        }
    }

    private async Task<MutationResponse> SendMutationAsync(
        string operation,
        string serviceName,
        string? runId,
        string? itemId,
        string path,
        object? body,
        CancellationToken ct)
    {
        byte[]? bodyBytes = body is null
            ? null
            : JsonSerializer.SerializeToUtf8Bytes(
                body,
                body.GetType(),
                JsonOptions);
        using HttpRequestMessage request = new(HttpMethod.Post, path);
        if (bodyBytes is not null)
        {
            request.Content = new ByteArrayContent(bodyBytes);
            request.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/json");
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            if (!response.IsSuccessStatusCode)
            {
                throw await CreateControlExceptionAsync(
                    response,
                    path,
                    ct);
            }

            byte[] content =
                await response.Content.ReadAsByteArrayAsync(ct);
            return new MutationResponse(response.StatusCode, content);
        }
        catch (AuthoringControlException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or IOException or
                OperationCanceledException)
        {
            throw new AuthoringMutationOutcomeUnknownException(
                operation,
                serviceName,
                runId,
                itemId,
                ex);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static async Task<AuthoringControlException>
        CreateControlExceptionAsync(
            HttpResponseMessage response,
            string path,
            CancellationToken ct)
    {
        byte[] content;
        try
        {
            content = await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is HttpRequestException or IOException or
                OperationCanceledException)
        {
            return CreateFallbackControlException(
                response,
                path,
                detail: ex.Message,
                ex);
        }

        AuthoringConflictResponse? error = null;
        try
        {
            error = JsonSerializer.Deserialize<AuthoringConflictResponse>(
                content,
                JsonOptions);
        }
        catch (JsonException)
        {
        }

        string responseText = Encoding.UTF8.GetString(content);
        string errorCode = string.IsNullOrWhiteSpace(error?.Error)
            ? $"http-{(int)response.StatusCode}"
            : error.Error;
        string? detail = error is not null
            ? error.Detail
            : string.IsNullOrWhiteSpace(responseText)
                ? null
                : responseText;
        IEnumerable<string> relatedRunIds =
            (error?.ConflictingRunIds ?? [])
                .Where(value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(error?.RunId))
        {
            relatedRunIds = relatedRunIds.Append(error.RunId);
        }
        string[] distinctRelatedRunIds = relatedRunIds
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        (string? retryAfterHeader, TimeSpan? retryAfter) =
            ReadRetryAfter(response);
        return new AuthoringControlException(
            response.StatusCode,
            errorCode,
            detail,
            retryAfterHeader,
            retryAfter,
            distinctRelatedRunIds,
            path);
    }

    private static AuthoringControlException
        CreateFallbackControlException(
            HttpResponseMessage response,
            string path,
            string? detail,
            Exception? innerException)
    {
        (string? retryAfterHeader, TimeSpan? retryAfter) =
            ReadRetryAfter(response);
        return new AuthoringControlException(
            response.StatusCode,
            $"http-{(int)response.StatusCode}",
            detail,
            retryAfterHeader,
            retryAfter,
            relatedRunIds: [],
            path,
            innerException);
    }

    private static (string? Header, TimeSpan? Delay) ReadRetryAfter(
        HttpResponseMessage response)
    {
        RetryConditionHeaderValue? value = response.Headers.RetryAfter;
        string? header = value?.ToString();
        if (value?.Delta is TimeSpan delta)
        {
            return (header, delta);
        }
        if (value?.Date is DateTimeOffset date)
        {
            TimeSpan delay = date - DateTimeOffset.UtcNow;
            return (
                header,
                delay > TimeSpan.Zero ? delay : TimeSpan.Zero);
        }
        return (header, null);
    }

    private static T Deserialize<T>(byte[] bytes, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Authoring endpoint '{path}' returned an empty response.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Authoring endpoint '{path}' returned invalid JSON.",
                ex);
        }
    }

    private static void ValidateRunResponse(
        AuthoringRunResponse response,
        string? expectedRunId = null)
    {
        if (response.Run is null ||
            response.Items is null ||
            string.IsNullOrWhiteSpace(response.Run.RunId) ||
            expectedRunId is not null &&
            !string.Equals(
                response.Run.RunId,
                expectedRunId,
                StringComparison.Ordinal) ||
            response.Items.Any(item =>
                string.IsNullOrWhiteSpace(item.ItemId) ||
                !string.Equals(
                    item.RunId,
                    response.Run.RunId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Authoring run response contains inconsistent coordinates.");
        }
    }

    private static void ValidateReconciliationStatus(
        PublicationReconciliationStatusResult response,
        string serviceName,
        string runId,
        bool requireCurrentEvidence = false)
    {
        ValidateReconciliationRun(
            response.Run,
            response.Items,
            serviceName,
            runId,
            expectedSourceRunId: null);
        ValidateComparisonAndCounts(
            response.Comparison,
            response.Counts,
            response.Run.SourceRunId);
        if (requireCurrentEvidence)
        {
            EnsureCurrentReconciliationComparison(response.Comparison);
        }
        if (response.GroupingImpacts is null ||
            response.InvalidatedTicketKeys is null ||
            response.Promotion is null ||
            response.InvalidatedTicketKeys
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != response.InvalidatedTicketKeys.Count ||
            response.Counts.InvalidatedTicketCount !=
                response.InvalidatedTicketKeys.Count)
        {
            throw new InvalidOperationException(
                "Publication reconciliation response has inconsistent recovery state.");
        }
        if (!string.Equals(
                response.Promotion.State,
                "staged",
                StringComparison.Ordinal) &&
            !string.Equals(
                response.Promotion.State,
                "snapshot-publish-pending",
                StringComparison.Ordinal) &&
            !string.Equals(
                response.Promotion.State,
                "ready",
                StringComparison.Ordinal) &&
            !string.Equals(
                response.Promotion.State,
                "canonical-unpublished",
                StringComparison.Ordinal) &&
            !string.Equals(
                response.Promotion.State,
                "cancelled",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Publication reconciliation response has unknown promotion state '{response.Promotion.State}'.");
        }
        ValidateReconciliationProof(response, requireCurrentEvidence);
        if (string.Equals(
                response.Promotion.State,
                "cancelled",
                StringComparison.Ordinal) &&
            (response.Promotion.CancelledAt is null ||
             string.IsNullOrWhiteSpace(
                 response.Promotion.CancellationReason) ||
             !string.Equals(
                 response.Run.Status,
                 "superseded",
                 StringComparison.Ordinal) ||
             response.Promotion.MutationFenceHeld))
        {
            throw new InvalidOperationException(
                "Cancelled publication reconciliation response has inconsistent audit or fence state.");
        }
        if (string.Equals(
                response.Promotion.State,
                "canonical-unpublished",
                StringComparison.Ordinal) &&
            (response.Promotion.AbandonedAt is null ||
             string.IsNullOrWhiteSpace(
                 response.Promotion.AbandonmentReason) ||
             !string.Equals(
                 response.Run.Status,
                 "abandoned",
                 StringComparison.Ordinal) ||
             response.Promotion.MutationFenceHeld))
        {
            throw new InvalidOperationException(
                "Abandoned publication reconciliation response has inconsistent audit or fence state.");
        }
        if (response.CanonicalEpochRecovery is { } recovery &&
            (!string.Equals(
                 recovery.SourceRunId,
                 response.Run.RunId,
                 StringComparison.Ordinal) ||
             recovery.AuthoringEpoch != response.Run.AuthoringEpoch ||
             recovery.State is not (
                 "materialization-pending" or
                 "snapshot-publish-pending" or
                 "ready") ||
             recovery.State == "ready" &&
             (string.IsNullOrWhiteSpace(recovery.SnapshotId) ||
              string.IsNullOrWhiteSpace(recovery.SnapshotSha256) ||
              recovery.ResolvedAt is null)))
        {
            throw new InvalidOperationException(
                "Publication reconciliation response has an inconsistent canonical-epoch recovery link.");
        }
    }

    private static void EnsureCurrentReconciliationComparison(
        PublicationReconciliationComparison comparison)
    {
        if (comparison.ContractVersion !=
            PublicationReconciliationContractVersion)
        {
            throw new InvalidOperationException(
                $"Publication reconciliation contract version {comparison.ContractVersion} is readable for status and audit only.");
        }
    }

    private static void ValidateReconciliationProof(
        PublicationReconciliationStatusResult response,
        bool requireCurrentEvidence)
    {
        PublicationReconciliationProof? proof = response.PublicationProof;
        if (proof is null)
        {
            if (requireCurrentEvidence &&
                response.Promotion.State is "snapshot-publish-pending" or "ready")
            {
                throw new InvalidOperationException(
                    "Publication reconciliation recovery requires a current publication proof.");
            }
            return;
        }
        if (!requireCurrentEvidence && proof.ContractVersion is 1 or 2)
        {
            return;
        }
        if (proof.ContractVersion != PublicationReconciliationContractVersion ||
            response.Comparison.ContractVersion !=
                PublicationReconciliationContractVersion ||
            proof.Purpose != PublicationReconciliationPurpose ||
            proof.SourceRunId != response.Comparison.SourceRunId ||
            proof.SourceSnapshotId != response.Comparison.SourceSnapshotId ||
            proof.StableJiraGeneration != response.Comparison.StableJiraGeneration ||
            proof.AcceptedTicketCount != response.Counts.AcceptedTicketCount ||
            proof.CarryForwardTicketCount != response.Counts.CarryForwardTicketCount ||
            proof.ReAuthorTicketCount != response.Counts.ReAuthorTicketCount ||
            !IsCanonicalSha256(proof.CorpusFingerprint) ||
            !IsCanonicalSha256(proof.GroupingFingerprint) ||
            !IsCanonicalSha256(proof.GroupingImpactFingerprint) ||
            proof.CapturedAt == default ||
            proof.CapturedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Publication reconciliation response has legacy, incomplete, or inconsistent publication proof.");
        }
    }

    private static void ValidateCanonicalEpochRecoveryStatus(
        CanonicalEpochRecoveryStatusResult response,
        string serviceName,
        string? expectedRunId,
        string? expectedSourceRunId)
    {
        if (response is null ||
            response.SourceAbandonment is null ||
            response.Frozen is null ||
            response.Recovery is null)
        {
            throw new InvalidOperationException(
                "Canonical-epoch recovery response is incomplete.");
        }
        ValidateRunResponse(
            new AuthoringRunResponse(response.Run, response.Items),
            expectedRunId);
        string expectedProcessorKind =
            AuthoringServiceBinding.GetProcessorKind(serviceName);
        bool valid =
            !response.Run.DatabaseOnly &&
            string.Equals(
                response.Run.ProcessorKind,
                expectedProcessorKind,
                StringComparison.Ordinal) &&
            string.Equals(
                response.Run.Purpose,
                CanonicalEpochRecoveryPurpose,
                StringComparison.Ordinal) &&
            string.Equals(
                response.Run.SourceRunId,
                response.SourceAbandonment.RunId,
                StringComparison.Ordinal) &&
            !string.Equals(
                response.Run.RunId,
                response.SourceAbandonment.RunId,
                StringComparison.Ordinal) &&
            (expectedSourceRunId is null ||
             string.Equals(
                 response.SourceAbandonment.RunId,
                 expectedSourceRunId,
                 StringComparison.Ordinal)) &&
            response.Run.AuthoringEpoch ==
                response.SourceAbandonment.AuthoringEpoch &&
            string.Equals(
                response.SourceAbandonment.PromotionState,
                "canonical-unpublished",
                StringComparison.Ordinal) &&
            response.SourceAbandonment.AbandonedAt != default &&
            response.SourceAbandonment.AbandonedAt.Offset == TimeSpan.Zero &&
            !string.IsNullOrWhiteSpace(
                response.SourceAbandonment.Reason) &&
            response.Frozen.AcceptedTicketCount > 0 &&
            response.Frozen.AcceptedTicketCount ==
                response.Items.Count &&
            response.Items.All(item =>
                string.Equals(
                    item.Status,
                    "complete",
                    StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(item.AcceptedReceiptId)) &&
            response.Frozen.GroupingPartitionCount >= 0 &&
            IsCanonicalSha256(response.Frozen.CorpusFingerprint) &&
            IsCanonicalSha256(response.Frozen.GroupingFingerprint) &&
            IsCanonicalSha256(response.Frozen.RecipeFingerprint) &&
            response.Frozen.CapturedAt != default &&
            response.Frozen.CapturedAt.Offset == TimeSpan.Zero &&
            response.Recovery.State is
                "materialization-pending" or
                "snapshot-publish-pending" or
                "ready";
        if (!valid)
        {
            throw new InvalidOperationException(
                "Canonical-epoch recovery response has inconsistent coordinates.");
        }

        if (response.PublicationProof is { } proof &&
            (proof.ContractVersion != 1 ||
             !string.Equals(
                 proof.Purpose,
                 CanonicalEpochRecoveryPurpose,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 proof.RunId,
                 response.Run.RunId,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 proof.SourceRunId,
                 response.SourceAbandonment.RunId,
                 StringComparison.Ordinal) ||
             proof.AuthoringEpoch != response.Run.AuthoringEpoch ||
             proof.AbandonedAt !=
                 response.SourceAbandonment.AbandonedAt ||
             proof.CapturedAt != response.Frozen.CapturedAt ||
             !string.Equals(
                 proof.CorpusFingerprint,
                 response.Frozen.CorpusFingerprint,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 proof.GroupingFingerprint,
                 response.Frozen.GroupingFingerprint,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 proof.RecipeFingerprint,
                 response.Frozen.RecipeFingerprint,
                 StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Canonical-epoch recovery proof conflicts with its frozen state.");
        }
        if (response.Recovery.State == "ready" &&
            (response.Recovery.MutationFenceHeld ||
             response.PublicationProof is null ||
             response.Snapshot is null ||
             !string.Equals(
                 response.Snapshot.RunId,
                 response.Run.RunId,
                 StringComparison.Ordinal) ||
             response.Snapshot.AuthoringEpoch !=
                 response.Run.AuthoringEpoch ||
             response.Snapshot.SchemaVersion != 3 ||
             !IsCanonicalSha256(response.Snapshot.Sha256) ||
             response.Snapshot.PublicationProof is not { } snapshotProof ||
             snapshotProof.Purpose != CanonicalEpochRecoveryPurpose ||
             snapshotProof.SourceRunId != response.SourceAbandonment.RunId ||
             snapshotProof.SourceContentRevision != response.Run.AuthoringEpoch ||
             snapshotProof.CorpusFingerprint != response.Frozen.CorpusFingerprint ||
             snapshotProof.GroupingFingerprint != response.Frozen.GroupingFingerprint ||
             !string.Equals(
                 response.Run.Status,
                 "completed",
                 StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Ready canonical-epoch recovery response is incomplete.");
        }
        if (response.Recovery.State != "ready" &&
            (!response.Recovery.MutationFenceHeld ||
             response.Snapshot is not null ||
             response.PublicationProof is not null ||
             response.Run.Status is not ("running" or "finalizing" or "error")))
        {
            throw new InvalidOperationException(
                "Pending canonical-epoch recovery response lost its restriction or retryable run.");
        }
    }

    private static bool IsCanonicalSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character =>
               character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void ValidateReconciliationRun(
        AuthoringRunStatus run,
        IReadOnlyList<AuthoringRunItemStatus> items,
        string serviceName,
        string? expectedRunId,
        string? expectedSourceRunId)
    {
        ValidateRunResponse(new AuthoringRunResponse(run, items), expectedRunId);
        if (!string.Equals(
                run.Purpose,
                PublicationReconciliationPurpose,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Publication reconciliation response purpose '{run.Purpose}' does not match '{PublicationReconciliationPurpose}'.");
        }
        if (expectedSourceRunId is not null &&
            !string.Equals(
                run.SourceRunId,
                expectedSourceRunId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Publication reconciliation response source run '{run.SourceRunId}' does not match requested run '{expectedSourceRunId}'.");
        }
        string expectedProcessorKind =
            AuthoringServiceBinding.GetProcessorKind(serviceName);
        if (!string.Equals(
                run.ProcessorKind,
                expectedProcessorKind,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Publication reconciliation response processor '{run.ProcessorKind}' does not match authoring service '{serviceName}'.");
        }
    }

    private static void ValidateComparisonAndCounts(
        PublicationReconciliationComparison comparison,
        AuthoringRunReconciliationCounts counts,
        string? expectedSourceRunId)
    {
        if (comparison is null ||
            counts is null ||
            comparison.Items is null ||
            comparison.ContractVersion < 1 ||
            string.IsNullOrWhiteSpace(comparison.SourceRunId) ||
            expectedSourceRunId is not null &&
            !string.Equals(
                comparison.SourceRunId,
                expectedSourceRunId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Publication reconciliation comparison has inconsistent source coordinates.");
        }
        if (comparison.Items.Any(item =>
                item is null ||
                string.IsNullOrWhiteSpace(item.TicketKey) ||
                string.IsNullOrWhiteSpace(item.Disposition) ||
                comparison.ContractVersion >= 2 &&
                (string.IsNullOrWhiteSpace(item.ItemKind) ||
                 string.IsNullOrWhiteSpace(
                     item.ExpectedSourceRevision))))
        {
            throw new InvalidOperationException(
                "Publication reconciliation comparison has incomplete item coordinates.");
        }
        counts.Validate();
        int carryForward = comparison.Items.Count(item =>
            string.Equals(
                item.Disposition,
                "carry-forward",
                StringComparison.Ordinal));
        int reAuthor = comparison.Items.Count(item =>
            string.Equals(
                item.Disposition,
                "re-author",
                StringComparison.Ordinal));
        if (comparison.Items.Count != counts.AcceptedTicketCount ||
            carryForward != counts.CarryForwardTicketCount ||
            reAuthor != counts.ReAuthorTicketCount ||
            carryForward + reAuthor != comparison.Items.Count)
        {
            throw new InvalidOperationException(
                "Publication reconciliation comparison does not match its reported counts.");
        }
    }

    private static string ControlPath(string serviceName)
        => $"api/v1/processing-services/{Uri.EscapeDataString(serviceName)}/authoring/runs";

    private sealed record MutationResponse(
        HttpStatusCode StatusCode,
        byte[] Content);

    private sealed class SnapshotStreamException(
        string message,
        Exception innerException)
        : IOException(message, innerException);
}
