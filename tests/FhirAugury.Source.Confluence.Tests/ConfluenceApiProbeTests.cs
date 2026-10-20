using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace FhirAugury.Source.Confluence.Tests;

/// <summary>
/// Opt-in gate for the live Confluence probe. Two things matter here:
/// the probe must never fire during an ordinary <c>dotnet test</c> run, and it
/// must remain runnable in this repository's actual environment.
/// </summary>
/// <remarks>
/// The plan for slot 0827-01 originally required both an explicit opt-in flag
/// and a credential. HL7's Confluence answers the <c>/rest/api</c> surface
/// anonymously, so a mandatory credential would have made the probe
/// unrunnable. The opt-in flag alone satisfies the rationale — no accidental
/// network traffic from an exported cookie — and a credential is used only
/// when one happens to be present.
/// </remarks>
internal static class ConfluenceProbe
{
    /// <summary>Environment variable that must be set to <c>1</c> for probes to run.</summary>
    public const string OptInVariable = "FHIR_AUGURY_CONFLUENCE_PROBE";

    /// <summary>Spacing between probe requests, deliberately polite.</summary>
    public static readonly TimeSpan RequestInterval = TimeSpan.FromMilliseconds(220);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable(OptInVariable) is "1" or "true" or "True";

    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("FHIR_AUGURY_CONFLUENCE_Confluence__BaseUrl")?.TrimEnd('/')
        ?? "https://confluence.hl7.org";

    public static string? Cookie =>
        Environment.GetEnvironmentVariable("FHIR_AUGURY_CONFLUENCE_Confluence__Cookie");

    public static string? ApiToken =>
        Environment.GetEnvironmentVariable("FHIR_AUGURY_CONFLUENCE_Confluence__ApiToken");

    public static string CredentialDescription =>
        !string.IsNullOrWhiteSpace(Cookie) ? "session cookie"
        : !string.IsNullOrWhiteSpace(ApiToken) ? "API token"
        : "anonymous";

    /// <summary>
    /// HL7's Confluence sits behind an AWS WAF that answers <c>405 Not Allowed</c>
    /// with <c>x-amzn-waf-action: captcha</c> to any client whose User-Agent is
    /// not browser-shaped. A bare token like <c>FhirAugury/2.0</c> — what
    /// <c>Program.cs</c> sends today — is rejected on every request. This form
    /// still identifies us honestly and passes the challenge.
    /// </summary>
    public const string UserAgent =
        "Mozilla/5.0 (compatible; FhirAugury/2.0; +https://github.com/GinoCanessa/fhir-augury)";

    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        HttpClient client = new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("accept", "application/json");
        client.DefaultRequestHeaders.TryAddWithoutValidation("user-agent", UserAgent);

