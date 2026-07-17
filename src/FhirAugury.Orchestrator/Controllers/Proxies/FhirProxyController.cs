using FhirAugury.Orchestrator.Routing;
using Microsoft.AspNetCore.Mvc;

namespace FhirAugury.Orchestrator.Controllers.Proxies;

/// <summary>
/// Typed orchestrator proxy for the FHIR specification source. Forwards every
/// request to <c>FhirAugury.Source.Fhir</c> via <see cref="SourceHttpClient"/>,
/// preserving the query string (so the canonical-URL query parameters
/// <c>?system=</c>/<c>?url=</c> and search <c>?q=</c>/<c>?types=</c> pass through
/// unchanged), request headers, response status, body, and ETag/Last-Modified.
/// </summary>
/// <remarks>
/// Each action is a 1:1 passthrough to the corresponding upstream FHIR source
/// route. Common response codes: <c>200 OK</c> for success, <c>404 Not Found</c>
/// for unknown releases/artifacts, <c>503 Service Unavailable</c> when the source
/// is unreachable.
/// </remarks>
[ApiController]
[Route("api/v1/fhir")]
public class FhirProxyController(SourceHttpClient httpClient) : ControllerBase
{
    private const string Source = "fhir";

    private static string Esc(string value) => Uri.EscapeDataString(value);

    // ── Releases & lifecycle ─────────────────────────────────────────────

    /// <summary>List the FHIR releases available in the spec database.</summary>
    [HttpGet("releases")]
    public Task<IActionResult> Releases(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "releases", Request, ct);

    /// <summary>FHIR source statistics.</summary>
    [HttpGet("stats")]
    public Task<IActionResult> Stats(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "stats", Request, ct);

    /// <summary>FHIR source health.</summary>
    [HttpGet("health")]
    public Task<IActionResult> Health(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "health", Request, ct);

    /// <summary>FHIR source status (indexes).</summary>
    [HttpGet("status")]
    public Task<IActionResult> Status(CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, "status", Request, ct);

    // ── Structures ───────────────────────────────────────────────────────

