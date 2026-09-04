using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Hydration;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Processing;

public sealed class PlannerTicketHandler
    : JiraAuthoringWorkItemHandler,
      IProcessingWorkItemHandler<JiraProcessingSourceTicketRecord>
{
    private readonly JiraAgentCommandRenderer _commandRenderer;
    private readonly IJiraAgentCliRunner _runner;
    private readonly JiraProcessingSourceTicketStore _store;
    private readonly IJiraTicketDiscoveryClient _discoveryClient;
    private readonly IJiraAgentExtensionTokenProvider _extensionTokenProvider;
    private readonly PlannerDatabase _database;
    private readonly PlannedTicketHydrator _hydrator;
    private readonly IOptions<ProcessingServiceOptions> _processingOptions;
    private readonly ILogger<PlannerTicketHandler> _logger;

    [ActivatorUtilitiesConstructor]
    public PlannerTicketHandler(
        JiraAgentCommandRenderer commandRenderer,
        IJiraAgentCliRunner runner,
        JiraProcessingSourceTicketStore store,
        IJiraTicketDiscoveryClient discoveryClient,
        IJiraAgentExtensionTokenProvider extensionTokenProvider,
        PlannerDatabase database,
        PlannedTicketHydrator hydrator,
        AuthoringRunStore authoringStore,
        IOptions<ProcessingServiceOptions> processingOptions,
        ILogger<PlannerTicketHandler> logger)
        : base(
            commandRenderer,
            runner,
            authoringStore,
            extensionTokenProvider,
            processingOptions)
    {
        _commandRenderer = commandRenderer;
        _runner = runner;
        _store = store;
        _discoveryClient = discoveryClient;
        _extensionTokenProvider = extensionTokenProvider;
        _database = database;
        _hydrator = hydrator;
        _processingOptions = processingOptions;
        _logger = logger;
    }

    public PlannerTicketHandler(
        JiraAgentCommandRenderer commandRenderer,
        IJiraAgentCliRunner runner,
        JiraProcessingSourceTicketStore store,
        IJiraTicketDiscoveryClient discoveryClient,
        IJiraAgentExtensionTokenProvider extensionTokenProvider,
        PlannerDatabase database,
        PlannedTicketHydrator hydrator,
        IOptions<ProcessingServiceOptions> processingOptions,
        IOptions<Configuration.PlannerOptions> plannerOptions,
        ILogger<PlannerTicketHandler> logger)
        : this(
            commandRenderer,
            runner,
            store,
            discoveryClient,
            extensionTokenProvider,
            database,
            hydrator,
            new AuthoringRunStore(database),
            processingOptions,
            logger)
    {
        _ = plannerOptions;
    }

    public async Task ProcessAsync(JiraProcessingSourceTicketRecord item, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> extensionTokens =
            await _extensionTokenProvider.GetTokensAsync(item, ct);
        JiraAgentCommandContext context = new()
        {
            TicketKey = item.Key,
            SourceTicketId = item.Id,
            DatabasePath = Path.GetFullPath(_processingOptions.Value.DatabasePath),
            SourceTicketShape = item.SourceTicketShape,
            ExtensionTokens = extensionTokens,
        };
        JiraAgentCommand command = _commandRenderer.Render(context);
        _logger.LogInformation("Starting ticket-plan agent for {TicketKey}", item.Key);
        await _database.DeletePlanForTicketAsync(item.Key, ct);

        JiraAgentResult result;
        try
        {
            result = await _runner.RunAsync(command, context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _database.DeletePlanForTicketAsync(item.Key, CancellationToken.None);
            await MarkErrorAsync(item, $"Agent command failed: {ex.Message}", null, ct);
            throw new InvalidOperationException($"Agent command failed for {item.Key}: {ex.Message}", ex);
        }

        if (ct.IsCancellationRequested || result.Canceled)
        {
            string message = $"Agent run for {item.Key} was canceled.";
            await _database.DeletePlanForTicketAsync(item.Key, CancellationToken.None);
            if (!ct.IsCancellationRequested)
            {
                await MarkErrorAsync(item, message, result.ExitCode, CancellationToken.None);
            }
            throw new OperationCanceledException(message, ct);
        }

        if (result.ExitCode != 0)
        {
            string message = string.IsNullOrWhiteSpace(result.StderrTail)
                ? $"Agent exited with code {result.ExitCode}."
                : result.StderrTail;
            await _database.DeletePlanForTicketAsync(item.Key, CancellationToken.None);
            await MarkErrorAsync(item, message, result.ExitCode, ct);
            throw new InvalidOperationException(message);
        }

        if (!await _database.PlanExistsAsync(item.Key, ct))
        {
            string message = $"Agent completed but did not persist planned_tickets row for {item.Key}.";
            await MarkErrorAsync(item, message, result.ExitCode, ct);
            throw new InvalidOperationException(message);
        }

        await _hydrator.HydrateAsync(item.Key, ct);
        await _store.MarkCompleteAsync(item, DateTimeOffset.UtcNow, ct);
        await _discoveryClient.MarkProcessedAsync(item.Key, item.SourceTicketShape, ct);
        _logger.LogInformation(
            "Completed ticket-plan agent for {TicketKey} in {ElapsedMs} ms",
            item.Key,
            result.Elapsed.TotalMilliseconds);
    }

    protected override async Task<AuthoringWorkResult> OnReceiptAcceptedAsync(
        JiraAuthoringWorkItem item,
        string receiptId,
        CancellationToken ct)
    {
        HydrationAttemptResult hydration =
            await _hydrator.HydrateWithResultAsync(item.SourceTicket.Key, ct);
        return hydration is HydrationAttemptFailure failure
            ? AuthoringWorkResult.Retry(failure.Reason)
            : AuthoringWorkResult.Complete(receiptId);
    }

    private Task MarkErrorAsync(
        JiraProcessingSourceTicketRecord item,
        string message,
        int? exitCode,
        CancellationToken ct)
        => _store.MarkErrorAsync(item, message, exitCode, DateTimeOffset.UtcNow, ct);
}
