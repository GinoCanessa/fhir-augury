using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Processing;

public sealed class FhirTicketPrepHandler(
    JiraAgentCommandRenderer commandRenderer,
    IJiraAgentCliRunner runner,
    JiraProcessingSourceTicketStore store,
    IJiraTicketDiscoveryClient discoveryClient,
    PreparerDatabase database,
    IOptions<ProcessingServiceOptions> processingOptions,
    ILogger<FhirTicketPrepHandler> logger) : IProcessingWorkItemHandler<JiraProcessingSourceTicketRecord>
{
    public async Task ProcessAsync(JiraProcessingSourceTicketRecord item, CancellationToken ct)
    {
        JiraAgentCommandContext context = new()
        {
            TicketKey = item.Key,
            SourceTicketId = item.Id,
            DatabasePath = processingOptions.Value.DatabasePath,
            SourceTicketShape = item.SourceTicketShape,
            ExtensionTokens = new Dictionary<string, string>(),
        };
        JiraAgentCommand command = commandRenderer.Render(context);
        logger.LogInformation("Starting ticket-prep agent for {TicketKey}", item.Key);
        JiraAgentResult result;
        try
        {
            result = await runner.RunAsync(command, context, ct);
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

        bool persisted = await database.PreparedTicketExistsAsync(item.Key, ct);
        if (!persisted)
        {
            string message = $"Agent completed but did not persist prepared_tickets row for {item.Key}.";
            await MarkErrorAsync(item, message, result.ExitCode, ct);
            throw new InvalidOperationException(message);
        }

        DateTimeOffset completedAt = DateTimeOffset.UtcNow;
        await store.MarkCompleteAsync(item, completedAt, ct);
        await discoveryClient.MarkProcessedAsync(item.Key, item.SourceTicketShape, ct);
        logger.LogInformation("Completed ticket-prep agent for {TicketKey} in {ElapsedMs} ms", item.Key, result.Elapsed.TotalMilliseconds);
    }

    private Task MarkErrorAsync(JiraProcessingSourceTicketRecord item, string message, int? exitCode, CancellationToken ct) =>
        store.MarkErrorAsync(item, message, exitCode, DateTimeOffset.UtcNow, ct);
}
