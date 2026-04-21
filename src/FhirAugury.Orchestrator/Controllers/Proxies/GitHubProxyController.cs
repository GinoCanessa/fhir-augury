using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Controllers.Proxies;

/// <summary>
/// Typed orchestrator proxy for the GitHub source. Items use action-first
/// routing (<c>items/{action}/{**key}</c>) per the source service convention
/// — the multi-segment greedy catch-all <c>{**key}</c> preserves the
/// <c>owner/name#123</c> (or <c>owner/name@sha</c>) shape of GitHub item
/// keys without URL-escaping the slash. This layout is kept by design
/// (analysis §5.1.4); it is not aligned with the other sources'
/// <c>items/{id}/{action}</c> shape.
/// </summary>
/// <remarks>
/// Each action is a 1:1 passthrough to the corresponding upstream GitHub
/// route via <see cref="SourceHttpClient"/>, preserving query string,
/// request body, allow-listed headers, response status, body, and
/// ETag/Last-Modified. Common response codes: <c>200 OK</c> for success,
/// <c>404 Not Found</c> for unknown identifiers, <c>503 Service Unavailable</c>
/// when the GitHub source is unreachable.
/// </remarks>
[ApiController]
[Route("api/v1/github")]
public class GitHubProxyController(SourceHttpClient httpClient) : ControllerBase
{
    private const string Source = "github";

    private static string EncodeKey(string key) => key.Replace("#", "%23");

    // ── Items (action-first) ─────────────────────────────────────────────

    /// <summary>List GitHub items (paged).</summary>
    /// <param name="limit">Maximum number of items.</param>
    /// <param name="offset">Number of items to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items")]
    public Task<IActionResult> ListItems([FromQuery] int? limit, [FromQuery] int? offset, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "items", Request, ct);

