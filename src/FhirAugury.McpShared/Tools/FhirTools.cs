using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace FhirAugury.McpShared.Tools;

/// <summary>
/// MCP tools for the FHIR specification source. Each tool forwards to the FHIR
/// source through the orchestrator (<c>/api/v1/fhir/...</c>). The release
/// defaults to <c>default</c> (resolved server-side to the latest stable
/// release) when omitted.
/// </summary>
[McpServerToolType]
public static class FhirTools
{
    [McpServerTool, Description("List the FHIR releases available in the spec database.")]
    public static Task<string> ListFhirReleases(
        IHttpClientFactory httpClientFactory, CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory, "/api/v1/fhir/releases", cancellationToken);

    [McpServerTool, Description("List the resources for a FHIR release (filters: workGroup, maturity, status).")]
    public static Task<string> ListFhirResources(
        IHttpClientFactory httpClientFactory,
        [Description("Release token, e.g. R5, 5.0, DSTU2 (default = latest stable)")] string release = "default",
        [Description("Optional work group filter")] string? workGroup = null,
        [Description("Optional FHIR maturity (FMM) filter")] int? maturity = null,
        [Description("Optional status filter")] string? status = null,
        CancellationToken cancellationToken = default)
    {
        List<string> q = [];
        if (workGroup != null) q.Add($"workGroup={Esc(workGroup)}");
        if (maturity != null) q.Add($"maturity={maturity.Value}");
        if (status != null) q.Add($"status={Esc(status)}");
        return CallAsync(httpClientFactory, $"/api/v1/fhir/{Esc(release)}/resources{Query(q)}", cancellationToken);
    }

    [McpServerTool, Description("List the data types (complex + primitive) for a FHIR release.")]
    public static Task<string> ListFhirDataTypes(
        IHttpClientFactory httpClientFactory,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory, $"/api/v1/fhir/{Esc(release)}/datatypes", cancellationToken);

    [McpServerTool, Description("List the profiles for a FHIR release.")]
    public static Task<string> ListFhirProfiles(
        IHttpClientFactory httpClientFactory,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory, $"/api/v1/fhir/{Esc(release)}/profiles", cancellationToken);

    [McpServerTool, Description("List the interfaces for a FHIR release (filters: workGroup, maturity, status).")]
    public static Task<string> ListFhirInterfaces(
        IHttpClientFactory httpClientFactory,
        [Description("Release token (default = latest stable)")] string release = "default",
        [Description("Optional work group filter")] string? workGroup = null,
        [Description("Optional FHIR maturity (FMM) filter")] int? maturity = null,
        [Description("Optional status filter")] string? status = null,
        CancellationToken cancellationToken = default)
    {
        List<string> q = [];
        if (workGroup != null) q.Add($"workGroup={Esc(workGroup)}");
        if (maturity != null) q.Add($"maturity={maturity.Value}");
        if (status != null) q.Add($"status={Esc(status)}");
        return CallAsync(httpClientFactory, $"/api/v1/fhir/{Esc(release)}/interfaces{Query(q)}", cancellationToken);
    }

    [McpServerTool, Description("Get a structure's metadata and full element tree.")]
    public static Task<string> GetFhirStructure(
        IHttpClientFactory httpClientFactory,
        [Description("Structure name, e.g. Observation")] string name,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory, $"/api/v1/fhir/{Esc(release)}/structures/{Esc(name)}", cancellationToken);

    [McpServerTool, Description("Get a single element of a structure by dotted path (e.g. Patient.contact.name).")]
    public static Task<string> GetFhirElement(
        IHttpClientFactory httpClientFactory,
        [Description("Structure name, e.g. Patient")] string name,
        [Description("Dotted element path, e.g. Patient.contact.name")] string path,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory,
            $"/api/v1/fhir/{Esc(release)}/structures/{Esc(name)}/elements/{path}", cancellationToken);

    [McpServerTool, Description("List a structure's elements (flat, or the nested tree when nested=true).")]
    public static Task<string> ListFhirElements(
        IHttpClientFactory httpClientFactory,
        [Description("Structure name, e.g. Observation")] string name,
        [Description("Release token (default = latest stable)")] string release = "default",
        [Description("Return the nested element tree instead of the flat list")] bool? nested = null,
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory,
            $"/api/v1/fhir/{Esc(release)}/structures/{Esc(name)}/elements" + (nested == true ? "?nested=true" : string.Empty),
            cancellationToken);

    [McpServerTool, Description("List the code systems for a FHIR release.")]
    public static Task<string> ListFhirCodeSystems(
        IHttpClientFactory httpClientFactory,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory, $"/api/v1/fhir/{Esc(release)}/codesystems", cancellationToken);

    [McpServerTool, Description("Look up a code system resource by canonical URL or id.")]
    public static Task<string> GetFhirCodeSystem(
        IHttpClientFactory httpClientFactory,
        [Description("Code system canonical URL or id")] string system,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory,
            $"/api/v1/fhir/{Esc(release)}/codesystems/lookup?system={Esc(system)}", cancellationToken);

    [McpServerTool, Description("Look up a single concept by code system URL/id and code.")]
    public static Task<string> LookupFhirCode(
        IHttpClientFactory httpClientFactory,
        [Description("Code system canonical URL or id")] string system,
        [Description("The code to look up")] string code,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory,
            $"/api/v1/fhir/{Esc(release)}/codesystems/concept?system={Esc(system)}&code={Esc(code)}",
            cancellationToken);

