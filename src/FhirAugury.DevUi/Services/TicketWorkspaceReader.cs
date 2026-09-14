using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.DevUi.Configuration;
using FhirAugury.DevUi.Models;
using FhirAugury.Processing.Client;
using FhirAugury.Processing.Contracts;
using Microsoft.Extensions.Options;

namespace FhirAugury.DevUi.Services;

public sealed class TicketWorkspaceReader
{
    private readonly IAuthoringControlClient _authoringClient;
    private readonly IOrchestratorReadinessClient _readinessClient;
    private readonly TicketWorkflowCatalog _catalog;
    private readonly ReadinessEvaluator _readinessEvaluator;
    private readonly IOptions<DevUiOptions> _options;
    private readonly ILogger<TicketWorkspaceReader> _logger;

    public TicketWorkspaceReader(
        IAuthoringControlClient authoringClient,
        IOrchestratorReadinessClient readinessClient,
        TicketWorkflowCatalog catalog,
        ReadinessEvaluator readinessEvaluator,
        IOptions<DevUiOptions> options,
        ILogger<TicketWorkspaceReader> logger)
    {
        _authoringClient = authoringClient ??
            throw new ArgumentNullException(nameof(authoringClient));
        _readinessClient = readinessClient ??
            throw new ArgumentNullException(nameof(readinessClient));
        _catalog = catalog ??
            throw new ArgumentNullException(nameof(catalog));
        _readinessEvaluator = readinessEvaluator ??
            throw new ArgumentNullException(nameof(readinessEvaluator));
        _options = options ??
            throw new ArgumentNullException(nameof(options));
        _logger = logger ??
            throw new ArgumentNullException(nameof(logger));
    }

    public async Task<WorkspaceReadOutcome<AuthoringRunListResponse>>
        ReadHistoryAsync(
            string? workflow,
            CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ReadContext context = new(
            "history",
            workflow,
            "api/v1/processing-services/{service}/authoring/runs",
            "Run history");
        WorkspaceReadFailureReason? inputFailure =
            WorkspaceReadFailureReason.InvalidWorkflow;
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workflow);
            TicketWorkflowDefinition definition = _catalog.Get(workflow);
            context = context with
            {
                Workflow = definition.RouteKey,
                Endpoint =
                    $"api/v1/processing-services/{Uri.EscapeDataString(definition.ProcessingServiceName)}/authoring/runs",
                Description = $"{definition.ProcessingServiceName} run history",
            };

