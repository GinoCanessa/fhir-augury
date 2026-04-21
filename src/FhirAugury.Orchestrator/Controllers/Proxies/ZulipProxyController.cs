using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Controllers.Proxies;

/// <summary>
/// Typed orchestrator proxy for the Zulip source. Forwards every request to
/// <c>FhirAugury.Source.Zulip</c> via <see cref="SourceHttpClient"/>, preserving
/// query string, request body, allow-listed headers, response status, body,
/// and ETag/Last-Modified.
/// </summary>
/// <remarks>
/// Each action is a 1:1 passthrough to the corresponding upstream Zulip route.
/// Refer to the Zulip source's own OpenAPI document for the full
/// request/response schema. Common response codes: <c>200 OK</c> for success,
/// <c>404 Not Found</c> for unknown identifiers, <c>503 Service Unavailable</c>
/// when the Zulip source is unreachable.
/// </remarks>
[ApiController]
[Route("api/v1/zulip")]
public class ZulipProxyController(SourceHttpClient httpClient) : ControllerBase
{
    private const string Source = "zulip";

    // ── Items ────────────────────────────────────────────────────────────

    /// <summary>List Zulip items (paged).</summary>
    /// <param name="limit">Maximum number of items.</param>
    /// <param name="offset">Number of items to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items")]
    public Task<IActionResult> ListItems([FromQuery] int? limit, [FromQuery] int? offset, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "items", Request, ct);

    /// <summary>Get a single Zulip item by identifier.</summary>
    /// <param name="id">Zulip item identifier.</param>
    /// <param name="includeContent">Include the content body.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/{id}")]
    public Task<IActionResult> GetItem(string id, [FromQuery] bool? includeContent, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(id)}", Request, ct);

    /// <summary>Get items related to a Zulip item.</summary>
    /// <param name="id">Zulip item identifier.</param>
    /// <param name="limit">Maximum number of related items.</param>
    /// <param name="seedSource">Optional seed-source filter.</param>
    /// <param name="seedId">Optional seed-id filter.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/{id}/related")]
    public Task<IActionResult> GetItemRelated(string id,
        [FromQuery] int? limit, [FromQuery] string? seedSource, [FromQuery] string? seedId, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(id)}/related", Request, ct);

    /// <summary>Get a markdown snapshot of a Zulip item.</summary>
    /// <param name="id">Zulip item identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/{id}/snapshot")]
    public Task<IActionResult> GetItemSnapshot(string id, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(id)}/snapshot", Request, ct);

    /// <summary>Get the raw content body of a Zulip item.</summary>
    /// <param name="id">Zulip item identifier.</param>
    /// <param name="format">Optional content format.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/{id}/content")]
    public Task<IActionResult> GetItemContent(string id, [FromQuery] string? format, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(id)}/content", Request, ct);

    /// <summary>
    /// Shape-parity stub: Zulip items have no comments, so this endpoint
    /// always returns an empty array. Provided so cross-source consumers
    /// can call <c>items/{id}/comments</c> uniformly across all four sources.
    /// </summary>
    /// <param name="id">Zulip item identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Empty array (always).</response>
    [HttpGet("items/{id}/comments")]
    public Task<IActionResult> GetItemComments(string id, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(id)}/comments", Request, ct);

    /// <summary>
    /// Shape-parity stub: Zulip items carry no first-class link graph, so
    /// this endpoint always returns an empty array. Provided so cross-source
    /// consumers can call <c>items/{id}/links</c> uniformly across all four
    /// sources.
    /// </summary>
    /// <param name="id">Zulip item identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Empty array (always).</response>
    [HttpGet("items/{id}/links")]
    public Task<IActionResult> GetItemLinks(string id, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(id)}/links", Request, ct);

    // ── Messages ─────────────────────────────────────────────────────────

    /// <summary>List Zulip messages (paged).</summary>
    /// <param name="limit">Maximum number of messages.</param>
    /// <param name="offset">Number of messages to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("messages")]
    public Task<IActionResult> ListMessages([FromQuery] int? limit, [FromQuery] int? offset, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "messages", Request, ct);