    [McpServerTool, Description("List a code system's concepts by URL/id (optionally as a hierarchy).")]
    public static Task<string> ListFhirCodeSystemConcepts(
        IHttpClientFactory httpClientFactory,
        [Description("Code system canonical URL or id")] string system,
        [Description("Release token (default = latest stable)")] string release = "default",
        [Description("Return the concepts as a hierarchy")] bool? hierarchical = null,
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory,
            $"/api/v1/fhir/{Esc(release)}/codesystems/concepts?system={Esc(system)}"
            + (hierarchical == true ? "&hierarchical=true" : string.Empty),
            cancellationToken);

    [McpServerTool, Description("List the value sets for a FHIR release.")]
    public static Task<string> ListFhirValueSets(
        IHttpClientFactory httpClientFactory,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory, $"/api/v1/fhir/{Esc(release)}/valuesets", cancellationToken);

    [McpServerTool, Description("Look up a value set resource by canonical URL or id.")]
    public static Task<string> GetFhirValueSet(
        IHttpClientFactory httpClientFactory,
        [Description("Value set canonical URL or id")] string url,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory,
            $"/api/v1/fhir/{Esc(release)}/valuesets/lookup?url={Esc(url)}", cancellationToken);

    [McpServerTool, Description("Get a value set's expanded concept list by URL/id.")]
    public static Task<string> ExpandFhirValueSet(
        IHttpClientFactory httpClientFactory,
        [Description("Value set canonical URL or id")] string url,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory,
            $"/api/v1/fhir/{Esc(release)}/valuesets/concepts?url={Esc(url)}", cancellationToken);

    [McpServerTool, Description("Get the elements that bind to a value set (reverse bindings) by URL/id.")]
    public static Task<string> GetFhirValueSetBindings(
        IHttpClientFactory httpClientFactory,
        [Description("Value set canonical URL or id")] string url,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory,
            $"/api/v1/fhir/{Esc(release)}/valuesets/bindings?url={Esc(url)}", cancellationToken);

    [McpServerTool, Description("List the operations for a FHIR release.")]
    public static Task<string> ListFhirOperations(
        IHttpClientFactory httpClientFactory,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory, $"/api/v1/fhir/{Esc(release)}/operations", cancellationToken);

    [McpServerTool, Description("Get a single operation by id or code (e.g. expand).")]
    public static Task<string> GetFhirOperation(
        IHttpClientFactory httpClientFactory,
        [Description("Operation id or code, e.g. expand")] string idOrCode,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory,
            $"/api/v1/fhir/{Esc(release)}/operations/{Esc(idOrCode)}", cancellationToken);

    [McpServerTool, Description("List search parameters for a FHIR release (filters: base resource, code).")]
    public static Task<string> ListFhirSearchParameters(
        IHttpClientFactory httpClientFactory,
        [Description("Release token (default = latest stable)")] string release = "default",
        [Description("Optional base resource filter, e.g. Observation")] string? baseResource = null,
        [Description("Optional search parameter code filter")] string? code = null,
        CancellationToken cancellationToken = default)
    {
        List<string> q = [];
        if (baseResource != null) q.Add($"base={Esc(baseResource)}");
        if (code != null) q.Add($"code={Esc(code)}");
        return CallAsync(httpClientFactory,
            $"/api/v1/fhir/{Esc(release)}/searchparameters{Query(q)}", cancellationToken);
    }

    [McpServerTool, Description("Get a single search parameter by id or code (e.g. Observation-code).")]
    public static Task<string> GetFhirSearchParameter(
        IHttpClientFactory httpClientFactory,
        [Description("Search parameter id or code, e.g. Observation-code")] string idOrCode,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory,
            $"/api/v1/fhir/{Esc(release)}/searchparameters/{Esc(idOrCode)}", cancellationToken);

    [McpServerTool, Description("Resolve a canonical URL to an artifact (structure / code system / value set / operation / search parameter).")]
    public static Task<string> ResolveFhirCanonical(
        IHttpClientFactory httpClientFactory,
        [Description("Canonical URL (versioned or unversioned)")] string url,
        [Description("Release token (default = latest stable)")] string release = "default",
        CancellationToken cancellationToken = default)
        => CallAsync(httpClientFactory,
            $"/api/v1/fhir/{Esc(release)}/resolve?url={Esc(url)}", cancellationToken);

    [McpServerTool, Description("Search FHIR artifacts by name/title/description (FTS).")]
    public static Task<string> SearchFhir(
        IHttpClientFactory httpClientFactory,
        [Description("Search query")] string q,
        [Description("Release token (default = latest stable)")] string release = "default",
        [Description("Optional comma-separated kinds, e.g. structure,valueset")] string? types = null,
        CancellationToken cancellationToken = default)
    {
        List<string> query = [$"q={Esc(q)}"];
        if (types != null) query.Add($"types={Esc(types)}");
        return CallAsync(httpClientFactory, $"/api/v1/fhir/{Esc(release)}/search{Query(query)}", cancellationToken);
    }

    private static async Task<string> CallAsync(
        IHttpClientFactory httpClientFactory, string url, CancellationToken cancellationToken)
    {
        try
        {
            HttpClient client = httpClientFactory.CreateClient("orchestrator");
            JsonElement root = await UnifiedTools.GetJsonAsync(client, url, cancellationToken);
            return FormatJson(root);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private static string Query(List<string> parts)
        => parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);

    private static string FormatJson(JsonElement root) =>
        $"```json\n{JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true })}\n```";
}
