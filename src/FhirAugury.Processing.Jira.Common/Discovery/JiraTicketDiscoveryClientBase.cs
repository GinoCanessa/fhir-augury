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
    /// <summary>
    /// Page size used when paginating local-processing list responses.
    /// Must match
    /// <c>FhirAugury.Source.Jira.Indexing.JiraLocalProcessingQueryBuilder.DefaultLimit</c>;
    /// the server clamps each response to this many rows.
    /// </summary>
    private const int PageSize = 500;
    private const int MaximumPaginationPasses = 3;

    private readonly JiraProcessingOptions _options = optionsAccessor.Value;

    protected abstract string LocalProcessingTicketsPath { get; }
    protected abstract string ItemPathPrefix { get; }
    protected abstract string SetProcessedPath { get; }

    public async Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsAsync(ResolvedJiraProcessingFilters filters, CancellationToken ct)
        => (await ListTicketsWithProvenanceAsync(filters, ct)).Tickets;

    public async Task<IReadOnlyList<JiraIssueSummaryEntry>> ListTicketsForModeAsync(
        ResolvedJiraProcessingFilters filters,
        bool runBacked,
        CancellationToken ct)
        => (await ListTicketsForModeWithProvenanceAsync(
            filters,
            runBacked,
            ct)).Tickets;

    public Task<JiraTicketDiscoveryBatch> ListTicketsWithProvenanceAsync(
        ResolvedJiraProcessingFilters filters,
        CancellationToken ct)
        => ListTicketsForModeWithProvenanceAsync(
            filters,
            runBacked: false,
            ct);

    public async Task<JiraTicketDiscoveryBatch>
        ListTicketsForModeWithProvenanceAsync(
            ResolvedJiraProcessingFilters filters,
            bool runBacked,
            CancellationToken ct)
    {
        string path = $"{LocalProcessingTicketsPath}?type={Uri.EscapeDataString(filters.SourceTicketShape)}";
        for (int pass = 0; pass < MaximumPaginationPasses; pass++)
        {
            List<JiraIssueSummaryEntry> aggregate = [];
            SourceReadProvenance? baseline = null;
            Dictionary<string, DateTimeOffset?> projectWatermarks =
                new(StringComparer.OrdinalIgnoreCase);
            bool passIsStable = true;
            bool restart = false;

            for (int offset = 0; ; offset += PageSize)
            {
                JiraLocalProcessingListRequest request = requestFactory.CreateListRequest(
                    filters,
                    limit: PageSize,
                    offset: offset,
                    runBacked: runBacked);
                using HttpResponseMessage response = await httpClient.PostAsJsonAsync(path, request, ct);
                response.EnsureSuccessStatusCode();
                JiraLocalProcessingListResponse? payload =
                    await response.Content.ReadFromJsonAsync<JiraLocalProcessingListResponse>(
                        cancellationToken: ct);
                SourceReadProvenance? pageProvenance = payload?.Provenance;
                IReadOnlyDictionary<string, DateTimeOffset?>? pageWatermarks =
                    pageProvenance?.ProjectLastSuccessfulRefreshAt;
                bool pageIsStable =
                    pageProvenance is { IsStable: true } &&
                    string.Equals(
                        pageProvenance.Source,
                        "jira",
                        StringComparison.OrdinalIgnoreCase) &&
                    pageWatermarks is not null &&
                    (baseline is null ||
                     baseline.ContentRevision == pageProvenance.ContentRevision);
                if (pageIsStable &&
                    pageProvenance is not null &&
                    pageWatermarks is not null)
                {
                    baseline ??= pageProvenance;
                    foreach ((string project, DateTimeOffset? watermark) in
                             pageWatermarks)
                    {
                        if (projectWatermarks.TryGetValue(
                                project,
                                out DateTimeOffset? existing) &&
                            existing != watermark)
                        {
                            pageIsStable = false;
                            break;
                        }
                        projectWatermarks[project] = watermark;
                    }
                }

                if (!pageIsStable)
                {
                    passIsStable = false;
                    if (pass < MaximumPaginationPasses - 1)
                    {
                        restart = true;
                        break;
                    }
                }

                IReadOnlyList<JiraIssueSummaryEntry>? pageResults =
                    payload?.Results;
                if (pageResults is null)
                {
                    break;
                }

                aggregate.AddRange(pageResults);
                if (pageResults.Count < PageSize)
                {
                    break;
                }
            }

            if (restart)
            {
                continue;
            }

            SourceReadProvenance? provenance =
                passIsStable && baseline is not null
                    ? CreateRepresentedProvenance(
                        baseline,
                        projectWatermarks,
                        aggregate.Select(ticket => ticket.ProjectKey))
                    : null;
            return new JiraTicketDiscoveryBatch(aggregate, provenance);
        }

        throw new InvalidOperationException(
            "Jira pagination did not complete within the configured pass limit.");
    }

    public async Task<JiraIssueSummaryEntry?> GetTicketAsync(string key, string sourceTicketShape, CancellationToken ct)
        => (await GetTicketWithProvenanceAsync(
            key,
            sourceTicketShape,
            ct))?.Ticket;

    public async Task<JiraTicketDiscoveryItem?> GetTicketWithProvenanceAsync(
        string key,
        string sourceTicketShape,
        CancellationToken ct)
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
        if (item is null)
        {
            return null;
        }

        JiraIssueSummaryEntry ticket = JiraItemResponseMapper.Map(item);
        SourceReadProvenance? provenance =
            item.Provenance is { IsStable: true } itemProvenance &&
            string.Equals(
                itemProvenance.Source,
                "jira",
                StringComparison.OrdinalIgnoreCase) &&
            itemProvenance.ProjectLastSuccessfulRefreshAt is not null
                ? CreateRepresentedProvenance(
                    itemProvenance,
                    itemProvenance.ProjectLastSuccessfulRefreshAt,
                    [ticket.ProjectKey])
                : null;
        return new JiraTicketDiscoveryItem(ticket, provenance);
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

    private static SourceReadProvenance CreateRepresentedProvenance(
        SourceReadProvenance source,
        IReadOnlyDictionary<string, DateTimeOffset?> projectWatermarks,
        IEnumerable<string> representedProjects)
    {
        Dictionary<string, DateTimeOffset?> represented =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string project in representedProjects)
        {
            if (string.IsNullOrWhiteSpace(project) ||
                represented.ContainsKey(project))
            {
                continue;
            }

            DateTimeOffset? watermark = null;
            foreach ((string candidate, DateTimeOffset? value) in projectWatermarks)
            {
                if (string.Equals(
                        candidate,
                        project,
                        StringComparison.OrdinalIgnoreCase))
                {
                    watermark = value;
                    break;
                }
            }
            represented.Add(project, watermark);
        }

        return new SourceReadProvenance
        {
            Source = source.Source,
            ContentRevision = source.ContentRevision,
            IsStable = true,
            ProjectLastSuccessfulRefreshAt = represented,
        };
    }
}
