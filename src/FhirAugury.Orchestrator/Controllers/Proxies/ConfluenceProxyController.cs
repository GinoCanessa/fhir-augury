using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Controllers.Proxies;

/// <summary>
/// Typed orchestrator proxy for the Confluence source. Forwards every request
/// to <c>FhirAugury.Source.Confluence</c> via <see cref="SourceHttpClient"/>,
/// preserving query string, request body, allow-listed headers, response
/// status, body, and ETag/Last-Modified.
/// </summary>
/// <remarks>
/// Each action is a 1:1 passthrough to the corresponding upstream Confluence
/// route. Common response codes: <c>200 OK</c> for success, <c>404 Not Found</c>
/// for unknown identifiers, <c>503 Service Unavailable</c> when the Confluence
/// source is unreachable.
/// </remarks>
[ApiController]
[Route("api/v1/confluence")]
public class ConfluenceProxyController(SourceHttpClient httpClient) : ControllerBase
{
    private const string Source = "confluence";

    // ── Items ────────────────────────────────────────────────────────────

    /// <summary>List Confluence items (paged).</summary>
    /// <param name="limit">Maximum number of items.</param>
    /// <param name="offset">Number of items to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items")]
    public Task<IActionResult> ListItems([FromQuery] int? limit, [FromQuery] int? offset, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "items", Request, ct);

    /// <summary>Get a single Confluence item by identifier.</summary>
    /// <param name="id">Confluence item identifier.</param>
    /// <param name="includeContent">Include the content body.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/{id}")]
    public Task<IActionResult> GetItem(string id, [FromQuery] bool? includeContent, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(id)}", Request, ct);

    /// <summary>Get items related to a Confluence item.</summary>
    /// <param name="id">Confluence item identifier.</param>
    /// <param name="limit">Maximum number of related items.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/{id}/related")]
    public Task<IActionResult> GetItemRelated(string id, [FromQuery] int? limit, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(id)}/related", Request, ct);

    /// <summary>Get a markdown snapshot of a Confluence item.</summary>
    /// <param name="id">Confluence item identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/{id}/snapshot")]
    public Task<IActionResult> GetItemSnapshot(string id, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(id)}/snapshot", Request, ct);

    /// <summary>Get the raw content body of a Confluence item.</summary>
    /// <param name="id">Confluence item identifier.</param>
    /// <param name="format">Optional content format.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/{id}/content")]
    public Task<IActionResult> GetItemContent(string id, [FromQuery] string? format, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(id)}/content", Request, ct);

    // ── Pages ────────────────────────────────────────────────────────────

    /// <summary>List Confluence pages (paged), optionally filtered by space.</summary>
    /// <param name="limit">Maximum number of pages.</param>
    /// <param name="offset">Number of pages to skip.</param>
    /// <param name="spaceKey">Optional Confluence space key.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("pages")]
    public Task<IActionResult> ListPages(
        [FromQuery] int? limit, [FromQuery] int? offset, [FromQuery] string? spaceKey, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "pages", Request, ct);

    /// <summary>List the Confluence pages tagged with a specific label.</summary>
    /// <param name="label">Label name.</param>
    /// <param name="spaceKey">Optional Confluence space key.</param>
    /// <param name="limit">Maximum number of pages.</param>
    /// <param name="offset">Number of pages to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("pages/by-label/{label}")]
    public Task<IActionResult> PagesByLabel(string label,
        [FromQuery] string? spaceKey, [FromQuery] int? limit, [FromQuery] int? offset, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"pages/by-label/{Uri.EscapeDataString(label)}", Request, ct);

    /// <summary>Get a single Confluence page by ID.</summary>
    /// <param name="pageId">Confluence page ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("pages/{pageId}")]
    public Task<IActionResult> GetPage(string pageId, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"pages/{Uri.EscapeDataString(pageId)}", Request, ct);

    /// <summary>Get pages related to a Confluence page.</summary>
    /// <param name="pageId">Confluence page ID.</param>
    /// <param name="limit">Maximum number of related pages.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("pages/{pageId}/related")]
    public Task<IActionResult> GetPageRelated(string pageId, [FromQuery] int? limit, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"pages/{Uri.EscapeDataString(pageId)}/related", Request, ct);

    /// <summary>Get a markdown snapshot of a Confluence page.</summary>
    /// <param name="pageId">Confluence page ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("pages/{pageId}/snapshot")]
    public Task<IActionResult> GetPageSnapshot(string pageId, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"pages/{Uri.EscapeDataString(pageId)}/snapshot", Request, ct);

    /// <summary>Get the raw content body of a Confluence page.</summary>
    /// <param name="pageId">Confluence page ID.</param>
    /// <param name="format">Optional content format.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("pages/{pageId}/content")]
    public Task<IActionResult> GetPageContent(string pageId, [FromQuery] string? format, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"pages/{Uri.EscapeDataString(pageId)}/content", Request, ct);

    /// <summary>Get the comments on a Confluence page.</summary>
    /// <param name="pageId">Confluence page ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("pages/{pageId}/comments")]
    public Task<IActionResult> GetPageComments(string pageId, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"pages/{Uri.EscapeDataString(pageId)}/comments", Request, ct);

    /// <summary>Get the immediate child pages of a Confluence page.</summary>
    /// <param name="pageId">Confluence page ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("pages/{pageId}/children")]
    public Task<IActionResult> GetPageChildren(string pageId, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"pages/{Uri.EscapeDataString(pageId)}/children", Request, ct);

    /// <summary>Get the ancestor chain of a Confluence page (root → page).</summary>
    /// <param name="pageId">Confluence page ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("pages/{pageId}/ancestors")]
    public Task<IActionResult> GetPageAncestors(string pageId, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"pages/{Uri.EscapeDataString(pageId)}/ancestors", Request, ct);

    /// <summary>Get pages linked to/from a Confluence page.</summary>
    /// <param name="pageId">Confluence page ID.</param>
    /// <param name="direction">Optional direction filter (<c>inbound</c>, <c>outbound</c>, or omit for both).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("pages/{pageId}/linked")]
    public Task<IActionResult> GetPageLinked(string pageId, [FromQuery] string? direction, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"pages/{Uri.EscapeDataString(pageId)}/linked", Request, ct);

    // ── Spaces ───────────────────────────────────────────────────────────

    /// <summary>List the indexed Confluence spaces.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("spaces")]
    public Task<IActionResult> ListSpaces(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "spaces", Request, ct);

    // ── Ingestion ────────────────────────────────────────────────────────

    /// <summary>Trigger a synchronous ingestion run on the Confluence source.</summary>
    /// <param name="type">Sync type — <c>incremental</c> (default), <c>full</c>, or <c>rebuild</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Ingestion completed synchronously.</response>
    /// <response code="202">Ingestion queued.</response>
    [HttpPost("ingest")]
    public Task<IActionResult> Ingest([FromQuery] string? type, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Post, "ingest", Request, ct);

    /// <summary>
    /// Rebuild the Confluence source database from the file-system cache.
    /// The CLI surface exposes this operation as the <c>reingest</c> verb
    /// (renamed from <c>rebuild</c> in the 2026-04 sync); the wire path
    /// retains its historical name.
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
