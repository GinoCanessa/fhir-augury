using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Discovery;

public sealed class OrchestratorJiraTicketDiscoveryClient(
    HttpClient httpClient,
    IOptions<JiraProcessingOptions> optionsAccessor,
    JiraLocalProcessingRequestFactory requestFactory)
    : JiraTicketDiscoveryClientBase(httpClient, optionsAccessor, requestFactory)
{
    protected override string LocalProcessingTicketsPath => "api/v1/jira/local-processing/tickets";
    protected override string ItemPathPrefix => "api/v1/jira/items";
    protected override string SetProcessedPath => "api/v1/jira/local-processing/set-processed";
}
