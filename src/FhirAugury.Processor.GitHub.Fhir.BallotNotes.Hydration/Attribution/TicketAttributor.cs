using System.Globalization;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
using FhirAugury.Common.WorkGroups;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;

/// <summary>A Jira ticket attributed to a unit within the commit window.</summary>
public sealed record AttributedTicket
{
    public required string Key { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Resolution { get; init; } = string.Empty;
    public string WorkGroup { get; init; } = string.Empty;
    public string Specification { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;

    /// <summary>The ticket's Jira change-impact classification (e.g. <c>Non-substantive</c>); empty when unset.</summary>
    public string ChangeImpact { get; init; } = string.Empty;

    /// <summary>The ticket's Jira change-category classification; empty when unset.</summary>
    public string ChangeCategory { get; init; } = string.Empty;

    /// <summary>The ticket's Jira issue Type, e.g. <c>Technical Correction</c>; empty when unset.</summary>
    public string IssueType { get; init; } = string.Empty;

    /// <summary>
    /// Related/linked Jira ticket keys (semicolon-joined, self excluded) gathered
    /// from the issue's related-issues and duplicate-of links; empty when none.
    /// </summary>
    public string RelatedTicketKeys { get; init; } = string.Empty;

    /// <summary>Number of window commits attributed to this ticket.</summary>
    public int CommitCount { get; init; }

    /// <summary>Latest window-commit date touching this ticket (drives recency selection).</summary>
    public DateTimeOffset AttributionDate { get; init; }
}

/// <summary>The attribution result for one unit's window of commits.</summary>
public sealed record UnitAttribution
{
    public required IReadOnlyList<AttributedTicket> Tickets { get; init; }

    /// <summary>Per-commit attributed ticket keys, keyed by full SHA.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> CommitTicketKeys { get; init; }

    /// <summary>Owning work-group display name (most recently attributed ticket wins).</summary>
    public string WorkGroup { get; init; } = string.Empty;

    /// <summary>Owning work-group code/slug derived from <see cref="WorkGroup"/>.</summary>
    public string WorkGroupCode { get; init; } = string.Empty;
}

/// <summary>
/// Attributes window commits to Jira tickets: canonical Jira keys (FHIR-N,
/// UP-N, UPSM-N, …) extracted from commit messages, enriched best-effort via
/// the orchestrator's cross-referenced endpoint (Jira-source fallback), with
/// per-ticket details fetched the same way. The owning work group is the one on
/// the most recently attributed ticket (recency-primary), falling back to the
/// request hint.
/// </summary>
public sealed class TicketAttributor
{
    private readonly HttpClient _httpClient;
    private readonly BallotNotesHydrationOptions _options;
    private readonly ILogger<TicketAttributor> _logger;

    public TicketAttributor(
        HttpClient httpClient,
        IOptions<BallotNotesHydrationOptions> options,
        ILogger<TicketAttributor> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Extracts distinct, order-preserving canonical Jira keys (e.g. <c>FHIR-N</c>,
    /// <c>UP-N</c>, <c>UPSM-N</c>) using the shared <see cref="JiraTicketExtractor"/>,
    /// which only matches known HL7 project prefixes (so incidental tokens like
    /// <c>ABC-1</c> or <c>UTF-8</c> are not treated as ticket keys).
    /// </summary>
    public static IReadOnlyList<string> ExtractTicketKeys(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        List<string> keys = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (JiraTicketMatch match in JiraTicketExtractor.ExtractTickets(text))
        {
            string key = match.JiraKey.ToUpperInvariant();
            if (seen.Add(key)) keys.Add(key);
        }
        return keys;
    }

    /// <summary>
    /// Selects the owning work group: the most recently attributed ticket's
    /// work group (recency-primary). Falls back to <paramref name="hint"/> when no
    /// attributed ticket carries a work group.
    /// </summary>
    public static (string WorkGroup, string WorkGroupCode) SelectOwningWorkGroup(
        IReadOnlyList<AttributedTicket> tickets,
        string? hint)
    {
        AttributedTicket? best = null;
        foreach (AttributedTicket ticket in tickets)
        {
            if (string.IsNullOrWhiteSpace(ticket.WorkGroup)) continue;
            if (best is null || ticket.AttributionDate > best.AttributionDate)
            {
                best = ticket;
            }
        }

        if (best is not null)
        {
            return (best.WorkGroup, Hl7WorkGroupNameCleaner.Clean(best.WorkGroup));
        }

        string fallback = hint?.Trim() ?? string.Empty;
        return (fallback, Hl7WorkGroupNameCleaner.Clean(fallback));
    }

    /// <summary>
    /// Attributes the supplied commits, returning per-ticket details, per-commit
    /// keys, and the recency-selected owning work group.
    /// </summary>
    public async Task<UnitAttribution> AttributeAsync(
        IReadOnlyList<WindowCommit> commits,
        string? workGroupHint,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commits);

        Dictionary<string, List<string>> commitKeys = new(StringComparer.Ordinal);
        Dictionary<string, DateTimeOffset> commitDates = new(StringComparer.Ordinal);
        Dictionary<string, int> commitCount = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, DateTimeOffset> latest = new(StringComparer.OrdinalIgnoreCase);
        List<string> order = [];
        List<WindowCommit> gapCommits = [];

        foreach (WindowCommit commit in commits)
        {
            List<string> keys = [.. ExtractTicketKeys($"{commit.Subject}\n{commit.Body}")];

            foreach (string crossRef in await TryCrossReferenceAsync(commit.Sha, ct).ConfigureAwait(false))
            {
                if (!keys.Contains(crossRef, StringComparer.OrdinalIgnoreCase))
                {
                    keys.Add(crossRef);
                }
            }

            commitKeys[commit.Sha] = keys;

            DateTimeOffset commitDate = ParseDate(commit.AuthorDate);
            commitDates[commit.Sha] = commitDate;
            if (keys.Count == 0)
            {
                gapCommits.Add(commit);
            }

            foreach (string key in keys)
            {
                string norm = key.ToUpperInvariant();
                if (!commitCount.ContainsKey(norm))
                {
                    commitCount[norm] = 0;
                    order.Add(norm);
                }
                commitCount[norm]++;
                if (!latest.TryGetValue(norm, out DateTimeOffset existing) || commitDate > existing)
                {
                    latest[norm] = commitDate;
                }
            }
        }

        // Pass 2 (gap-fill): for window commits that named no ticket, attribute the
        // ticket(s) on the PR that introduced them — once per PR, dated by the
        // latest contributing commit — merged into the existing bookkeeping so they
        // flow indistinguishably through enrichment and owning-WG selection.
        if (gapCommits.Count > 0)
        {
            foreach (PrTicketHarvest harvest in PrTicketResolver.Resolve(_options.GitHubDbPath, gapCommits, _logger))
            {
                DateTimeOffset prDate = DateTimeOffset.MinValue;
                foreach (string sha in harvest.ContributingShas)
                {
                    if (commitDates.TryGetValue(sha, out DateTimeOffset d) && d > prDate)
                    {
                        prDate = d;
                    }
                }

                foreach (string key in harvest.TicketKeys)
                {
                    string norm = key.ToUpperInvariant();

                    foreach (string sha in harvest.ContributingShas)
                    {
                        if (commitKeys.TryGetValue(sha, out List<string>? shaKeys)
                            && !shaKeys.Contains(norm, StringComparer.OrdinalIgnoreCase))
                        {
                            shaKeys.Add(norm);
                        }
                    }

                    if (!commitCount.ContainsKey(norm))
                    {
                        commitCount[norm] = 0;
                        order.Add(norm);
                    }
                    commitCount[norm]++; // once per PR per decision 2
                    if (!latest.TryGetValue(norm, out DateTimeOffset existing) || prDate > existing)
                    {
                        latest[norm] = prDate;
                    }
                }
            }
        }

        List<AttributedTicket> tickets = [];
        int ticketOrder = 0;
        foreach (string key in order)
        {
            TicketDetails details = await TryGetTicketDetailsAsync(key, ct).ConfigureAwait(false);
            tickets.Add(new AttributedTicket
            {
                Key = key,
                Title = details.Title,
                Resolution = details.Resolution,
                WorkGroup = details.WorkGroup,
                Specification = details.Specification,
                Url = details.Url,
                ChangeImpact = details.ChangeImpact,
                ChangeCategory = details.ChangeCategory,
                IssueType = details.IssueType,
                RelatedTicketKeys = details.RelatedTicketKeys,
                CommitCount = commitCount[key],
                AttributionDate = latest.TryGetValue(key, out DateTimeOffset d) ? d : DateTimeOffset.MinValue,
            });
            ticketOrder++;
        }

        (string workGroup, string workGroupCode) = SelectOwningWorkGroup(tickets, workGroupHint);

        return new UnitAttribution
        {
            Tickets = tickets,
            CommitTicketKeys = commitKeys.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<string>)kv.Value,
                StringComparer.Ordinal),
            WorkGroup = workGroup,
            WorkGroupCode = workGroupCode,
        };
    }

    private static DateTimeOffset ParseDate(string isoDate)
        => DateTimeOffset.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset d)
            ? d
            : DateTimeOffset.MinValue;

