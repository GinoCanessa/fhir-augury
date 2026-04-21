using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Controllers.Proxies;

/// <summary>
/// Typed orchestrator proxy for the Jira source. Forwards every request to
/// <c>FhirAugury.Source.Jira</c> via <see cref="SourceHttpClient"/>, preserving
/// query string, request body, allow-listed headers, response status, body,
/// and ETag/Last-Modified.
/// </summary>
/// <remarks>
/// <para>
/// Each action is a 1:1 passthrough to the corresponding upstream Jira route
/// at <c>http://source-jira/api/v1/{...}</c>. The action signatures only
/// declare the parameters the orchestrator itself needs to bind from the
/// route or query string; everything else (additional query parameters,
/// request body, and allow-listed headers) is forwarded verbatim to the
/// upstream service. Refer to the Jira source's own OpenAPI document for
/// the full request/response schema of each operation.
/// </para>
/// <para>
/// Common response codes (set by the upstream service and forwarded
/// unchanged): <c>200 OK</c> for success, <c>202 Accepted</c> for
/// asynchronous ingest, <c>400 Bad Request</c> for invalid query/body
/// shapes, <c>404 Not Found</c> for unknown identifiers, and
/// <c>503 Service Unavailable</c> when the Jira source is unreachable.
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/jira")]
public class JiraProxyController(SourceHttpClient httpClient) : ControllerBase
{
    private const string Source = "jira";

    // ── Items ────────────────────────────────────────────────────────────

    /// <summary>List Jira items (paged).</summary>
    /// <param name="limit">Maximum number of items to return.</param>
    /// <param name="offset">Number of items to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Page of Jira items.</response>
    [HttpGet("items")]
    public Task<IActionResult> ListItems([FromQuery] int? limit, [FromQuery] int? offset, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "items", Request, ct);

    /// <summary>Get a single Jira issue by key (e.g. <c>FHIR-12345</c>).</summary>
    /// <param name="key">Jira issue key.</param>
    /// <param name="includeContent">Include the issue body in the response.</param>
    /// <param name="includeComments">Include the issue comments in the response.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Issue payload.</response>
    /// <response code="404">Issue not found.</response>
    [HttpGet("items/{key}")]
    public Task<IActionResult> GetItem(string key,
        [FromQuery] bool? includeContent, [FromQuery] bool? includeComments, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(key)}", Request, ct);

    /// <summary>Get items related to a Jira issue (BM25 keyword similarity).</summary>
    /// <param name="key">Jira issue key.</param>
    /// <param name="seedSource">Optional seed-source filter.</param>
    /// <param name="limit">Maximum number of related items.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/{key}/related")]
    public Task<IActionResult> GetItemRelated(string key,
        [FromQuery] string? seedSource, [FromQuery] int? limit, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(key)}/related", Request, ct);

    /// <summary>Get a markdown snapshot of a Jira issue.</summary>
    /// <param name="key">Jira issue key.</param>
    /// <param name="includeComments">Include comments in the snapshot.</param>
    /// <param name="includeRefs">Include cross-references in the snapshot.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/{key}/snapshot")]
    public Task<IActionResult> GetItemSnapshot(string key,
        [FromQuery] bool? includeComments, [FromQuery] bool? includeRefs, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(key)}/snapshot", Request, ct);

    /// <summary>Get the raw content body of a Jira issue.</summary>
    /// <param name="key">Jira issue key.</param>
    /// <param name="format">Optional content format (e.g. <c>markdown</c>, <c>html</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/{key}/content")]
    public Task<IActionResult> GetItemContent(string key,
        [FromQuery] string? format, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(key)}/content", Request, ct);

    /// <summary>Get comments on a Jira issue.</summary>
    /// <param name="key">Jira issue key.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/{key}/comments")]
    public Task<IActionResult> GetItemComments(string key, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(key)}/comments", Request, ct);

    /// <summary>Get the issue links graph for a Jira issue.</summary>
    /// <param name="key">Jira issue key.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("items/{key}/links")]
    public Task<IActionResult> GetItemLinks(string key, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"items/{Uri.EscapeDataString(key)}/links", Request, ct);

    // ── Query, dimensions ────────────────────────────────────────────────

    /// <summary>Run a structured Jira issue query. Body is the structured query payload.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("query")]
    public Task<IActionResult> Query(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Post, "query", Request, ct);

    /// <summary>List all Jira labels with usage counts.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("labels")]
    public Task<IActionResult> Labels(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "labels", Request, ct);

    /// <summary>List all Jira issue statuses.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("statuses")]
    public Task<IActionResult> Statuses(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "statuses", Request, ct);

    /// <summary>List Jira users (assignees / reporters).</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("users")]
    public Task<IActionResult> Users(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "users", Request, ct);

    /// <summary>List the configured "in-person" attendee names.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("inpersons")]
    public Task<IActionResult> InPersons(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "inpersons", Request, ct);

    // ── Specifications ───────────────────────────────────────────────────

    /// <summary>List the FHIR specifications tracked by Jira.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("specifications")]
    public Task<IActionResult> ListSpecifications(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "specifications", Request, ct);

    /// <summary>Get the issues filed against a specific FHIR specification.</summary>
    /// <param name="spec">Specification key.</param>
    /// <param name="limit">Maximum number of issues.</param>
    /// <param name="offset">Number of issues to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("specifications/{spec}")]
    public Task<IActionResult> GetSpecification(string spec,
        [FromQuery] int? limit, [FromQuery] int? offset, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"specifications/{Uri.EscapeDataString(spec)}", Request, ct);

    /// <summary>Bulk-resolve issue numbers within a Jira project.</summary>
    /// <param name="project">Optional Jira project key.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("issue-numbers")]
    public Task<IActionResult> IssueNumbers([FromQuery] string? project, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "issue-numbers", Request, ct);

