using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Discovery;

public sealed class DirectJiraTicketDiscoveryClient(
    HttpClient httpClient,
    IOptions<JiraProcessingOptions> optionsAccessor,
    JiraLocalProcessingRequestFactory requestFactory)
    : JiraTicketDiscoveryClientBase(httpClient, optionsAccessor, requestFactory)
{
    protected override string LocalProcessingTicketsPath => "api/v1/local-processing/tickets";
    protected override string ItemPathPrefix => "api/v1/items";
    protected override string SetProcessedPath => "api/v1/local-processing/set-processed";
}
