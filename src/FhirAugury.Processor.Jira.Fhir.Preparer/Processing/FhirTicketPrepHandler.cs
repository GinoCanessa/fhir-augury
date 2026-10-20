using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Hydration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public sealed class FhirTicketPrepHandler
    : JiraAuthoringWorkItemHandler,
      IProcessingWorkItemHandler<JiraProcessingSourceTicketRecord>
{
    private readonly JiraAgentCommandRenderer _commandRenderer;
    private readonly IJiraAgentCliRunner _runner;
    private readonly JiraProcessingSourceTicketStore _store;
    private readonly IJiraTicketDiscoveryClient _discoveryClient;
    private readonly PreparerDatabase _database;
    private readonly PreparedTicketHydrator _hydrator;
    private readonly IOptions<ProcessingServiceOptions> _processingOptions;
    private readonly AuthoringRunStore _authoringStore;
    private readonly ILogger<FhirTicketPrepHandler> _logger;

    public FhirTicketPrepHandler(
        JiraAgentCommandRenderer commandRenderer,
        IJiraAgentCliRunner runner,
        JiraProcessingSourceTicketStore store,
        IJiraTicketDiscoveryClient discoveryClient,
        PreparerDatabase database,
        PreparedTicketHydrator hydrator,
        AuthoringRunStore authoringStore,
        IJiraAgentExtensionTokenProvider extensionTokenProvider,
        IOptions<ProcessingServiceOptions> processingOptions,
        ILogger<FhirTicketPrepHandler> logger)
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
        _database = database;
        _hydrator = hydrator;
        _processingOptions = processingOptions;
        _authoringStore = authoringStore;
        _logger = logger;
    }

    public FhirTicketPrepHandler(
        JiraAgentCommandRenderer commandRenderer,
        IJiraAgentCliRunner runner,
        JiraProcessingSourceTicketStore store,
        IJiraTicketDiscoveryClient discoveryClient,
        PreparerDatabase database,
        PreparedTicketHydrator hydrator,
        IOptions<ProcessingServiceOptions> processingOptions,
        ILogger<FhirTicketPrepHandler> logger)
        : this(
            commandRenderer,
            runner,
            store,
            discoveryClient,
            database,
            hydrator,
            new AuthoringRunStore(database),
            new EmptyJiraAgentExtensionTokenProvider(),
            processingOptions,
            logger)
    {
    }

    public async Task ProcessAsync(JiraProcessingSourceTicketRecord item, CancellationToken ct)
    {
        JiraAgentCommandContext context = new()
        {
            TicketKey = item.Key,
            SourceTicketId = item.Id,
            DatabasePath = _processingOptions.Value.DatabasePath,
            SourceTicketShape = item.SourceTicketShape,
            ExtensionTokens = new Dictionary<string, string>(),
        };
        JiraAgentCommand command = _commandRenderer.Render(context);
        _logger.LogInformation("Starting ticket-prep agent for {TicketKey}", item.Key);
        JiraAgentResult result;
        try
        {
            result = await _runner.RunAsync(command, context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await MarkErrorAsync(item, $"Agent command failed: {ex.Message}", null, ct);
            throw new InvalidOperationException($"Agent command failed for {item.Key}: {ex.Message}", ex);
        }

        if (ct.IsCancellationRequested || result.Canceled)
        {
            string message = $"Agent run for {item.Key} was canceled.";
            if (!ct.IsCancellationRequested)
            {
                await MarkErrorAsync(item, message, result.ExitCode, CancellationToken.None);
            }

            throw new OperationCanceledException(message, ct);
        }

        if (result.ExitCode != 0)
        {
            string message = string.IsNullOrWhiteSpace(result.StderrTail) ? $"Agent exited with code {result.ExitCode}." : result.StderrTail;
            await MarkErrorAsync(item, message, result.ExitCode, ct);
            throw new InvalidOperationException(message);
        }

        bool persisted = await _database.PreparedTicketExistsAsync(item.Key, ct);
        if (!persisted)
        {
            string message = $"Agent completed but did not persist prepared_tickets row for {item.Key}.";
            await MarkErrorAsync(item, message, result.ExitCode, ct);
            throw new InvalidOperationException(message);
        }

        await _hydrator.HydrateAsync(item.Key, ct);

        DateTimeOffset completedAt = DateTimeOffset.UtcNow;
        await _store.MarkCompleteAsync(item, completedAt, ct);
        await _discoveryClient.MarkProcessedAsync(item.Key, item.SourceTicketShape, ct);
        _logger.LogInformation("Completed ticket-prep agent for {TicketKey} in {ElapsedMs} ms", item.Key, result.Elapsed.TotalMilliseconds);
    }

    private Task MarkErrorAsync(JiraProcessingSourceTicketRecord item, string message, int? exitCode, CancellationToken ct) =>
        _store.MarkErrorAsync(item, message, exitCode, DateTimeOffset.UtcNow, ct);

    protected override async Task<AuthoringWorkResult> OnReceiptAcceptedAsync(
        JiraAuthoringWorkItem item,
        string receiptId,
        CancellationToken ct)
    {
        AuthoringRunRecord? run =
            await _authoringStore.GetRunAsync(item.RunItem.RunId, ct);
        if (run?.Purpose ==
            PreparedTicketPublicationReconciliationContract.Purpose)
        {
            return AuthoringWorkResult.Complete(receiptId);
        }
        HydrationAttemptResult hydration = await _hydrator.HydrateWithResultAsync(
            item.SourceTicket.Key,
            ct);
        return hydration is HydrationAttemptFailure failure
            ? AuthoringWorkResult.Retry(failure.Reason)
            : AuthoringWorkResult.Complete(receiptId);
    }
}
