using System.Net.Http.Json;
using System.Text.Json;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common.Internal;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Hydration.Common;

/// <summary>
/// HTTP-only fetcher that hits the orchestrator's typed proxies and
/// returns neutral <see cref="HydrationBatch"/> row records. Knows
/// nothing about any concrete database type; never throws except for
/// <see cref="OperationCanceledException"/> (per-entity failures are
/// surfaced as <c>unresolved</c> rows).
/// </summary>
public class OrchestratorHydrationFetcher(
    HttpClient httpClient,
    ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Unused today, but stored so that future per-fetch diagnostics
    // (e.g. structured logging at fetch boundaries) can land without
    // a ctor change for all callers.
    private readonly ILogger _logger = logger;

    public virtual async Task<(HydrationTicketRow Parent, List<HydrationJiraXrefRow> XrefRows)> FetchParentAsync(
        string ticketKey, DateTimeOffset hydratedAt, CancellationToken ct)
    {
        List<HydrationJiraXrefRow> xrefRows = [];
        string path = $"api/v1/jira/items/{Uri.EscapeDataString(ticketKey)}?includeContent=true&includeComments=true";
        FetchResult<OrchestratorItemResponse> result = await GetJsonAsync<OrchestratorItemResponse>(path, ct);

        if (result.Reason is not null || result.Value is null)
        {
            return (
                new HydrationTicketRow(
                    TicketKey: ticketKey,
                    Priority: null,
                    Resolution: null,
                    ResolutionDescriptionPlain: null,
                    Specification: null,
                    RaisedInVersion: null,
                    SelectedBallot: null,
                    ChangeCategory: null,
                    Impact: null,
                    Labels: null,
                    CommentCount: null,
                    DescriptionPlain: null,
                    HydratedAt: hydratedAt,
                    HydrationStatus: "unresolved",
                    HydrationReason: result.Reason ?? "empty response"),
                xrefRows);
        }

        Dictionary<string, string> metadata = result.Value.Metadata ?? [];
        int? commentCount = null;
        if (metadata.TryGetValue("comment_count", out string? commentCountValue)
            && int.TryParse(commentCountValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsedCount))
        {
            commentCount = parsedCount;
        }

        HydrationTicketRow parent = new(
            TicketKey: ticketKey,
            Priority: metadata.GetValueOrDefault("priority"),
            Resolution: metadata.GetValueOrDefault("resolution"),
            ResolutionDescriptionPlain: metadata.GetValueOrDefault("resolution_description_plain"),
            Specification: metadata.GetValueOrDefault("specification"),
            RaisedInVersion: metadata.GetValueOrDefault("raised_in_version"),
            SelectedBallot: metadata.GetValueOrDefault("selected_ballot"),
            ChangeCategory: metadata.GetValueOrDefault("change_category"),
            Impact: metadata.GetValueOrDefault("impact"),
            Labels: metadata.GetValueOrDefault("labels"),
            CommentCount: commentCount,
            DescriptionPlain: metadata.GetValueOrDefault("description_plain"),
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);

        AppendXref(ticketKey, metadata.GetValueOrDefault("duplicate_of"), "DuplicateOf", xrefRows);
        AppendXref(ticketKey, metadata.GetValueOrDefault("related_issues"), "RelatedIssues", xrefRows);
        AppendXref(ticketKey, metadata.GetValueOrDefault("related_artifacts"), "RelatedArtifacts", xrefRows);

        return (parent, xrefRows);
    }

    private static void AppendXref(string ticketKey, string? csv, string source, List<HydrationJiraXrefRow> rows)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string token in csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!JiraKeys.IsKey(token))
            {
                continue;
            }

            if (!seen.Add(token))
            {
                continue;
            }

            rows.Add(new HydrationJiraXrefRow(ticketKey, token, source));
        }
    }

    public virtual async Task<HydrationJiraRow> FetchJiraAsync(string ticketKey, string jiraKey, DateTimeOffset hydratedAt, CancellationToken ct)
    {
        string path = $"api/v1/jira/items/{Uri.EscapeDataString(jiraKey)}";
        FetchResult<OrchestratorItemResponse> result = await GetJsonAsync<OrchestratorItemResponse>(path, ct);
        if (result.Reason is not null || result.Value is null)
        {
            return new HydrationJiraRow(
                TicketKey: ticketKey,
                JiraKey: jiraKey,
                Title: null,
                Status: null,
                Type: null,
                Priority: null,
                Resolution: null,
                ResolutionDescriptionPlain: null,
                WorkGroup: null,
                Specification: null,
                UpdatedAt: null,
                Url: null,
                HydratedAt: hydratedAt,
                HydrationStatus: "unresolved",
                HydrationReason: result.Reason ?? "empty response");
        }

        Dictionary<string, string> metadata = result.Value.Metadata ?? [];
        return new HydrationJiraRow(
            TicketKey: ticketKey,
            JiraKey: jiraKey,
            Title: result.Value.Title,
            Status: metadata.GetValueOrDefault("status"),
            Type: metadata.GetValueOrDefault("type"),
            Priority: metadata.GetValueOrDefault("priority"),
            Resolution: metadata.GetValueOrDefault("resolution"),
            ResolutionDescriptionPlain: metadata.GetValueOrDefault("resolution_description_plain"),
            WorkGroup: metadata.GetValueOrDefault("work_group"),
            Specification: metadata.GetValueOrDefault("specification"),
            UpdatedAt: result.Value.UpdatedAt,
            Url: result.Value.Url,
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);
    }

    public virtual async Task<HydrationZulipRow> FetchZulipAsync(string ticketKey, string threadId, DateTimeOffset hydratedAt, CancellationToken ct)
    {
        string[] parts = threadId.Split(':', 2);
        if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
        {
            return new HydrationZulipRow(
                TicketKey: ticketKey,
                ZulipThreadId: threadId,
                StreamId: null,
                StreamName: null,
                Topic: null,
                MessageCount: null,
                FirstMessageAt: null,
                LastMessageAt: null,
                FirstMessageExcerpt: null,
                Url: null,
                HydratedAt: hydratedAt,
                HydrationStatus: "unresolved",
                HydrationReason: "malformed thread id");
        }

        string streamName = parts[0];
        string topic = parts[1];
        string path = $"api/v1/zulip/threads/{Uri.EscapeDataString(streamName)}/{Uri.EscapeDataString(topic)}";
        FetchResult<OrchestratorZulipThreadResponse> result = await GetJsonAsync<OrchestratorZulipThreadResponse>(path, ct);
        if (result.Reason is not null || result.Value is null)
        {
            return new HydrationZulipRow(
                TicketKey: ticketKey,
                ZulipThreadId: threadId,
                StreamId: null,
                StreamName: streamName,
                Topic: topic,
                MessageCount: null,
                FirstMessageAt: null,
                LastMessageAt: null,
                FirstMessageExcerpt: null,
                Url: null,
                HydratedAt: hydratedAt,
                HydrationStatus: "unresolved",
                HydrationReason: result.Reason ?? "empty response");
        }

        return new HydrationZulipRow(
            TicketKey: ticketKey,
            ZulipThreadId: threadId,
            StreamId: result.Value.StreamId,
            StreamName: result.Value.Stream ?? streamName,
            Topic: result.Value.Topic ?? topic,
            MessageCount: result.Value.MessageCount,
            FirstMessageAt: result.Value.FirstMessageAt,
            LastMessageAt: result.Value.LastMessageAt,
            FirstMessageExcerpt: result.Value.FirstMessageExcerpt,
            Url: result.Value.Url,
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);
    }

    public virtual async Task<HydrationGitHubRow> FetchGitHubAsync(string ticketKey, string itemId, DateTimeOffset hydratedAt, CancellationToken ct)
    {
        if (!GitHubItemKey.TryParse(itemId, out ParsedGitHubItemKey parsed))
        {
            return new HydrationGitHubRow(
                TicketKey: ticketKey,
                GitHubItemId: itemId,
                Owner: null,
                Repo: null,
                Number: null,
                Path: null,
                Title: null,
                State: null,
                IsPullRequest: null,
                Labels: null,
                UpdatedAt: null,
                Url: null,
                HydratedAt: hydratedAt,
                HydrationStatus: "unresolved",
                HydrationReason: "malformed item id");
        }

        string path = $"api/v1/github/items/{itemId.Replace("#", "%23")}?includeComments=false";
        FetchResult<OrchestratorItemResponse> result = await GetJsonAsync<OrchestratorItemResponse>(path, ct);
        if (result.Reason is not null || result.Value is null)
        {
            string reason = result.Reason ?? "empty response";
            if (parsed.Path is not null && result.Reason is not null && result.Reason.Contains("404", StringComparison.Ordinal))
            {
                reason = "file path not indexed";
            }

            return new HydrationGitHubRow(
                TicketKey: ticketKey,
                GitHubItemId: itemId,
                Owner: parsed.Owner,
                Repo: parsed.Repo,
                Number: parsed.Number,
                Path: parsed.Path,
                Title: null,
                State: null,
                IsPullRequest: null,
                Labels: null,
                UpdatedAt: null,
                Url: null,
                HydratedAt: hydratedAt,
                HydrationStatus: "unresolved",
                HydrationReason: reason);
        }

        Dictionary<string, string> metadata = result.Value.Metadata ?? [];
        bool? isPullRequest = null;
        if (metadata.TryGetValue("is_pull_request", out string? prValue) && bool.TryParse(prValue, out bool prParsed))
        {
            isPullRequest = prParsed;
        }

        return new HydrationGitHubRow(
            TicketKey: ticketKey,
            GitHubItemId: itemId,
            Owner: parsed.Owner,
            Repo: parsed.Repo,
            Number: parsed.Number,
            Path: parsed.Path,
            Title: result.Value.Title,
            State: metadata.GetValueOrDefault("state"),
            IsPullRequest: isPullRequest,
            Labels: metadata.GetValueOrDefault("labels"),
            UpdatedAt: result.Value.UpdatedAt,
            Url: result.Value.Url,
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);
    }

    public virtual async Task<HydrationRepoRow> FetchRepoAsync(string ticketKey, string repo, DateTimeOffset hydratedAt, CancellationToken ct)
    {
        string[] parts = repo.Split('/', 2);
        if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
        {
            return new HydrationRepoRow(
                TicketKey: ticketKey,
                Repo: repo,
                Description: null,
                WorkGroup: null,
                Specification: null,
                CategoryDetail: null,
                Url: null,
                HydratedAt: hydratedAt,
                HydrationStatus: "unresolved",
                HydrationReason: "malformed repo");
        }

        string path = $"api/v1/github/repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}";
        FetchResult<OrchestratorGitHubRepoResponse> result = await GetJsonAsync<OrchestratorGitHubRepoResponse>(path, ct);
        if (result.Reason is not null || result.Value is null)
        {
            return new HydrationRepoRow(
                TicketKey: ticketKey,
                Repo: repo,
                Description: null,
                WorkGroup: null,
                Specification: null,
                CategoryDetail: null,
                Url: null,
                HydratedAt: hydratedAt,
                HydrationStatus: "unresolved",
                HydrationReason: result.Reason ?? "empty response");
        }

        return new HydrationRepoRow(
            TicketKey: ticketKey,
            Repo: repo,
            Description: result.Value.Description,
            WorkGroup: null,
            Specification: null,
            CategoryDetail: result.Value.Category,
            Url: result.Value.Url ?? $"https://github.com/{repo}",
            HydratedAt: hydratedAt,
            HydrationStatus: "resolved",
            HydrationReason: null);
    }

    private async Task<FetchResult<T>> GetJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
            {
                return new FetchResult<T>(null, $"orchestrator {(int)response.StatusCode}");
            }

            T? value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
            return value is null
                ? new FetchResult<T>(null, "empty response")
                : new FetchResult<T>(value, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FetchResult<T>(null, "orchestrator timeout");
        }
        catch (HttpRequestException ex)
        {
            return new FetchResult<T>(null, $"orchestrator error: {ex.GetType().Name}");
        }
        catch (JsonException ex)
        {
            return new FetchResult<T>(null, $"malformed response: {ex.GetType().Name}");
        }
    }

    private readonly record struct FetchResult<T>(T? Value, string? Reason) where T : class;
}