    private async Task<IReadOnlyList<string>> TryCrossReferenceAsync(string value, CancellationToken ct)
    {
        using JsonDocument? doc = await TryGetWithFallbackAsync(
            $"/api/v1/content/cross-referenced?value={Uri.EscapeDataString(value)}", ct).ConfigureAwait(false);
        if (doc is null) return [];

        List<string> keys = [];
        if (doc.RootElement.TryGetProperty("hits", out JsonElement hits) && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement hit in hits.EnumerateArray())
            {
                CollectJiraKey(hit, "sourceType", "sourceId", value, keys);
                CollectJiraKey(hit, "targetType", "targetId", value, keys);
            }
        }
        return keys;
    }

    private static void CollectJiraKey(JsonElement hit, string typeProp, string idProp, string excludeValue, List<string> keys)
    {
        if (hit.ValueKind != JsonValueKind.Object) return;
        if (!string.Equals(GetStringCI(hit, typeProp), "jira", StringComparison.OrdinalIgnoreCase)) return;

        string id = GetStringCI(hit, idProp);
        if (string.IsNullOrWhiteSpace(id)) return;
        if (string.Equals(id, excludeValue, StringComparison.OrdinalIgnoreCase)) return;
        if (!ValueFormatDetector.IsJiraKey(id)) return;

        string key = id.ToUpperInvariant();
        if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            keys.Add(key);
        }
    }

    private async Task<TicketDetails> TryGetTicketDetailsAsync(string key, CancellationToken ct)
    {
        using JsonDocument? doc = await TryGetWithFallbackAsync(
            $"/api/v1/content/item/jira/{Uri.EscapeDataString(key)}", ct).ConfigureAwait(false);
        if (doc is null) return TicketDetails.Empty;

        JsonElement root = doc.RootElement;
        string title = GetStringCI(root, "title");
        string url = GetStringCI(root, "url");
        string workGroup = string.Empty, specification = string.Empty, resolution = string.Empty;
        string changeImpact = string.Empty, changeCategory = string.Empty;
        string issueType = string.Empty;
        string relatedTicketKeys = string.Empty;

        if (TryGetObjectCI(root, "metadata", out JsonElement metadata))
        {
            workGroup = GetStringCI(metadata, "work_group");
            specification = GetStringCI(metadata, "specification");
            resolution = GetStringCI(metadata, "resolution");
            changeImpact = GetStringCI(metadata, "change_impact");
            changeCategory = GetStringCI(metadata, "change_category");
            issueType = GetStringCI(metadata, "type");

            // Related/linked tickets come from the related-issues + duplicate-of
            // fields; extract distinct FHIR-keys, excluding the ticket itself.
            List<string> related = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { key.ToUpperInvariant() };
            foreach (string field in new[] { GetStringCI(metadata, "related_issues"), GetStringCI(metadata, "duplicate_of") })
            {
                foreach (string relatedKey in ExtractTicketKeys(field))
                {
                    if (seen.Add(relatedKey)) related.Add(relatedKey);
                }
            }
            relatedTicketKeys = string.Join(";", related);
        }

        return new TicketDetails(title, resolution, workGroup, specification, url, changeImpact, changeCategory, issueType, relatedTicketKeys);
    }

    private async Task<JsonDocument?> TryGetWithFallbackAsync(string relativeUrl, CancellationToken ct)
    {
        foreach (string baseAddress in EnumerateAddresses())
        {
            JsonDocument? doc = await TryGetJsonAsync(baseAddress, relativeUrl, ct).ConfigureAwait(false);
            if (doc is not null) return doc;
        }
        return null;
    }

    private IEnumerable<string> EnumerateAddresses()
    {
        if (!string.IsNullOrWhiteSpace(_options.OrchestratorAddress)) yield return _options.OrchestratorAddress;
        if (!string.IsNullOrWhiteSpace(_options.JiraSourceAddress)) yield return _options.JiraSourceAddress;
    }

    private async Task<JsonDocument?> TryGetJsonAsync(string baseAddress, string relativeUrl, CancellationToken ct)
    {
        try
        {
            Uri uri = new(new Uri(baseAddress, UriKind.Absolute), relativeUrl);
            using HttpResponseMessage response = await _httpClient.GetAsync(uri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort attribution fetch failed for {Url} via {Base}", relativeUrl, baseAddress);
            return null;
        }
    }

    private static string GetStringCI(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return string.Empty;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : string.Empty;
            }
        }
        return string.Empty;
    }

    private static bool TryGetObjectCI(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object) return false;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Object)
            {
                value = property.Value;
                return true;
            }
        }
        return false;
    }

    private readonly record struct TicketDetails(
        string Title,
        string Resolution,
        string WorkGroup,
        string Specification,
        string Url,
        string ChangeImpact,
        string ChangeCategory,
        string IssueType,
        string RelatedTicketKeys)
    {
        public static TicketDetails Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
    }
}
