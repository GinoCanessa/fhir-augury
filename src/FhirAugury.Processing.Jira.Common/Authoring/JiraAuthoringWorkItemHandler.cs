using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Agent;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Authoring;

public class JiraAuthoringWorkItemHandler(
    JiraAgentCommandRenderer commandRenderer,
    IJiraAgentCliRunner runner,
    AuthoringRunStore authoringStore,
    IJiraAgentExtensionTokenProvider extensionTokenProvider,
    IOptions<ProcessingServiceOptions> processingOptions)
    : IAuthoringWorkItemHandler<JiraAuthoringWorkItem>
{
    public async Task<AuthoringWorkResult> ProcessAsync(
        JiraAuthoringWorkItem item,
        AuthoringQueueClaim claim,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(item.RunItem.AcceptedReceiptId))
        {
            AuthoringRunItemRecord claimed = (await authoringStore.GetRunItemsAsync(
                item.RunItem.RunId,
                ct)).Single(value => value.Id == item.RunItem.Id);
            if (!string.Equals(claimed.PostPersistenceLeaseId, claim.OperationId, StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StaleOperation,
                    $"Post-persistence lease '{claim.OperationId}' no longer owns item '{item.RunItem.Id}'.");
            }
            return await OnReceiptAcceptedAsync(item, item.RunItem.AcceptedReceiptId, ct);
        }

        IReadOnlyDictionary<string, string> extensionTokens =
            await extensionTokenProvider.GetTokensAsync(item, ct);
        string callbackUrl =
            $"http://localhost:{processingOptions.Value.Ports.Http}/processing/authoring/runs/" +
            $"{Uri.EscapeDataString(item.RunItem.RunId)}/items/{Uri.EscapeDataString(item.RunItem.Id)}/result";
        JiraAgentCommandContext context = new()
        {
            TicketKey = item.SourceTicket.Key,
            SourceTicketId = item.SourceTicket.Id,
            DatabasePath = processingOptions.Value.DatabasePath,
            SourceTicketShape = item.SourceTicket.SourceTicketShape,
            ExtensionTokens = extensionTokens,
            IsAuthoringWorker = true,
            RunId = item.RunItem.RunId,
            RunItemId = item.RunItem.Id,
            CallbackUrl = callbackUrl,
            OperationId = claim.OperationId,
            OperationToken = claim.OperationToken,
            ExpectedSourceRevision = item.RunItem.ExpectedSourceRevision,
        };
        JiraAgentCommand command = commandRenderer.Render(context);
        JiraAgentResult result = await runner.RunAsync(command, context, ct);
        AuthoringRunItemRecord persisted = (await authoringStore.GetRunItemsAsync(
            item.RunItem.RunId,
            ct)).Single(value => value.Id == item.RunItem.Id);
        if (!string.IsNullOrWhiteSpace(persisted.AcceptedReceiptId))
        {
            if (!string.Equals(persisted.CurrentOperationId, claim.OperationId, StringComparison.Ordinal))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.StaleOperation,
                    $"Operation '{claim.OperationId}' no longer owns item '{item.RunItem.Id}'.");
            }
            return AuthoringWorkResult.Persisted(persisted.AcceptedReceiptId);
        }

        if (ct.IsCancellationRequested || result.Canceled)
        {
            return AuthoringWorkResult.Retry("Authoring worker was canceled.");
        }
        if (result.ExitCode != 0)
        {
            string message = string.IsNullOrWhiteSpace(result.StderrTail)
                ? $"Agent exited with code {result.ExitCode}."
                : result.StderrTail;
            return AuthoringWorkResult.Retry(message);
        }

        return AuthoringWorkResult.Retry(
            "Agent exited successfully without an accepted persistence receipt.");
    }

    protected virtual Task<AuthoringWorkResult> OnReceiptAcceptedAsync(
        JiraAuthoringWorkItem item,
        string receiptId,
        CancellationToken ct)
        => Task.FromResult(AuthoringWorkResult.Complete(receiptId));
}