    // ── Work groups ──────────────────────────────────────────────────────

    /// <summary>List the HL7 work groups with issue counts.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("work-groups")]
    public Task<IActionResult> WorkGroups(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "work-groups", Request, ct);

    /// <summary>List the issues owned by a specific HL7 work group.</summary>
    /// <param name="groupCode">Work-group code (e.g. <c>fhir-i</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("work-groups/{groupCode}/issues")]
    public Task<IActionResult> WorkGroupIssues(string groupCode, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"work-groups/{Uri.EscapeDataString(groupCode)}/issues", Request, ct);

    /// <summary>List every (work-group, issue) pair across all groups.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("work-groups/issues")]
    public Task<IActionResult> AllWorkGroupIssues(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "work-groups/issues", Request, ct);

    // ── Projects ─────────────────────────────────────────────────────────

    /// <summary>List the Jira projects this service is configured to ingest.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("projects")]
    public Task<IActionResult> ListProjects(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "projects", Request, ct);

    /// <summary>Get a single Jira project by key.</summary>
    /// <param name="key">Project key (e.g. <c>FHIR</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("projects/{key}")]
    public Task<IActionResult> GetProject(string key, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"projects/{Uri.EscapeDataString(key)}", Request, ct);

    /// <summary>Update a Jira project's mutable metadata. Body carries the patch.</summary>
    /// <param name="key">Project key.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("projects/{key}")]
    public Task<IActionResult> UpdateProject(string key, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Put, $"projects/{Uri.EscapeDataString(key)}", Request, ct);

    // ── Local processing ─────────────────────────────────────────────────

    /// <summary>Page through tickets in the local-processing queue.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("local-processing/tickets")]
    public Task<IActionResult> LocalProcessingTickets(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Post, "local-processing/tickets", Request, ct);

    /// <summary>Draw a random unprocessed ticket from the local-processing queue.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("local-processing/random-ticket")]
    public Task<IActionResult> LocalProcessingRandomTicket(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Post, "local-processing/random-ticket", Request, ct);

    /// <summary>Mark one or more tickets as processed in the local-processing queue.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("local-processing/set-processed")]
    public Task<IActionResult> LocalProcessingSetProcessed(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Post, "local-processing/set-processed", Request, ct);

    /// <summary>Reset the local-processing queue (clear every "processed" flag).</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("local-processing/clear-all-processed")]
    public Task<IActionResult> LocalProcessingClearAllProcessed(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Post, "local-processing/clear-all-processed", Request, ct);

    // ── Ingestion ────────────────────────────────────────────────────────

    /// <summary>
    /// Triggers a synchronous ingestion run on the Jira source. The optional
    /// <paramref name="jiraProject"/> parameter scopes ingestion to a single
    /// configured Jira project (forwarded as Jira's <c>?project=</c> query
    /// parameter; the consumer-facing name is <c>jira-project</c> to
    /// disambiguate from "GitHub project").
    /// </summary>
    /// <param name="type">Sync type — <c>incremental</c> (default), <c>full</c>, or <c>rebuild</c>.</param>
    /// <param name="jiraProject">Optional Jira project key to scope the run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Ingestion completed synchronously.</response>
    /// <response code="202">Ingestion queued (when configured to run asynchronously).</response>
    [HttpPost("ingest")]
    public Task<IActionResult> Ingest(
        [FromQuery] string? type,
        [FromQuery(Name = "jira-project")] string? jiraProject,
        CancellationToken ct)
        => ForwardIngest("ingest", type, jiraProject, ct);

    /// <summary>
    /// Queues an asynchronous ingestion run on the Jira source. See
    /// <see cref="Ingest"/> for the <paramref name="jiraProject"/> semantics.
    /// </summary>
    /// <param name="type">Sync type — <c>incremental</c> (default), <c>full</c>, or <c>rebuild</c>.</param>
    /// <param name="jiraProject">Optional Jira project key to scope the run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="202">Ingestion queued.</response>
    [HttpPost("ingest/trigger")]
    public Task<IActionResult> IngestTrigger(
        [FromQuery] string? type,
        [FromQuery(Name = "jira-project")] string? jiraProject,
        CancellationToken ct)
        => ForwardIngest("ingest/trigger", type, jiraProject, ct);

    private Task<IActionResult> ForwardIngest(string path, string? type, string? jiraProject, CancellationToken ct)
    {
        // Translate consumer-facing `jira-project` back to Jira's `project` query parameter.
        List<string> parts = [];
        if (!string.IsNullOrEmpty(type)) parts.Add($"type={Uri.EscapeDataString(type)}");
        if (!string.IsNullOrEmpty(jiraProject)) parts.Add($"project={Uri.EscapeDataString(jiraProject)}");
        string? query = parts.Count > 0 ? "?" + string.Join('&', parts) : null;
        return httpClient.ProxyAsync(Source, HttpMethod.Post, path, Request, ct, overrideQueryString: query);
    }

    /// <summary>
    /// Rebuild the Jira source database from the file-system cache (no upstream
    /// re-fetch). The CLI surface exposes this operation as the <c>reingest</c>
    /// verb (renamed from <c>rebuild</c> in the 2026-04 sync); the wire path
    /// retains its historical name.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("rebuild")]
    public Task<IActionResult> Rebuild(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Post, "rebuild", Request, ct);

    /// <summary>
    /// Receive a peer-ingestion notification from the orchestrator's
    /// <c>POST /api/v1/notify-ingestion</c> fan-out and trigger a cross-
    /// reference re-scan against the freshly-updated peer. Tagged
    /// <c>ingestion-notifications</c>.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("notify-peer")]
    public Task<IActionResult> NotifyPeer(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Post, "notify-peer", Request, ct);
}
