using System.Net.Http.Json;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Discovery;

public abstract class JiraTicketDiscoveryClientBase(
    HttpClient httpClient,
    IOptions<JiraProcessingOptions> optionsAccessor,
    JiraLocalProcessingRequestFactory requestFactory) : IJiraTicketDiscoveryClient
{
    private readonly JiraProcessingOptions _options = optionsAccessor.Value;

    protected abstract string LocalProcessingTicketsPath { get; }
    protected abstract string ItemPathPrefix { get; }
    protected abstract string SetProcessedPath { get; }

    public async Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsAsync(ResolvedJiraProcessingFilters filters, CancellationToken ct)
    {
        JiraLocalProcessingListRequest request = requestFactory.CreateListRequest(filters);
        string path = $"{LocalProcessingTicketsPath}?type={Uri.EscapeDataString(filters.SourceTicketShape)}";
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(path, request, ct);
        response.EnsureSuccessStatusCode();
        JiraLocalProcessingListResponse? payload = await response.Content.ReadFromJsonAsync<JiraLocalProcessingListResponse>(cancellationToken: ct);
        return payload?.Results ?? [];
    }

    public async Task<JiraIssueSummaryEntry?> GetTicketAsync(string key, string sourceTicketShape, CancellationToken ct)
    {
        if (!string.Equals(sourceTicketShape, "fhir", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Source ticket shape '{sourceTicketShape}' is not supported in v1.");
        }

        string path = $"{ItemPathPrefix}/{Uri.EscapeDataString(key)}";
        using HttpResponseMessage response = await httpClient.GetAsync(path, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        ItemResponse? item = await response.Content.ReadFromJsonAsync<ItemResponse>(cancellationToken: ct);
        return item is null ? null : JiraItemResponseMapper.Map(item);
    }

    public async Task MarkProcessedAsync(string key, string sourceTicketShape, CancellationToken ct)
    {
        if (!_options.MarkUpstreamProcessedOnSuccess)
        {
            return;
        }

        JiraLocalProcessingSetRequest request = new() { Key = key, ProcessedLocally = true };
        string path = $"{SetProcessedPath}?type={Uri.EscapeDataString(sourceTicketShape)}";
        using HttpResponseMessage response = await httpClient.PostAsJsonAsync(path, request, ct);
        response.EnsureSuccessStatusCode();
    }
}
