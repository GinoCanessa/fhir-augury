using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Authoring;

namespace FhirAugury.Processing.Jira.Common.Agent;

public interface IJiraAgentExtensionTokenProvider
{
    Task<IReadOnlyDictionary<string, string>> GetTokensAsync(JiraProcessingSourceTicketRecord ticket, CancellationToken ct);

    Task<IReadOnlyDictionary<string, string>> GetTokensAsync(
        JiraAuthoringWorkItem item,
        CancellationToken ct)
        => GetTokensAsync(item.SourceTicket, ct);
}

public sealed class EmptyJiraAgentExtensionTokenProvider : IJiraAgentExtensionTokenProvider
{
    public Task<IReadOnlyDictionary<string, string>> GetTokensAsync(JiraProcessingSourceTicketRecord ticket, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
}
