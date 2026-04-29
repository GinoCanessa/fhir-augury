using System.Diagnostics;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processor.Jira.Fhir.Planner.Configuration;
using FhirAugury.Processor.Jira.Fhir.Planner.Database;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Processing;

public sealed class PlannerTicketHandler(
    JiraAgentCommandRenderer commandRenderer,
    IJiraAgentCliRunner runner,
    JiraProcessingSourceTicketStore store,
    IJiraTicketDiscoveryClient discoveryClient,
    IJiraAgentExtensionTokenProvider extensionTokenProvider,
    PlannerDatabase database,
    IOptions<ProcessingServiceOptions> processingOptions,
    IOptions<PlannerOptions> plannerOptions,
    ILogger<PlannerTicketHandler> logger) : IProcessingWorkItemHandler<JiraProcessingSourceTicketRecord>
{
    public async Task ProcessAsync(JiraProcessingSourceTicketRecord item, CancellationToken ct)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyDictionary<string, string> extensionTokens = await extensionTokenProvider.GetTokensAsync(item, ct);
        string dbPath = Path.GetFullPath(processingOptions.Value.DatabasePath);
        JiraAgentCommandContext context = new()
        {
            TicketKey = item.Key,
            SourceTicketId = item.Id,
            DatabasePath = dbPath,
            SourceTicketShape = item.SourceTicketShape,
            ExtensionTokens = extensionTokens,
        };
        JiraAgentCommand command = commandRenderer.Render(context);
        IReadOnlyList<string> normalizedRepoFilters = PlannerRepoFilters.Normalize(plannerOptions.Value.RepoFilters);
        logger.LogInformation("Starting ticket-plan agent for {TicketKey} with {RepoFilterCount} repo filters", item.Key, normalizedRepoFilters.Count);
        await database.DeletePlanForTicketAsync(item.Key, ct);

        JiraAgentResult result;
        try
        {
            result = await runner.RunAsync(command, context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await database.DeletePlanForTicketAsync(item.Key, CancellationToken.None);
            await MarkErrorAsync(item, $"Agent command failed: {ex.Message}", null, ct);
            throw new InvalidOperationException($"Agent command failed for {item.Key}: {ex.Message}", ex);
        }

        if (ct.IsCancellationRequested || result.Canceled)
        {
            string message = $"Agent run for {item.Key} was canceled.";
            if (!ct.IsCancellationRequested)
            {
                await database.DeletePlanForTicketAsync(item.Key, CancellationToken.None);
                await MarkErrorAsync(item, message, result.ExitCode, CancellationToken.None);
            }

            throw new OperationCanceledException(message, ct);
        }

        if (result.ExitCode != 0)
        {
            await database.DeletePlanForTicketAsync(item.Key, ct);
            string message = string.IsNullOrWhiteSpace(result.StderrTail) ? $"Agent exited with code {result.ExitCode}." : result.StderrTail;
            await MarkErrorAsync(item, message, result.ExitCode, ct);
            logger.LogWarning("ticket-plan agent failed for {TicketKey} in {ElapsedMs} ms with exit code {ExitCode}", item.Key, stopwatch.Elapsed.TotalMilliseconds, result.ExitCode);
            throw new InvalidOperationException(message);
        }

        await store.MarkCompleteAsync(item, DateTimeOffset.UtcNow, ct);
        await discoveryClient.MarkProcessedAsync(item.Key, item.SourceTicketShape, ct);
        logger.LogInformation("Completed ticket-plan agent for {TicketKey} in {ElapsedMs} ms with exit code {ExitCode}", item.Key, stopwatch.Elapsed.TotalMilliseconds, result.ExitCode);
    }

    private Task MarkErrorAsync(JiraProcessingSourceTicketRecord item, string message, int? exitCode, CancellationToken ct) =>
        store.MarkErrorAsync(item, message, exitCode, DateTimeOffset.UtcNow, ct);
}
