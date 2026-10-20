using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common.Internal;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.Jira.Fhir.Hydration.Common;

/// <summary>
/// HTTP-only fetcher that hits the orchestrator's typed proxies and
/// returns neutral <see cref="HydrationBatch"/> row records. Knows
/// nothing about any concrete database type. Expected transport failures are
/// surfaced as <c>unresolved</c> rows; caller cancellation and programming
/// errors propagate to the coordinator.
/// </summary>
public class OrchestratorHydrationFetcher(
    HttpClient httpClient,
    ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger _logger = logger;

    public virtual async Task<PublicationMetadataFetchResult>
        FetchPublicationMetadataAsync(
            string ticketKey,
            DateTimeOffset hydratedAt,
            CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticketKey);
        string path =
            $"api/v1/jira/items/{Uri.EscapeDataString(ticketKey)}";
        FetchResult<OrchestratorItemResponse> result =
            await GetJsonAsync<OrchestratorItemResponse>(path, ct);
        if (result.Value is null)
        {
            return PublicationFailure(
                ticketKey,
                hydratedAt,
                result.StatusCode == HttpStatusCode.NotFound
                    ? PublicationMetadataFetchFailureReason.TicketNotFound
                    : PublicationMetadataFetchFailureReason.SourceUnavailable,
                result.Reason ?? "empty response");
        }

        OrchestratorItemResponse item = result.Value;
        if (string.IsNullOrWhiteSpace(item.Id) ||
            !string.Equals(
                item.Id,
                ticketKey,
                StringComparison.OrdinalIgnoreCase) ||
            item.Title is null)
        {
            return PublicationFailure(
                ticketKey,
                hydratedAt,
                PublicationMetadataFetchFailureReason.InvalidResponse,
                "The Jira item response did not identify the requested ticket.");
        }

        OrchestratorItemPeopleResponse? people = item.People;
        if (people?.PublicDisplayNamePolicyVersion !=
            PublicDisplayNamePolicy.CurrentVersion)
        {
            return PublicationFailure(
                ticketKey,
                hydratedAt,
                PublicationMetadataFetchFailureReason.PeoplePolicyNotCurrent,
                "The Jira item response did not carry the current public display-name policy.");
        }

        OrchestratorSourceReadProvenance? provenance = item.Provenance;
        if (provenance is null ||
            !string.Equals(
                provenance.Source,
                "jira",
                StringComparison.OrdinalIgnoreCase) ||
            provenance.ContentRevision is null)
        {
            return PublicationFailure(
                ticketKey,
                hydratedAt,
                PublicationMetadataFetchFailureReason.MissingSourceProvenance,
                "The Jira item response did not carry complete source provenance.");
        }
        if (provenance.IsStable != true)
        {
            return PublicationFailure(
                ticketKey,
                hydratedAt,
                PublicationMetadataFetchFailureReason.UnstableSource,
                "The Jira item response was not read from a stable source generation.",
                sourceIsStable: false);
        }

        int separator = ticketKey.IndexOf('-', StringComparison.Ordinal);
        if (separator <= 0 ||
            provenance.ProjectLastSuccessfulRefreshAt is null)
        {
            return PublicationFailure(
                ticketKey,
                hydratedAt,
                PublicationMetadataFetchFailureReason.MissingProjectProvenance,
                "The Jira item response did not carry project refresh provenance.",
                sourceIsStable: true);
        }

        string expectedProject = ticketKey[..separator];
        KeyValuePair<string, DateTimeOffset?> projectCoordinate =
            provenance.ProjectLastSuccessfulRefreshAt.FirstOrDefault(
                pair => string.Equals(
                    pair.Key,
                    expectedProject,
                    StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(projectCoordinate.Key) ||
            projectCoordinate.Value is null)
        {
            return PublicationFailure(
                ticketKey,
                hydratedAt,
                PublicationMetadataFetchFailureReason.MissingProjectProvenance,
                $"The Jira item response did not carry a successful refresh for project '{expectedProject}'.",
                sourceIsStable: true);
        }

        Dictionary<string, string> metadata = item.Metadata ?? [];
        string observedSourceRevision = JiraSourceRevision.Compute(
            item.UpdatedAt,
            item.Id,
            item.Title,
            metadata.GetValueOrDefault("status"),
            metadata.GetValueOrDefault("work_group"),
            metadata.GetValueOrDefault("type"),
            metadata.GetValueOrDefault("specification"));
        return new PublicationMetadataFetchResult(
            ticketKey,
            hydratedAt,
            observedSourceRevision,
            NormalizeDisplayName(people.Reporter),
            NormalizeDisplayName(people.Assignee),
            NormalizeDisplayNames(people.InPersonRequesters),
            projectCoordinate.Key,
            projectCoordinate.Value,
            provenance.ContentRevision,
            true,
            PublicDisplayNamePolicy.CurrentVersion,
            Failure: null,
            UpdatedAt: item.UpdatedAt);
    }

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
        OrchestratorItemPeopleResponse? people =
            ReadTrustedPeople(result.Value.People);
        (
            string? sourceProject,
            DateTimeOffset? sourceLastSuccessfulRefreshAt,
            long? sourceContentRevision,
            bool? sourceIsStable) = ReadJiraProvenance(
                ticketKey,
                result.Value.Provenance);
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
            HydrationReason: null,
            DescriptionHtml: result.Value.Content,
            ResolutionDescriptionHtml: metadata.GetValueOrDefault("resolution_description"),
            Reporter: metadata.GetValueOrDefault("reporter"),
            CreatedAt: result.Value.CreatedAt,
            RelatedArtifactsRaw: metadata.GetValueOrDefault("related_artifacts"),
            RelatedPagesRaw: metadata.GetValueOrDefault("related_pages"),
            Assignee: NormalizeDisplayName(people?.Assignee),
            InPersonRequesters: NormalizeDisplayNames(people?.InPersonRequesters),
            SourceProject: sourceProject,
            SourceLastSuccessfulRefreshAt: sourceLastSuccessfulRefreshAt,
            SourceContentRevision: sourceContentRevision,
            SourceIsStable: sourceIsStable,
            StructuredReporter: NormalizeDisplayName(people?.Reporter),
            PublicDisplayNamePolicyVersion:
                people?.PublicDisplayNamePolicyVersion);

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
        string path = $"api/v1/jira/items/{Uri.EscapeDataString(jiraKey)}?includeContent=true";
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
        OrchestratorItemPeopleResponse? people =
            ReadTrustedPeople(result.Value.People);
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
            HydrationReason: null,
            DescriptionHtml: result.Value.Content,
            ResolutionDescriptionHtml: metadata.GetValueOrDefault("resolution_description"),
            Reporter: metadata.GetValueOrDefault("reporter"),
            CreatedAt: result.Value.CreatedAt,
            RelatedArtifactsRaw: metadata.GetValueOrDefault("related_artifacts"),
            RelatedPagesRaw: metadata.GetValueOrDefault("related_pages"),
            Assignee: NormalizeDisplayName(people?.Assignee),
            StructuredReporter: NormalizeDisplayName(people?.Reporter),
            PublicDisplayNamePolicyVersion:
                people?.PublicDisplayNamePolicyVersion);
    }

    public virtual async Task<HydrationZulipRow> FetchZulipAsync(string ticketKey, string threadId, DateTimeOffset hydratedAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string path = $"api/v1/zulip/references/resolve?reference={Uri.EscapeDataString(threadId)}";
        ZulipFetchResult result = await GetZulipReferenceAsync(path, threadId, ct);
        ct.ThrowIfCancellationRequested();
        if (result.Outcome != ZulipReferenceLookupOutcome.Resolved || result.Diagnostics.Count > 0)
        {
            _logger.LogWarning(
                "Zulip reference lookup outcome {Outcome}; HTTP {StatusCode}; diagnostics {DiagnosticCodes}",
                result.Outcome, (int?)result.StatusCode, string.Join(",", result.Diagnostics));
        }

        ZulipReferenceResolutionResponse? value = result.Value;
        return new HydrationZulipRow(
            TicketKey: ticketKey,
            ZulipThreadId: threadId,
            StreamId: value?.StreamId,
            StreamName: value?.StreamName,
            Topic: value?.Topic,
            MessageCount: value?.MessageCount,
            FirstMessageAt: value?.FirstMessageAt?.ToUniversalTime(),
            LastMessageAt: value?.LastMessageAt?.ToUniversalTime(),
            FirstMessageExcerpt: value?.FirstMessageExcerpt,
            Url: value?.Url,
            HydratedAt: hydratedAt,
            HydrationStatus: value is null ? "unresolved" : "resolved",
            HydrationReason: ZulipReferenceHydrationReason.Serialize(new ZulipReferenceHydrationOutcome
            {
                Backing = value is null ? ZulipReferenceBacking.None : ZulipReferenceBacking.TypedResolver,
                LatestOutcome = result.Outcome,
                Diagnostics = result.Diagnostics,
            }));
    }

    private async Task<ZulipFetchResult> GetZulipReferenceAsync(string path, string reference, CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
                httpClient, path, ct, sourceName: "orchestrator");
            if (!response.IsSuccessStatusCode)
            {
                // HTTP failures remain failures even if an intermediary supplies
                // HTML, an empty body, or an unrelated JSON error document.
                ZulipFetchResult failure = ZulipHttpFailure(response.StatusCode);
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable
                    && response.Content.Headers.ContentLength != 0)
                {
                    try
                    {
                        ZulipReferenceResolutionResponse? error =
                            await response.Content.ReadFromJsonAsync<ZulipReferenceResolutionResponse>(JsonOptions, ct);
                        if (IsSourceResolutionFailure(error, reference, response.StatusCode))
                            return new(null, error!.Outcome!.Value, error.Diagnostics, response.StatusCode);
                    }
                    catch (JsonException)
                    {
                        ct.ThrowIfCancellationRequested();
                    }
                }
                return failure;
            }

            if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
                return new(null, ZulipReferenceLookupOutcome.InvalidEnvelope, [], response.StatusCode);

            ZulipReferenceResolutionResponse? value =
                await response.Content.ReadFromJsonAsync<ZulipReferenceResolutionResponse>(JsonOptions, ct);
            if (value is null || !ZulipReferenceContract.IsCoherentResolution(value, reference))
                return new(null, ZulipReferenceLookupOutcome.InvalidEnvelope, [], response.StatusCode);

            return new(value, ZulipReferenceLookupOutcome.Resolved, value.Diagnostics, response.StatusCode);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, ZulipReferenceLookupOutcome.Timeout, []);
        }
        catch (HttpRequestException ex)
        {
            ct.ThrowIfCancellationRequested();
            return ex.StatusCode is { } status
                ? ZulipHttpFailure(status)
                : new(null, ZulipReferenceLookupOutcome.SourceUnavailable, []);
        }
        catch (IOException)
        {
            ct.ThrowIfCancellationRequested();
            return new(null, ZulipReferenceLookupOutcome.SourceUnavailable, []);
        }
        catch (JsonException)
        {
            ct.ThrowIfCancellationRequested();
            return new(null, ZulipReferenceLookupOutcome.InvalidJson, []);
        }
    }

    private static bool IsSourceResolutionFailure(
        ZulipReferenceResolutionResponse? response, string reference, HttpStatusCode status) =>
        response is not null
        && string.Equals(response.Reference, reference, StringComparison.Ordinal)
        && response.Outcome is ZulipReferenceLookupOutcome.InvalidReference
            or ZulipReferenceLookupOutcome.UnsupportedReference
            or ZulipReferenceLookupOutcome.AmbiguousReference
            or ZulipReferenceLookupOutcome.InvalidSourceContext
            or ZulipReferenceLookupOutcome.SourceUnavailable
        && ZulipReferenceContract.HttpStatus(response.Outcome.Value) == (int)status
        && response.Kind is null && response.Url is null && response.MessageId is null
        && response.StreamId is null && response.StreamName is null && response.Topic is null
        && response.MessageCount is null && response.FirstMessageExcerpt is null
        && response.FirstMessageAt is null && response.LastMessageAt is null
        && response.Diagnostics is not null && response.Diagnostics.All(Enum.IsDefined);

    private static ZulipFetchResult ZulipHttpFailure(HttpStatusCode status)
    {
        ZulipReferenceLookupOutcome outcome = status switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone => ZulipReferenceLookupOutcome.NotFound,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ZulipReferenceLookupOutcome.AuthenticationFailed,
            HttpStatusCode.BadRequest => ZulipReferenceLookupOutcome.InvalidReference,
            _ when HttpRetryHelper.IsTransient(status) || (int)status >= 500 => ZulipReferenceLookupOutcome.TransientFailure,
            _ => ZulipReferenceLookupOutcome.HttpFailure,
        };
        return new(null, outcome, [], status);
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
            using HttpResponseMessage response = await HttpRetryHelper.GetWithRetryAsync(
                httpClient,
                path,
                ct,
                sourceName: "orchestrator");
            if (!response.IsSuccessStatusCode)
            {
                return new FetchResult<T>(
                    null,
                    $"orchestrator {(int)response.StatusCode}",
                    response.StatusCode);
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
        catch (HttpRequestException ex) when (ex.StatusCode is { } statusCode && HttpRetryHelper.IsAuthFailure(statusCode))
        {
            return new FetchResult<T>(
                null,
                $"orchestrator {(int)statusCode}",
                statusCode);
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

    private static (
        string? SourceProject,
        DateTimeOffset? SourceLastSuccessfulRefreshAt,
        long? SourceContentRevision,
        bool? SourceIsStable) ReadJiraProvenance(
            string ticketKey,
            OrchestratorSourceReadProvenance? provenance)
    {
        if (provenance is null)
        {
            return (null, null, null, null);
        }

        if (provenance.IsStable != true ||
            provenance.ContentRevision is null ||
            !string.Equals(
                provenance.Source,
                "jira",
                StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, null, false);
        }

        int separator = ticketKey.IndexOf('-', StringComparison.Ordinal);
        if (separator <= 0 ||
            provenance.ProjectLastSuccessfulRefreshAt is null)
        {
            return (null, null, null, null);
        }

        string expectedProject = ticketKey[..separator];
        foreach ((string project, DateTimeOffset? refreshAt) in
                 provenance.ProjectLastSuccessfulRefreshAt)
        {
            if (string.Equals(
                    project,
                    expectedProject,
                    StringComparison.OrdinalIgnoreCase))
            {
                return (
                    project,
                    refreshAt,
                    provenance.ContentRevision,
                    true);
            }
        }

        return (null, null, null, null);
    }

    private static OrchestratorItemPeopleResponse? ReadTrustedPeople(
        OrchestratorItemPeopleResponse? people)
        => people?.PublicDisplayNamePolicyVersion ==
            PublicDisplayNamePolicy.CurrentVersion
                ? people
                : null;

    private static string? NormalizeDisplayName(string? value)
        => PublicDisplayNamePolicy.Normalize(value);

    private static IReadOnlyList<string> NormalizeDisplayNames(
        IReadOnlyList<string>? values)
        => values is null
            ? []
            : values
                .Select(NormalizeDisplayName)
                .OfType<string>()
                .Order(StringComparer.Ordinal)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value, StringComparer.Ordinal)
                .ToArray();

    private static PublicationMetadataFetchResult PublicationFailure(
        string ticketKey,
        DateTimeOffset hydratedAt,
        PublicationMetadataFetchFailureReason reason,
        string detail,
        bool? sourceIsStable = null)
        => new(
            ticketKey,
            hydratedAt,
            ObservedSourceRevision: null,
            Reporter: null,
            Assignee: null,
            InPersonRequesters: [],
            SourceProject: null,
            SourceLastSuccessfulRefreshAt: null,
            SourceContentRevision: null,
            SourceIsStable: sourceIsStable,
            PublicDisplayNamePolicyVersion: null,
            Failure: new PublicationMetadataFetchFailure(reason, detail));

    private readonly record struct FetchResult<T>(
        T? Value,
        string? Reason,
        HttpStatusCode? StatusCode = null) where T : class;

    private readonly record struct ZulipFetchResult(
        ZulipReferenceResolutionResponse? Value,
        ZulipReferenceLookupOutcome Outcome,
        IReadOnlyList<ZulipReferenceDiagnosticCode> Diagnostics,
        HttpStatusCode? StatusCode = null);
}
