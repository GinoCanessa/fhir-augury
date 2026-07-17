using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Processing;

public sealed class JiraTicketProcessingHandler(
    JiraAgentCommandRenderer commandRenderer,
    IJiraAgentCliRunner runner,
    JiraProcessingSourceTicketStore store,
    IJiraTicketDiscoveryClient discoveryClient,
    IJiraAgentExtensionTokenProvider extensionTokenProvider,
    IOptions<ProcessingServiceOptions> processingOptions) : IProcessingWorkItemHandler<JiraProcessingSourceTicketRecord>
{
    public async Task ProcessAsync(JiraProcessingSourceTicketRecord item, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> extensionTokens = await extensionTokenProvider.GetTokensAsync(item, ct);
        JiraAgentCommandContext context = new()
        {
            TicketKey = item.Key,
            SourceTicketId = item.Id,
            DatabasePath = processingOptions.Value.DatabasePath,
            SourceTicketShape = item.SourceTicketShape,
            ExtensionTokens = extensionTokens,
        };
        JiraAgentCommand command = commandRenderer.Render(context);
        JiraAgentResult result = await runner.RunAsync(command, context, ct);
        if (ct.IsCancellationRequested || result.Canceled)
        {
            return;
        }

        if (result.ExitCode == 0)
        {
            await store.MarkCompleteAsync(item, DateTimeOffset.UtcNow, ct);
            await discoveryClient.MarkProcessedAsync(item.Key, item.SourceTicketShape, ct);
            return;
        }

        string message = string.IsNullOrWhiteSpace(result.StderrTail) ? $"Agent exited with code {result.ExitCode}." : result.StderrTail;
        await store.MarkErrorAsync(item, message, result.ExitCode, DateTimeOffset.UtcNow, ct);
        throw new InvalidOperationException(message);
    }
}
