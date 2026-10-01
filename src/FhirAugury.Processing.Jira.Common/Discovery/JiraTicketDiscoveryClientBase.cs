using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Discovery;

public abstract class JiraTicketDiscoveryClientBase(
    HttpClient httpClient,
    IOptions<JiraProcessingOptions> optionsAccessor,
    JiraLocalProcessingRequestFactory requestFactory) : IJiraTicketDiscoveryClient, IJiraTicketLabelMatcher
{
    /// <summary>
    /// Page size used when paginating local-processing list responses.
    /// Must match
    /// <c>FhirAugury.Source.Jira.Indexing.JiraLocalProcessingQueryBuilder.DefaultLimit</c>;
    /// the server clamps each response to this many rows.
    /// </summary>
    private const int PageSize = 500;
    private const int MaximumPaginationPasses = 3;

    private static readonly JsonSerializerOptions SelectionSerializerOptions = new(JsonSerializerOptions.Web)
    {
        RespectRequiredConstructorParameters = true,
    };

    private readonly JiraProcessingOptions _options = optionsAccessor.Value;

    protected abstract string LocalProcessingTicketsPath { get; }
    protected abstract string LocalProcessingSelectionTicketsPath { get; }
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
        bool useSelection = filters.HasLabelTextFilters;
        string ticketsPath = useSelection ? LocalProcessingSelectionTicketsPath : LocalProcessingTicketsPath;
        string path = $"{ticketsPath}?type={Uri.EscapeDataString(filters.SourceTicketShape)}";
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
                JiraLocalProcessingListResponse? payload;
                if (useSelection)
                {
                    JiraLocalProcessingSelectionRequest request = requestFactory.CreateSelectionRequest(
                        filters,
                        limit: PageSize,
                        offset: offset,
                        runBacked: runBacked);
                    payload = await ReadSelectionPageAsync(path, request, ct);
                }
                else
                {
                    JiraLocalProcessingListRequest request = requestFactory.CreateListRequest(
                        filters,
                        limit: PageSize,
                        offset: offset,
                        runBacked: runBacked);
                    using HttpResponseMessage response = await httpClient.PostAsJsonAsync(path, request, ct);
                    response.EnsureSuccessStatusCode();
                    payload = await response.Content.ReadFromJsonAsync<JiraLocalProcessingListResponse>(
                        cancellationToken: ct);
                }
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

    public async Task<IReadOnlyList<string>> MatchKeysAsync(
        IReadOnlyList<string> keys,
        ResolvedJiraProcessingFilters filters,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(filters);
        ct.ThrowIfCancellationRequested();
        if (keys.Count == 0)
        {
            return [];
        }

        JiraLocalProcessingSelectionRequest request = requestFactory.CreateLabelMatchRequest(keys, filters);
        HashSet<string> submittedKeys = new(keys, StringComparer.OrdinalIgnoreCase);
        string path = $"{LocalProcessingSelectionTicketsPath}?type={Uri.EscapeDataString(filters.SourceTicketShape)}";
        JiraLocalProcessingListResponse payload = await ReadSelectionPageAsync(path, request, ct);
        return payload.Results
            .Where(ticket => submittedKeys.Contains(ticket.Key))
            .Select(ticket => ticket.Key)
            .ToArray();
    }

    private async Task<JiraLocalProcessingListResponse> ReadSelectionPageAsync(
        string path,
        JiraLocalProcessingSelectionRequest request,
        CancellationToken ct)
    {
        // Retain the current response for disposal even if cancellation interrupts retry backoff.
        HttpResponseMessage? response = null;
        try
        {
            response = await HttpRetryHelper.ExecuteWithRetryAsync(
                async token =>
                {
                    response = await httpClient.PostAsJsonAsync<JiraLocalProcessingSelectionRequest>(
                        path,
                        request,
                        token);
                    return response;
                },
                ct,
                sourceName: "Jira ticket selection");
            response.EnsureSuccessStatusCode();
            JiraLocalProcessingListResponse? payload =
                await response.Content.ReadFromJsonAsync<JiraLocalProcessingListResponse>(
                    SelectionSerializerOptions,
                    ct);
            ValidateSelectionPage(payload, request);
            return payload ?? throw InvalidSelectionResponse("The response must not be null.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (JiraTicketSelectionUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string detail = ex switch
            {
                HttpRequestException { StatusCode: HttpStatusCode.BadRequest } =>
                    "Jira ticket selection was rejected by upstream validation (HTTP 400). Check the criteria and source ticket shape.",
                HttpRequestException { StatusCode: HttpStatusCode.NotFound } =>
                    "Jira ticket selection route is not supported (HTTP 404). Deploy compatible Jira Source and Orchestrator selection endpoints.",
                HttpRequestException { StatusCode: { } status } when HttpRetryHelper.IsTransient(status) =>
                    $"Jira ticket selection remained unavailable after retries (HTTP {(int)status}).",
                HttpRequestException { StatusCode: { } status } =>
                    $"Jira ticket selection failed (HTTP {(int)status}).",
                HttpRequestException =>
                    "Jira ticket selection could not reach the upstream service after retries.",
                OperationCanceledException =>
                    "Jira ticket selection timed out without caller cancellation.",
                JsonException =>
                    "Jira ticket selection returned invalid JSON or omitted required response members.",
                _ => "Jira ticket selection could not complete the upstream request.",
            };
            throw new JiraTicketSelectionUnavailableException(detail, ex);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static void ValidateSelectionPage(
        JiraLocalProcessingListResponse? payload,
        JiraLocalProcessingSelectionRequest request)
    {
        if (payload?.Results is null)
        {
            throw InvalidSelectionResponse("The response and its results must not be null.");
        }

        int limit = request.Limit ?? PageSize;
        int offset = request.Offset ?? 0;
        if (payload.Limit != limit || payload.Offset != offset)
        {
            throw InvalidSelectionResponse(
                $"Expected limit {limit} and offset {offset}; received limit {payload.Limit} and offset {payload.Offset}.");
        }
        if (payload.Total < 0)
        {
            throw InvalidSelectionResponse("The total must not be negative.");
        }

        // Each page is transactional; a later page may observe a smaller total.
        int expectedCount = Math.Min(limit, Math.Max(0, payload.Total - offset));
        if (payload.Results.Count != expectedCount)
        {
            throw InvalidSelectionResponse(
                $"Expected {expectedCount} results for this page, but received {payload.Results.Count}.");
        }

        HashSet<string> returnedKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (JiraIssueSummaryEntry ticket in payload.Results)
        {
            if (ticket is null ||
                string.IsNullOrWhiteSpace(ticket.Key) ||
                ticket.Title is null ||
                ticket.ProjectKey is null ||
                ticket.Type is null ||
                ticket.Status is null ||
                ticket.Priority is null ||
                ticket.WorkGroup is null ||
                ticket.Specification is null)
            {
                throw InvalidSelectionResponse(
                    "Results must contain non-null summaries with nonblank keys and non-null text fields.");
            }
            if (!returnedKeys.Add(ticket.Key))
            {
                throw InvalidSelectionResponse("Results must not contain duplicate ticket keys.");
            }
        }

        if (request.Keys is { } keys &&
            (payload.Total != payload.Results.Count || payload.Results.Count > keys.Count))
        {
            throw InvalidSelectionResponse(
                "A candidate response must be one complete page with no more results than submitted keys.");
        }
    }

    private static JiraTicketSelectionUnavailableException InvalidSelectionResponse(string detail)
        => new(
            $"Jira ticket selection returned an invalid response: {detail}",
            new InvalidDataException(detail));

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