            inputFailure = WorkspaceReadFailureReason.InvalidOptions;
            DevUiOptions options = _options.Value;
            ArgumentNullException.ThrowIfNull(options);
            int limit = options.RecentRunLimit;
            if (limit is < 1 or > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options.RecentRunLimit),
                    limit,
                    "Recent run limit must be between 1 and 100.");
            }
            context = context with
            {
                Endpoint =
                    $"{context.Endpoint}?limit={limit.ToString(CultureInfo.InvariantCulture)}",
            };
            inputFailure = null;

            AuthoringRunListResponse response =
                await _authoringClient.ListAsync(
                    definition.ProcessingServiceName,
                    limit,
                    ct);
            ct.ThrowIfCancellationRequested();
            ValidateHistory(response);
            return WorkspaceReadOutcome<AuthoringRunListResponse>.Success(
                response with
                {
                    Runs = Array.AsReadOnly(response.Runs.ToArray()),
                });
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            return FailedRead<AuthoringRunListResponse>(
                ex,
                context,
                inputFailure);
        }
    }

    public async Task<WorkspaceReadOutcome<TicketWorkspaceReadiness>>
        ReadReadinessAsync(
            bool refresh = false,
            CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ReadContext context = new(
            refresh ? "readiness-refresh" : "readiness",
            null,
            refresh ? "api/v1/services/refresh" : "api/v1/services",
            "Workflow readiness");
        try
        {
            ServicesStatusResponse response = refresh
                ? await _readinessClient.RefreshAsync(ct)
                : await _readinessClient.GetAsync(ct);
            ct.ThrowIfCancellationRequested();
            ValidateReadiness(response);
            return WorkspaceReadOutcome<TicketWorkspaceReadiness>.Success(
                new TicketWorkspaceReadiness(
                    response.LastCheckedAt,
                    _catalog.Workflows.ToImmutableDictionary(
                        workflow => workflow.RouteKey,
                        workflow => _readinessEvaluator.Evaluate(
                            response,
                            workflow),
                        StringComparer.OrdinalIgnoreCase)));
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            return FailedRead<TicketWorkspaceReadiness>(ex, context);
        }
    }

    private WorkspaceReadOutcome<T> FailedRead<T>(
        Exception exception,
        ReadContext context,
        WorkspaceReadFailureReason? inputFailure = null) where T : class
    {
        WorkspaceReadFailureReason reason =
            ClassifyFailure(exception, inputFailure);
        HttpStatusCode? statusCode =
            (exception as HttpRequestException)?.StatusCode;
        AuthoringControlException? controlException =
            exception as AuthoringControlException;
        string endpoint =
            controlException is not null &&
            controlException.Endpoint != "authoring control"
                ? controlException.Endpoint
                : context.Endpoint;
        WorkspaceReadFailure failure = new(
            reason,
            context.Operation,
            context.Workflow,
            endpoint,
            FailureMessage(context, reason, statusCode),
            statusCode,
            controlException?.ErrorCode);
        _logger.Log(
            reason == WorkspaceReadFailureReason.Unexpected
                ? LogLevel.Error
                : LogLevel.Warning,
            exception,
            "Workspace {Operation} read failed for {Workflow} at {Endpoint}: {Reason}, HTTP {StatusCode}, code {ErrorCode}",
            failure.Operation,
            failure.Workflow,
            failure.Endpoint,
            failure.Reason,
            (int?)failure.StatusCode,
            failure.ErrorCode);
        return WorkspaceReadOutcome<T>.FromFailure(failure);
    }

    private static WorkspaceReadFailureReason ClassifyFailure(
        Exception exception,
        WorkspaceReadFailureReason? inputFailure) =>
        exception switch
        {
            AuthoringControlException control =>
                ClassifyHttpStatus(control.StatusCode),
            HttpRequestException request =>
                ClassifyHttpStatus(request.StatusCode),
            OperationCanceledException or TimeoutException =>
                WorkspaceReadFailureReason.Timeout,
            IOException =>
                WorkspaceReadFailureReason.Transport,
            OptionsValidationException =>
                WorkspaceReadFailureReason.InvalidOptions,
            ArgumentException or KeyNotFoundException or
                InvalidOperationException when inputFailure.HasValue =>
                inputFailure.Value,
            // The authoring client wraps invalid JSON and empty payloads.
            // Its list validation also rejects a null Runs collection.
            JsonException or InvalidOperationException or ArgumentNullException =>
                WorkspaceReadFailureReason.InvalidResponse,
            _ => WorkspaceReadFailureReason.Unexpected,
        };

    private static WorkspaceReadFailureReason ClassifyHttpStatus(
        HttpStatusCode? statusCode) =>
        statusCode switch
        {
            null => WorkspaceReadFailureReason.Transport,
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests =>
                WorkspaceReadFailureReason.TransientHttp,
            HttpStatusCode code when (int)code is >= 500 and <= 599 =>
                WorkspaceReadFailureReason.TransientHttp,
            HttpStatusCode.Unauthorized =>
                WorkspaceReadFailureReason.Authentication,
            HttpStatusCode.Forbidden =>
                WorkspaceReadFailureReason.Authorization,
            HttpStatusCode.NotFound =>
                WorkspaceReadFailureReason.NotFound,
            _ => WorkspaceReadFailureReason.HttpError,
        };

    private static string FailureMessage(
        ReadContext context,
        WorkspaceReadFailureReason reason,
        HttpStatusCode? statusCode)
    {
        string status = statusCode is HttpStatusCode code
            ? $" (HTTP {(int)code})"
            : "";
        string detail = reason switch
        {
            WorkspaceReadFailureReason.TransientHttp =>
                $"is temporarily unavailable{status}.",
            WorkspaceReadFailureReason.Transport =>
                "could not be received from the Orchestrator.",
            WorkspaceReadFailureReason.Timeout =>
                "timed out.",
            WorkspaceReadFailureReason.Authentication =>
                $"requires authentication{status}.",
            WorkspaceReadFailureReason.Authorization =>
                $"was denied{status}.",
            WorkspaceReadFailureReason.NotFound =>
                $"endpoint was not found{status}.",
            WorkspaceReadFailureReason.HttpError =>
                $"was rejected{status}.",
            WorkspaceReadFailureReason.InvalidWorkflow =>
                "could not be read because the workflow route is invalid.",
            WorkspaceReadFailureReason.InvalidOptions =>
                "could not be read because Dev UI configuration is invalid.",
            WorkspaceReadFailureReason.InvalidResponse =>
                "returned an invalid response.",
            _ => "could not be read due to an unexpected error.",
        };
        return $"{context.Description} {detail} Endpoint: {context.Endpoint}.";
    }

    private static void ValidateHistory(AuthoringRunListResponse? response)
    {
        if (response?.Runs is null)
        {
            throw new InvalidOperationException(
                "Authoring history must contain a run collection.");
        }
        HashSet<string> runIds = new(StringComparer.Ordinal);
        foreach (AuthoringRunStatus? run in response.Runs)
        {
            if (run is null ||
                string.IsNullOrWhiteSpace(run.RunId) ||
                string.IsNullOrWhiteSpace(run.ProcessorKind) ||
                string.IsNullOrWhiteSpace(run.Status) ||
                !runIds.Add(run.RunId))
            {
                throw new InvalidOperationException(
                    "Authoring history contains an unusable run.");
            }
        }
    }

    private static void ValidateReadiness(ServicesStatusResponse? response)
    {
        if (response?.Services is null)
        {
            throw new InvalidOperationException(
                "Readiness must contain a service collection.");
        }
        HashSet<string> serviceNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (ServiceHealthInfo? service in response.Services)
        {
            if (service is null ||
                string.IsNullOrWhiteSpace(service.Name) ||
                string.IsNullOrWhiteSpace(service.ServiceKind) ||
                string.IsNullOrWhiteSpace(service.Status) ||
                service.RequiredServices is null ||
                !serviceNames.Add($"{service.ServiceKind}/{service.Name}"))
            {
                throw new InvalidOperationException(
                    "Readiness contains an unusable service observation.");
            }
        }
    }

    private sealed record ReadContext(
        string Operation,
        string? Workflow,
        string Endpoint,
        string Description);
}