    /// <summary>Get items related to a GitHub item.</summary>
    /// <param name="key">GitHub item key (e.g. <c>HL7/fhir-core#1234</c>).</param>
    /// <param name="limit">Maximum number of related items.</param>
    /// <param name="seedSource">Optional seed-source filter.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/related/{**key}")]
    public Task<IActionResult> GetRelated(string key,
        [FromQuery] int? limit, [FromQuery] string? seedSource, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/related/{EncodeKey(key)}", Request, ct);

    /// <summary>Get a markdown snapshot of a GitHub item.</summary>
    /// <param name="key">GitHub item key.</param>
    /// <param name="includeComments">Include comments in the snapshot.</param>
    /// <param name="includeRefs">Include cross-references in the snapshot.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/snapshot/{**key}")]
    public Task<IActionResult> GetSnapshot(string key,
        [FromQuery] bool? includeComments, [FromQuery] bool? includeRefs, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/snapshot/{EncodeKey(key)}", Request, ct);

    /// <summary>Get the raw content body of a GitHub item.</summary>
    /// <param name="key">GitHub item key.</param>
    /// <param name="format">Optional content format.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/content/{**key}")]
    public Task<IActionResult> GetContent(string key, [FromQuery] string? format, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/content/{EncodeKey(key)}", Request, ct);

    /// <summary>Get the comments on a GitHub issue or PR.</summary>
    /// <param name="key">GitHub item key.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/comments/{**key}")]
    public Task<IActionResult> GetComments(string key, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/comments/{EncodeKey(key)}", Request, ct);

    /// <summary>Get the commits associated with a GitHub item.</summary>
    /// <param name="key">GitHub item key.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/commits/{**key}")]
    public Task<IActionResult> GetCommits(string key, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/commits/{EncodeKey(key)}", Request, ct);

    /// <summary>Get the pull request payload for a GitHub item.</summary>
    /// <param name="key">GitHub item key.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/pr/{**key}")]
    public Task<IActionResult> GetPullRequest(string key, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/pr/{EncodeKey(key)}", Request, ct);

    /// <summary>Get a single GitHub item by key.</summary>
    /// <param name="key">GitHub item key (e.g. <c>HL7/fhir-core#1234</c>).</param>
    /// <param name="includeContent">Include the content body.</param>
    /// <param name="includeComments">Include comments.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/{**key}")]
    public Task<IActionResult> GetItem(string key,
        [FromQuery] bool? includeContent, [FromQuery] bool? includeComments, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{EncodeKey(key)}", Request, ct);

    // ── Repos ────────────────────────────────────────────────────────────

    /// <summary>List the GitHub repositories the source service is configured to ingest.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("repos")]
    public Task<IActionResult> ListRepos(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "repos", Request, ct);

    /// <summary>List the indexed git tags for a repository.</summary>
    /// <param name="owner">Repository owner.</param>
    /// <param name="name">Repository name.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("repos/{owner}/{name}/tags")]
    public Task<IActionResult> ListRepoTags(string owner, string name, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/tags", Request, ct);

    /// <summary>List the indexed files for a repository tag.</summary>
    /// <param name="owner">Repository owner.</param>
    /// <param name="name">Repository name.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("repos/{owner}/{name}/tags/files")]
    public Task<IActionResult> ListRepoTagFiles(string owner, string name, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/tags/files", Request, ct);

    /// <summary>Search the indexed file contents for a repository tag.</summary>
    /// <param name="owner">Repository owner.</param>
    /// <param name="name">Repository name.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("repos/{owner}/{name}/tags/search")]
    public Task<IActionResult> SearchRepoTags(string owner, string name, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get,
            $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/tags/search", Request, ct);

    // ── Jira specs (in GitHub) ───────────────────────────────────────────

    /// <summary>List the Jira-tracked specifications resolved against GitHub artifacts.</summary>
    /// <param name="family">Optional specification family filter.</param>
    /// <param name="workgroup">Optional HL7 work-group filter.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("jira-specs")]
    public Task<IActionResult> ListJiraSpecs(
        [FromQuery] string? family, [FromQuery] string? workgroup, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "jira-specs", Request, ct);

    /// <summary>List the work groups that own at least one Jira-tracked specification.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("jira-specs/workgroups")]
    public Task<IActionResult> ListJiraSpecWorkgroups(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "jira-specs/workgroups", Request, ct);

    /// <summary>List the specification families.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("jira-specs/families")]
    public Task<IActionResult> ListJiraSpecFamilies(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "jira-specs/families", Request, ct);

    /// <summary>Resolve a Jira specification by its source git URL.</summary>
    /// <param name="url">Git URL.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("jira-specs/by-git-url")]
    public Task<IActionResult> JiraSpecByGitUrl([FromQuery] string? url, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "jira-specs/by-git-url", Request, ct);

    /// <summary>Resolve a Jira specification by its FHIR canonical URL.</summary>
    /// <param name="url">Canonical URL.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("jira-specs/by-canonical")]
    public Task<IActionResult> JiraSpecByCanonical([FromQuery] string? url, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "jira-specs/by-canonical", Request, ct);

    /// <summary>Resolve a Jira-spec artifact key to its concrete GitHub artifact.</summary>
    /// <param name="artifactKey">Artifact key.</param>
    /// <param name="specKey">Optional specification key.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("jira-specs/resolve-artifact/{artifactKey}")]
    public Task<IActionResult> ResolveJiraSpecArtifact(string artifactKey,
        [FromQuery] string? specKey, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get,
            $"jira-specs/resolve-artifact/{Uri.EscapeDataString(artifactKey)}", Request, ct);

    /// <summary>Resolve a Jira-spec page key to its concrete GitHub page.</summary>
    /// <param name="pageKey">Page key.</param>
    /// <param name="specKey">Optional specification key.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("jira-specs/resolve-page/{pageKey}")]
    public Task<IActionResult> ResolveJiraSpecPage(string pageKey,
        [FromQuery] string? specKey, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get,
            $"jira-specs/resolve-page/{Uri.EscapeDataString(pageKey)}", Request, ct);

    /// <summary>Get a single Jira specification by key.</summary>
    /// <param name="specKey">Specification key.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("jira-specs/{specKey}")]
    public Task<IActionResult> GetJiraSpec(string specKey, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get,
            $"jira-specs/{Uri.EscapeDataString(specKey)}", Request, ct);

    /// <summary>List the artifacts that belong to a Jira specification.</summary>
    /// <param name="specKey">Specification key.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("jira-specs/{specKey}/artifacts")]
    public Task<IActionResult> GetJiraSpecArtifacts(string specKey, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get,
            $"jira-specs/{Uri.EscapeDataString(specKey)}/artifacts", Request, ct);

    /// <summary>List the pages that belong to a Jira specification.</summary>
    /// <param name="specKey">Specification key.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("jira-specs/{specKey}/pages")]
    public Task<IActionResult> GetJiraSpecPages(string specKey, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get,
            $"jira-specs/{Uri.EscapeDataString(specKey)}/pages", Request, ct);

    /// <summary>List the indexed versions of a Jira specification.</summary>
    /// <param name="specKey">Specification key.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("jira-specs/{specKey}/versions")]
    public Task<IActionResult> GetJiraSpecVersions(string specKey, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get,
            $"jira-specs/{Uri.EscapeDataString(specKey)}/versions", Request, ct);

    // ── Ingestion ────────────────────────────────────────────────────────

    /// <summary>Trigger a synchronous ingestion run on the GitHub source.</summary>
    /// <param name="type">Sync type — <c>incremental</c> (default), <c>full</c>, or <c>rebuild</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Ingestion completed synchronously.</response>
    /// <response code="202">Ingestion queued.</response>
    [HttpPost("ingest")]
    public Task<IActionResult> Ingest([FromQuery] string? type, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Post, "ingest", Request, ct);

    /// <summary>
    /// Rebuild the GitHub source database from the file-system cache. The CLI
    /// surface exposes this operation as the <c>reingest</c> verb (renamed
    /// from <c>rebuild</c> in the 2026-04 sync); the wire path retains its
    /// historical name.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("rebuild")]
    public Task<IActionResult> Rebuild(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Post, "rebuild", Request, ct);

    /// <summary>
    /// Receive a peer-ingestion notification from the orchestrator's
    /// <c>POST /api/v1/notify-ingestion</c> fan-out. Tagged
    /// <c>ingestion-notifications</c>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("notify-peer")]
    public Task<IActionResult> NotifyPeer(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Post, "notify-peer", Request, ct);
}