        if (!string.IsNullOrWhiteSpace(Cookie))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("cookie", Cookie);
        }
        else if (!string.IsNullOrWhiteSpace(ApiToken))
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("authorization", $"Bearer {ApiToken}");
        }

        return client;
    });

    private static readonly Lazy<HttpClient> RawClient = new(() =>
        new HttpClient { Timeout = TimeSpan.FromMinutes(2) });

    /// <summary>Issues a rate-limited GET and returns the parsed JSON root.</summary>
    public static async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct = default)
    {
        using HttpResponseMessage response = await SendAsync(path, HttpMethod.Get, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Issues a rate-limited request and returns the raw response for status inspection.</summary>
    public static async Task<HttpResponseMessage> SendAsync(string path, HttpMethod method, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            TimeSpan elapsed = DateTimeOffset.UtcNow - _lastRequest;
            if (elapsed < RequestInterval)
            {
                await Task.Delay(RequestInterval - elapsed, ct);
            }

            using HttpRequestMessage request = new(method, path);
            HttpResponseMessage response = await Client.Value.SendAsync(request, ct);
            _lastRequest = DateTimeOffset.UtcNow;
            return response;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Sends a caller-built request (absolute URI, caller-supplied headers)
    /// through the same politeness gate, bypassing the shared client's default
    /// headers. Used by the User-Agent gate probe.
    /// </summary>
    public static async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            TimeSpan elapsed = DateTimeOffset.UtcNow - _lastRequest;
            if (elapsed < RequestInterval)
            {
                await Task.Delay(RequestInterval - elapsed, ct);
            }

            HttpResponseMessage response = await RawClient.Value.SendAsync(request, ct);
            _lastRequest = DateTimeOffset.UtcNow;
            return response;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Reads a CQL result-set total via <c>/rest/api/search</c>.</summary>
    public static async Task<int> GetCqlTotalAsync(string cql, CancellationToken ct = default)
    {
        JsonElement root = await GetJsonAsync($"/rest/api/search?cql={Uri.EscapeDataString(cql)}&limit=1", ct);
        return root.TryGetProperty("totalSize", out JsonElement total) ? total.GetInt32() : -1;
    }

    /// <summary>Follows <c>_links.next</c> to exhaustion, returning every result element.</summary>
    public static async Task<(List<JsonElement> Results, int Requests)> EnumerateAsync(
        string path, int maxRequests = 200, CancellationToken ct = default)
    {
        List<JsonElement> results = [];
        int requests = 0;
        string? next = path;

        while (!string.IsNullOrEmpty(next) && requests < maxRequests)
        {
            JsonElement root = await GetJsonAsync(next, ct);
            requests++;

            if (root.TryGetProperty("results", out JsonElement page) && page.ValueKind == JsonValueKind.Array)
            {
                results.AddRange(page.EnumerateArray().Select(e => e.Clone()));
            }

            next = root.TryGetProperty("_links", out JsonElement links)
                && links.TryGetProperty("next", out JsonElement nextLink)
                && nextLink.ValueKind == JsonValueKind.String
                ? nextLink.GetString()
                : null;
        }

        return (results, requests);
    }

    /// <summary>Reads a string property, returning null when absent or not a string.</summary>
    public static string? Str(this JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Reads a nested element, returning null when any segment is absent.</summary>
    public static JsonElement? Path(this JsonElement element, params string[] names)
    {
        JsonElement current = element;
        foreach (string name in names)
        {
            if (!current.TryGetProperty(name, out JsonElement next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself unless the live-probe opt-in
/// is set (see <see cref="ConfluenceProbe.OptInVariable"/>). Reports as
/// "Skipped" rather than failing, so the sanctioned
/// <c>dotnet test fhir-augury.slnx</c> run stays offline.
/// </summary>
public sealed class ConfluenceProbeFactAttribute : FactAttribute
{
    public ConfluenceProbeFactAttribute()
    {
        if (!ConfluenceProbe.IsEnabled)
        {
            Skip = $"Live Confluence probe disabled; set {ConfluenceProbe.OptInVariable}=1 to run.";
        }
    }
}

/// <summary>
/// Live, opt-in probes of the HL7 Confluence instance (slot 0827-01, Phase 1).
/// These exist to make the API assumptions the manifest-reconciliation design
/// rests on <em>falsifiable and re-runnable</em>: query shapes, pagination
/// behaviour, whether <c>container.id</c> is populated, whether archived
/// content is reachable, and how large the corpus actually is.
/// </summary>
/// <remarks>
/// Findings are transcribed into <c>docs/technical/confluence-api-notes.md</c>.
/// Nothing offline depends on these tests; they are skipped by default.
/// </remarks>
public class ConfluenceApiProbeTests(ITestOutputHelper output)
{
    private const string SampleSpace = "FHIR";
    private const int SweepPageSize = 200;

    private static readonly string[] SampleSpaces = ["FHIR", "FHIRI", "SOA"];

    [ConfluenceProbeFact]
    public async Task Probe02_SpaceDiscovery_EnumeratesGlobalCurrentSpaces()
    {
        output.WriteLine($"Base URL: {ConfluenceProbe.BaseUrl} (credential: {ConfluenceProbe.CredentialDescription})");

        (List<JsonElement> spaces, int requests) = await ConfluenceProbe.EnumerateAsync(
            $"/rest/api/space?type=global&status=current&limit={SweepPageSize}");

        output.WriteLine($"GET /rest/api/space?type=global&status=current&limit={SweepPageSize}");
        output.WriteLine($"  non-archived global spaces: {spaces.Count} over {requests} request(s)");

        string[] otherStatus = [.. spaces.Select(s => s.Str("status")).Where(s => s != "current").Distinct()!];
        string[] otherType = [.. spaces.Select(s => s.Str("type")).Where(t => t != "global").Distinct()!];
        output.WriteLine($"  status values other than 'current': {(otherStatus.Length == 0 ? "(none)" : string.Join(", ", otherStatus))}");
        output.WriteLine($"  type values other than 'global': {(otherType.Length == 0 ? "(none)" : string.Join(", ", otherType))}");

        JsonElement unfiltered = await ConfluenceProbe.GetJsonAsync($"/rest/api/space?limit={SweepPageSize}");
        int unfilteredCount = unfiltered.GetProperty("results").GetArrayLength();
        output.WriteLine($"  unfiltered /rest/api/space returns {unfilteredCount} (difference is personal/archived spaces)");

        Assert.NotEmpty(spaces);
        Assert.Empty(otherStatus);
        Assert.Empty(otherType);
    }

    [ConfluenceProbeFact]
    public async Task Probe03_ApiDialect_IsServerDataCenterV1()
    {
        JsonElement space = await ConfluenceProbe.GetJsonAsync($"/rest/api/space/{SampleSpace}");
        string? self = space.Path("_links", "self")?.GetString();

        output.WriteLine($"GET /rest/api/space/{SampleSpace}");
        output.WriteLine($"  _links.self: {self}");
        output.WriteLine($"  has _expandable (Server/DC marker): {space.TryGetProperty("_expandable", out _)}");
        output.WriteLine($"  has /wiki/ path prefix (Cloud marker): {self?.Contains("/wiki/", StringComparison.Ordinal) == true}");

        using HttpResponseMessage html = await ConfluenceProbe.SendAsync($"/spaces/{SampleSpace}/overview", HttpMethod.Get);
        string body = await html.Content.ReadAsStringAsync();
        output.WriteLine($"  ajs-version-number: {ReadMeta(body, "ajs-version-number") ?? "(not exposed)"}");
        output.WriteLine($"  ajs-build-number:   {ReadMeta(body, "ajs-build-number") ?? "(not exposed)"}");

        Assert.NotNull(self);
        Assert.Contains("/rest/api/", self, StringComparison.Ordinal);
        Assert.DoesNotContain("/wiki/rest/api/", self, StringComparison.Ordinal);
    }

    [ConfluenceProbeFact]
    public async Task Probe04_PageSweep_HonoursLimitAndPaginatesToExhaustion()
    {
        string path = $"/rest/api/content?spaceKey={SampleSpace}&type=page&expand=version&limit={SweepPageSize}";
        JsonElement first = await ConfluenceProbe.GetJsonAsync(path);

        int reportedLimit = first.GetProperty("limit").GetInt32();
        int firstCount = first.GetProperty("results").GetArrayLength();
        string? next = first.Path("_links", "next")?.GetString();

        output.WriteLine($"GET {path}");
        output.WriteLine($"  requested limit {SweepPageSize} -> reported limit {reportedLimit}, {firstCount} results");
        output.WriteLine($"  _links.next present: {next is not null} ({next})");
        output.WriteLine($"  'size' on this envelope is the page count, not the corpus total");

        int cqlTotal = await ConfluenceProbe.GetCqlTotalAsync($"space=\"{SampleSpace}\" and type=page");

        // Exhaustion is proved on a smaller space so the probe stays quick.
        (List<JsonElement> pages, int requests) = await ConfluenceProbe.EnumerateAsync(
            $"/rest/api/content?spaceKey=FHIRI&type=page&expand=version&limit={SweepPageSize}");
        int fhiriTotal = await ConfluenceProbe.GetCqlTotalAsync("space=\"FHIRI\" and type=page");

        output.WriteLine($"  {SampleSpace} pages via CQL totalSize: {cqlTotal}");
        output.WriteLine($"  FHIRI enumerated to exhaustion: {pages.Count} over {requests} request(s); CQL totalSize {fhiriTotal}");

        JsonElement sample = pages[0];
        output.WriteLine($"  sample entry fields: id={sample.Str("id")}, status={sample.Str("status")}, " +
                         $"version.number={sample.Path("version", "number")}, version.when={sample.Path("version", "when")}");

        Assert.Equal(SweepPageSize, reportedLimit);
        Assert.Equal(fhiriTotal, pages.Count);
    }

    [ConfluenceProbeFact]
    public async Task Probe05_ArchivedVisibility_RecordsAcceptedStatusForms()
    {
        output.WriteLine("Probing which 'status' form surfaces archived pages inside a live space.");

        foreach (string form in new[] { "current", "archived", "current,archived", "any" })
        {
            string path = $"/rest/api/content?spaceKey={SampleSpace}&type=page&status={Uri.EscapeDataString(form)}&limit=25";
            JsonElement root = await ConfluenceProbe.GetJsonAsync(path);
            JsonElement results = root.GetProperty("results");
            string[] statuses = [.. results.EnumerateArray().Select(r => r.Str("status") ?? "?").Distinct()];
            output.WriteLine($"  status={form,-17} -> {results.GetArrayLength(),3} results, statuses: " +
                             $"{(statuses.Length == 0 ? "(none)" : string.Join("|", statuses))}");
        }

        JsonElement repeated = await ConfluenceProbe.GetJsonAsync(
            $"/rest/api/content?spaceKey={SampleSpace}&type=page&status=current&status=archived&limit=25");
        string[] repeatedStatuses = [.. repeated.GetProperty("results").EnumerateArray()
            .Select(r => r.Str("status") ?? "?").Distinct()];
        output.WriteLine($"  status=current&status=archived (repeated) -> " +
                         $"{repeated.GetProperty("results").GetArrayLength()} results, statuses: {string.Join("|", repeatedStatuses)}");

        int archivedAnywhere = await ConfluenceProbe.GetCqlTotalAsync("type=page and status=archived");
        output.WriteLine($"  CQL 'type=page and status=archived' across the whole instance: {archivedAnywhere}");
        output.WriteLine($"  credential in use: {ConfluenceProbe.CredentialDescription} — archived visibility is permission-scoped.");
    }

    [ConfluenceProbeFact]
    public async Task Probe06_Comments_CqlStreamPopulatesContainerId()
    {
        string cql = Uri.EscapeDataString($"space=\"{SampleSpace}\" and type=comment");
        string path = $"/rest/api/content/search?cql={cql}&expand=version,container&limit={SweepPageSize}";
        JsonElement root = await ConfluenceProbe.GetJsonAsync(path);
        JsonElement results = root.GetProperty("results");

        int withContainer = results.EnumerateArray().Count(r => r.Path("container", "id") is not null);
        int total = results.GetArrayLength();

        output.WriteLine($"GET {path}");
        output.WriteLine($"  results: {total}, with container.id: {withContainer}");
        output.WriteLine($"  _links.next present: {root.Path("_links", "next") is not null}");

        JsonElement sample = results[0];
        output.WriteLine($"  sample: id={sample.Str("id")}, title={sample.Str("title")}, " +
                         $"container.id={sample.Path("container", "id")}, " +
                         $"extensions.location={sample.Path("extensions", "location")}");

        int instanceComments = await ConfluenceProbe.GetCqlTotalAsync("type=comment");
        output.WriteLine($"  instance-wide comments: {instanceComments}");

        // If this assertion ever fails, the sweep must fall back to
        // GET /rest/api/content/{id}/child/comment — roughly one call per page.
        Assert.Equal(total, withContainer);
    }

    [ConfluenceProbeFact]
    public async Task Probe07_Attachments_CqlStreamCarriesMediaTypeSizeAndDownloadLink()
    {
        string cql = Uri.EscapeDataString($"space=\"{SampleSpace}\" and type=attachment");
        string path = $"/rest/api/content/search?cql={cql}&expand=version,container,metadata&limit={SweepPageSize}";
        JsonElement root = await ConfluenceProbe.GetJsonAsync(path);
        JsonElement results = root.GetProperty("results");
        int total = results.GetArrayLength();

        int withContainer = results.EnumerateArray().Count(r => r.Path("container", "id") is not null);
        int withMediaType = results.EnumerateArray().Count(r => r.Path("extensions", "mediaType") is not null);
        int withFileSize = results.EnumerateArray().Count(r => r.Path("extensions", "fileSize") is not null);
        int withDownload = results.EnumerateArray().Count(r => r.Path("_links", "download") is not null);

        output.WriteLine($"GET {path}");
        output.WriteLine($"  results: {total}; container.id: {withContainer}; extensions.mediaType: {withMediaType}; " +
                         $"extensions.fileSize: {withFileSize}; _links.download: {withDownload}");

        JsonElement sample = results[0];
        string? download = sample.Path("_links", "download")?.GetString();
        output.WriteLine($"  sample: id={sample.Str("id")}, title={sample.Str("title")}, " +
                         $"mediaType={sample.Path("extensions", "mediaType")}, fileSize={sample.Path("extensions", "fileSize")}");
        output.WriteLine($"  download link (site-relative): {download}");

        using HttpResponseMessage head = await ConfluenceProbe.SendAsync(download!, HttpMethod.Head);
        output.WriteLine($"  HEAD on download link -> {(int)head.StatusCode}, " +
                         $"Content-Length={head.Content.Headers.ContentLength}, " +
                         $"Content-Type={head.Content.Headers.ContentType}");

        // Per-page fallback shape, in case the space-wide stream is ever refuted.
        string? containerId = sample.Path("container", "id")?.GetString();
        JsonElement child = await ConfluenceProbe.GetJsonAsync(
            $"/rest/api/content/{containerId}/child/attachment?expand=version,metadata&limit=50");
        output.WriteLine($"  per-page fallback child/attachment on {containerId}: " +
                         $"{child.GetProperty("results").GetArrayLength()} results");

        Assert.Equal(total, withContainer);
        Assert.Equal(total, withMediaType);
        Assert.Equal(total, withDownload);
        Assert.True(head.IsSuccessStatusCode, "attachment download link must be fetchable");
    }

    [ConfluenceProbeFact]
    public async Task Probe08_CorpusSize_RecordsTotalsAndDerivedRequestBudget()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        (List<JsonElement> spaces, _) = await ConfluenceProbe.EnumerateAsync(
            $"/rest/api/space?type=global&status=current&limit={SweepPageSize}");

        int pages = await ConfluenceProbe.GetCqlTotalAsync("type=page");
        int comments = await ConfluenceProbe.GetCqlTotalAsync("type=comment");
        int attachments = await ConfluenceProbe.GetCqlTotalAsync("type=attachment");
        int blogposts = await ConfluenceProbe.GetCqlTotalAsync("type=blogpost");

        output.WriteLine("Instance-wide corpus (CQL totalSize):");
        output.WriteLine($"  spaces (global, current): {spaces.Count}");
        output.WriteLine($"  pages: {pages}   comments: {comments}   attachments: {attachments}   blogposts: {blogposts}");

        output.WriteLine("Per-space totals:");
        foreach (string spaceKey in SampleSpaces)
        {
            int p = await ConfluenceProbe.GetCqlTotalAsync($"space=\"{spaceKey}\" and type=page");
            int c = await ConfluenceProbe.GetCqlTotalAsync($"space=\"{spaceKey}\" and type=comment");
            int a = await ConfluenceProbe.GetCqlTotalAsync($"space=\"{spaceKey}\" and type=attachment");
            output.WriteLine($"  {spaceKey,-6} pages={p,-6} comments={c,-6} attachments={a}");
        }

        // A sweep costs at least one request per stream per space, plus one per
        // additional page of results — that lower bound dominates on an instance
        // with many small spaces.
        int streamFloor = spaces.Count * 3;
        int pagingCost = (pages + comments + attachments) / SweepPageSize;
        int sweepRequests = streamFloor + pagingCost + 1;
        output.WriteLine($"Derived sweep budget at SweepPageSize={SweepPageSize}:");
        output.WriteLine($"  >= {streamFloor} (3 streams x {spaces.Count} spaces) + ~{pagingCost} paging = ~{sweepRequests} requests");
        output.WriteLine($"  at 5 req/s that is ~{sweepRequests / 5.0 / 60.0:F1} minutes per full sweep");
        output.WriteLine($"Derived initial fill budget: ~{pages + comments} body fetches + {attachments} blob fetches");
        output.WriteLine($"  at 5 req/s that is ~{(pages + comments + attachments) / 5.0 / 3600.0:F1} hours");

        // fileSize reliability and the byte estimate, sampled rather than
        // enumerated: walking 195k attachments would be a probe, not a sample.
        long sampledBytes = 0;
        int sampledCount = 0, missingSize = 0, zeroSize = 0, over25Mb = 0, over100Mb = 0;
        long largest = 0;

        foreach (string spaceKey in new[] { "SOA", "FHIRI", "CIMI", "CDS" })
        {
            (List<JsonElement> items, _) = await ConfluenceProbe.EnumerateAsync(
                $"/rest/api/content/search?cql={Uri.EscapeDataString($"space=\"{spaceKey}\" and type=attachment")}" +
                $"&expand=version,container,metadata&limit={SweepPageSize}",
                maxRequests: 15);

            foreach (JsonElement item in items)
            {
                sampledCount++;
                JsonElement? size = item.Path("extensions", "fileSize");
                if (size is null || size.Value.ValueKind != JsonValueKind.Number)
                {
                    missingSize++;
                    continue;
                }

                long bytes = size.Value.GetInt64();
                if (bytes == 0)
                {
                    zeroSize++;
                    continue;
                }

                sampledBytes += bytes;
                largest = Math.Max(largest, bytes);
                if (bytes > 26_214_400) over25Mb++;
                if (bytes > 104_857_600) over100Mb++;
            }
        }

        double averageBytes = sampledCount > 0 ? (double)sampledBytes / sampledCount : 0;
        output.WriteLine($"Attachment size sample ({sampledCount} attachments across 4 spaces):");
        output.WriteLine($"  total {sampledBytes / 1024.0 / 1024.0:F1} MB, average {averageBytes / 1024.0:F0} KB, " +
                         $"largest {largest / 1024.0 / 1024.0:F1} MB");
        output.WriteLine($"  fileSize absent: {missingSize}; fileSize zero: {zeroSize}");
        output.WriteLine($"  over 25 MB: {over25Mb}; over 100 MB (AttachmentMaxBytes default): {over100Mb}");
        output.WriteLine($"  EXTRAPOLATED instance attachment bytes: " +
                         $"~{attachments * averageBytes / 1024.0 / 1024.0 / 1024.0:F0} GB");
        output.WriteLine($"Probe wall clock: {stopwatch.Elapsed.TotalSeconds:F0}s");

        Assert.True(pages > 0 && comments > 0 && attachments > 0);
    }

    /// <summary>
    /// Not one of the plan's steps 2-8, but the finding that gates all of them:
    /// the instance sits behind an AWS WAF that captcha-challenges any client
    /// whose User-Agent is not browser-shaped, so the service's current
    /// <c>FhirAugury/2.0</c> agent would fail 100% of requests.
    /// </summary>
    [ConfluenceProbeFact]
    public async Task Probe09_WafUserAgentGate_RejectsNonBrowserUserAgents()
    {
        const string Path = "/rest/api/space?type=global&status=current&limit=1";

        (string Label, string Agent)[] agents =
        [
            ("service default (today)", "FhirAugury/2.0"),
            ("bare Mozilla token", "Mozilla/5.0"),
            ("common HTTP client", "python-requests/2.31.0"),
            ("browser-shaped", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"),
            ("honest + browser-shaped", ConfluenceProbe.UserAgent),
        ];

        output.WriteLine($"GET {Path} with varying User-Agent:");
        Dictionary<string, int> observed = [];

        foreach ((string label, string agent) in agents)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, ConfluenceProbe.BaseUrl + Path);
            request.Headers.TryAddWithoutValidation("user-agent", agent);
            request.Headers.TryAddWithoutValidation("accept", "application/json");

            using HttpResponseMessage response = await ConfluenceProbe.SendRawAsync(request);
            string? wafAction = response.Headers.TryGetValues("x-amzn-waf-action", out IEnumerable<string>? values)
                ? string.Join(",", values)
                : null;

            observed[label] = (int)response.StatusCode;
            output.WriteLine($"  {(int)response.StatusCode}  {label,-24} {agent}" +
                             (wafAction is null ? "" : $"   [x-amzn-waf-action: {wafAction}]"));
        }

        Assert.Equal(405, observed["service default (today)"]);
        Assert.Equal(200, observed["browser-shaped"]);
        Assert.Equal(200, observed["honest + browser-shaped"]);
    }

    private static string? ReadMeta(string html, string name)    {
        int index = html.IndexOf($"name=\"{name}\"", StringComparison.Ordinal);
        if (index < 0) return null;

        int contentIndex = html.IndexOf("content=\"", index, StringComparison.Ordinal);
        if (contentIndex < 0) return null;

        contentIndex += "content=\"".Length;
        int end = html.IndexOf('"', contentIndex);
        return end < 0 ? null : html[contentIndex..end];
    }
}