    /// <summary>List resources for a release.</summary>
    [HttpGet("{release}/resources")]
    public Task<IActionResult> Resources(string release,
        [FromQuery] string? workGroup, [FromQuery] int? maturity, [FromQuery] string? status,
        [FromQuery] string? kind, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/resources", Request, ct);

    /// <summary>List data types (complex + primitive) for a release.</summary>
    [HttpGet("{release}/datatypes")]
    public Task<IActionResult> DataTypes(string release, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/datatypes", Request, ct);

    /// <summary>List profiles for a release.</summary>
    [HttpGet("{release}/profiles")]
    public Task<IActionResult> Profiles(string release, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/profiles", Request, ct);

    /// <summary>List interfaces for a release.</summary>
    [HttpGet("{release}/interfaces")]
    public Task<IActionResult> Interfaces(string release, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/interfaces", Request, ct);

    /// <summary>Get a structure's metadata and element tree.</summary>
    [HttpGet("{release}/structures/{name}")]
    public Task<IActionResult> Structure(string release, string name, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/structures/{Esc(name)}", Request, ct);

    /// <summary>Get a structure's elements (flat or nested).</summary>
    [HttpGet("{release}/structures/{name}/elements")]
    public Task<IActionResult> Elements(string release, string name, [FromQuery] bool? nested, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/structures/{Esc(name)}/elements", Request, ct);

    /// <summary>Get a single element by dotted path.</summary>
    [HttpGet("{release}/structures/{name}/elements/{*path}")]
    public Task<IActionResult> Element(string release, string name, string path, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/structures/{Esc(name)}/elements/{path}", Request, ct);

    // ── Code systems ─────────────────────────────────────────────────────

    /// <summary>List code systems for a release.</summary>
    [HttpGet("{release}/codesystems")]
    public Task<IActionResult> CodeSystems(string release, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/codesystems", Request, ct);

    /// <summary>Look up a code system by canonical URL or id (<c>?system=</c>).</summary>
    [HttpGet("{release}/codesystems/lookup")]
    public Task<IActionResult> CodeSystemLookup(string release, [FromQuery] string? system, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/codesystems/lookup", Request, ct);

    /// <summary>List a code system's concepts (<c>?system=</c>, <c>?hierarchical=</c>).</summary>
    [HttpGet("{release}/codesystems/concepts")]
    public Task<IActionResult> CodeSystemConcepts(string release,
        [FromQuery] string? system, [FromQuery] bool? hierarchical, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/codesystems/concepts", Request, ct);

    /// <summary>Look up a single concept (<c>?system=</c>&amp;<c>code=</c>).</summary>
    [HttpGet("{release}/codesystems/concept")]
    public Task<IActionResult> CodeSystemConcept(string release,
        [FromQuery] string? system, [FromQuery] string? code, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/codesystems/concept", Request, ct);

    // ── Value sets ───────────────────────────────────────────────────────

    /// <summary>List value sets for a release.</summary>
    [HttpGet("{release}/valuesets")]
    public Task<IActionResult> ValueSets(string release, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/valuesets", Request, ct);

    /// <summary>Look up a value set by canonical URL or id (<c>?url=</c>).</summary>
    [HttpGet("{release}/valuesets/lookup")]
    public Task<IActionResult> ValueSetLookup(string release, [FromQuery] string? url, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/valuesets/lookup", Request, ct);

    /// <summary>Get a value set's expanded concepts (<c>?url=</c>).</summary>
    [HttpGet("{release}/valuesets/concepts")]
    public Task<IActionResult> ValueSetConcepts(string release, [FromQuery] string? url, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/valuesets/concepts", Request, ct);

    /// <summary>Get the elements that bind to a value set (<c>?url=</c>).</summary>
    [HttpGet("{release}/valuesets/bindings")]
    public Task<IActionResult> ValueSetBindings(string release, [FromQuery] string? url, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/valuesets/bindings", Request, ct);

    // ── Operations & search parameters ───────────────────────────────────

    /// <summary>List operations for a release.</summary>
    [HttpGet("{release}/operations")]
    public Task<IActionResult> Operations(string release, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/operations", Request, ct);

    /// <summary>Get an operation (by id / code / name) and its parameters.</summary>
    [HttpGet("{release}/operations/{idOrCode}")]
    public Task<IActionResult> Operation(string release, string idOrCode, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/operations/{Esc(idOrCode)}", Request, ct);

    /// <summary>List search parameters for a release (<c>?base=</c>, <c>?code=</c>).</summary>
    [HttpGet("{release}/searchparameters")]
    public Task<IActionResult> SearchParameters(string release,
        [FromQuery(Name = "base")] string? baseResource, [FromQuery] string? code, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/searchparameters", Request, ct);

    /// <summary>Get a search parameter (by id / code / name).</summary>
    [HttpGet("{release}/searchparameters/{idOrCode}")]
    public Task<IActionResult> SearchParameter(string release, string idOrCode, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/searchparameters/{Esc(idOrCode)}", Request, ct);

    // ── Cross-cutting ────────────────────────────────────────────────────

    /// <summary>Resolve a canonical URL to an artifact (<c>?url=</c>).</summary>
    [HttpGet("{release}/resolve")]
    public Task<IActionResult> Resolve(string release, [FromQuery] string? url, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/resolve", Request, ct);

    /// <summary>Search artifacts by name/title/description (<c>?q=</c>, <c>?types=</c>).</summary>
    [HttpGet("{release}/search")]
    public Task<IActionResult> Search(string release,
        [FromQuery] string? q, [FromQuery] string? types, [FromQuery] int? limit, CancellationToken ct)
        => httpClient.ProxyAsync(Source, HttpMethod.Get, $"{Esc(release)}/search", Request, ct);
}
