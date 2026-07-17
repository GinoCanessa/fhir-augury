using FhirAugury.Processing.Jira.Common.Agent;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Planner.Configuration;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Processing;

public sealed class PlannerAgentCommandTokenProvider(IOptions<PlannerOptions> optionsAccessor) : IJiraAgentExtensionTokenProvider
{
    private readonly PlannerOptions _options = optionsAccessor.Value;

    public Task<IReadOnlyDictionary<string, string>> GetTokensAsync(JiraProcessingSourceTicketRecord ticket, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, string> tokens = new Dictionary<string, string>
        {
            ["repoFilters"] = PlannerRepoFilters.RenderJson(_options.RepoFilters),
        };
        return Task.FromResult(tokens);
    }
}
