using FhirAugury.Processing.Jira.Common.Database.Records;

namespace FhirAugury.Processing.Jira.Common.Agent;

public interface IJiraAgentExtensionTokenProvider
{
    Task<IReadOnlyDictionary<string, string>> GetTokensAsync(JiraProcessingSourceTicketRecord ticket, CancellationToken ct);
}

public sealed class EmptyJiraAgentExtensionTokenProvider : IJiraAgentExtensionTokenProvider
{
    public Task<IReadOnlyDictionary<string, string>> GetTokensAsync(JiraProcessingSourceTicketRecord ticket, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
}