    /// <summary>Get a single Zulip message by integer ID.</summary>
    /// <param name="id">Zulip message ID.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("messages/{id:int}")]
    public Task<IActionResult> GetMessage(int id, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"messages/{id}", Request, ct);

    /// <summary>List the messages sent by a specific user.</summary>
    /// <param name="user">Zulip user identifier.</param>
    /// <param name="limit">Maximum number of messages.</param>
    /// <param name="offset">Number of messages to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("messages/by-user/{user}")]
    public Task<IActionResult> GetMessagesByUser(string user,
        [FromQuery] int? limit, [FromQuery] int? offset, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"messages/by-user/{Uri.EscapeDataString(user)}", Request, ct);

    // ── Streams ──────────────────────────────────────────────────────────

    /// <summary>List the indexed Zulip streams.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("streams")]
    public Task<IActionResult> ListStreams(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "streams", Request, ct);

    /// <summary>Get a single stream by Zulip stream ID.</summary>
    /// <param name="zulipStreamId">Zulip stream identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("streams/{zulipStreamId:int}")]
    public Task<IActionResult> GetStream(int zulipStreamId, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"streams/{zulipStreamId}", Request, ct);

    /// <summary>Update mutable stream metadata (e.g. <c>includeStream</c>). Body carries the patch.</summary>
    /// <param name="zulipStreamId">Zulip stream identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("streams/{zulipStreamId:int}")]
    public Task<IActionResult> UpdateStream(int zulipStreamId, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Put, $"streams/{zulipStreamId}", Request, ct);

    /// <summary>List the topics in a stream.</summary>
    /// <param name="streamName">Stream name.</param>
    /// <param name="limit">Maximum number of topics.</param>
    /// <param name="offset">Number of topics to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("streams/{streamName}/topics")]
    public Task<IActionResult> GetStreamTopics(string streamName,
        [FromQuery] int? limit, [FromQuery] int? offset, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"streams/{Uri.EscapeDataString(streamName)}/topics", Request, ct);

    // ── Threads ──────────────────────────────────────────────────────────

    /// <summary>Get the messages in a (stream, topic) thread.</summary>
    /// <param name="streamName">Stream name.</param>
    /// <param name="topic">Topic name.</param>
    /// <param name="limit">Maximum number of messages.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("threads/{streamName}/{topic}")]
    public Task<IActionResult> GetThread(string streamName, string topic,
        [FromQuery] int? limit, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get,
            $"threads/{Uri.EscapeDataString(streamName)}/{Uri.EscapeDataString(topic)}", Request, ct);

    /// <summary>Get a markdown snapshot of a (stream, topic) thread.</summary>
    /// <param name="streamName">Stream name.</param>
    /// <param name="topic">Topic name.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("threads/{streamName}/{topic}/snapshot")]
    public Task<IActionResult> GetThreadSnapshot(string streamName, string topic, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get,
            $"threads/{Uri.EscapeDataString(streamName)}/{Uri.EscapeDataString(topic)}/snapshot", Request, ct);

    // ── Query ────────────────────────────────────────────────────────────

    /// <summary>Run a structured Zulip message query. Body carries the structured query payload.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("query")]
    public Task<IActionResult> Query(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Post, "query", Request, ct);

    // ── Ingestion ────────────────────────────────────────────────────────

    /// <summary>Trigger a synchronous ingestion run on the Zulip source.</summary>
    /// <param name="type">Sync type — <c>incremental</c> (default), <c>full</c>, or <c>rebuild</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Ingestion completed synchronously.</response>
    /// <response code="202">Ingestion queued.</response>
    [HttpPost("ingest")]
    public Task<IActionResult> Ingest([FromQuery] string? type, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Post, "ingest", Request, ct);

    /// <summary>
    /// Rebuild the Zulip source database from the file-system cache. The CLI
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
