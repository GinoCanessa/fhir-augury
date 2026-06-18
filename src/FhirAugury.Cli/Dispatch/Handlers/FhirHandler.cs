using System.Text;
using FhirAugury.Cli.Models;

namespace FhirAugury.Cli.Dispatch.Handlers;

/// <summary>
/// Handles the whole <c>fhir-*</c> command family by forwarding to the FHIR
/// source through the orchestrator. The release defaults to <c>default</c>
/// (resolved server-side to the latest stable release) when omitted.
/// </summary>
public static class FhirHandler
{
    public static async Task<object> HandleAsync(FhirRequest request, string orchestratorAddr, CancellationToken ct)
    {
        using HttpServiceClient client = new(orchestratorAddr);
        string path = BuildPath(request);
        return new { data = await client.GetFromOrchestratorAsync(path, ct) };
    }

    /// <summary>
    /// Builds the orchestrator URL for a <c>fhir-*</c> command. Extracted for
    /// unit-testing the route/query construction without an HTTP call.
    /// </summary>
    internal static string BuildPath(FhirRequest request)
    {
        string release = Uri.EscapeDataString(
            string.IsNullOrWhiteSpace(request.Release) ? "default" : request.Release);
        string command = request.Command.ToLowerInvariant();

        return command switch
        {
            "fhir-releases" => "/api/v1/fhir/releases",

            "fhir-resources" => $"/api/v1/fhir/{release}/resources" + StructureFilters(request),
            "fhir-datatypes" => $"/api/v1/fhir/{release}/datatypes",
            "fhir-profiles" => $"/api/v1/fhir/{release}/profiles",
            "fhir-interfaces" => $"/api/v1/fhir/{release}/interfaces" + StructureFilters(request),
            "fhir-structure" => $"/api/v1/fhir/{release}/structures/{Esc(Require(request.Name, "name"))}",
            "fhir-elements" => $"/api/v1/fhir/{release}/structures/{Esc(Require(request.Name, "name"))}/elements"
                + (request.Nested == true ? "?nested=true" : string.Empty),
            // Element-by-path passes `path` raw to match the source catch-all
            // route ({*path}) and the MCP GetFhirElement tool (neither escapes it).
            "fhir-element" => $"/api/v1/fhir/{release}/structures/{Esc(Require(request.Name, "name"))}/elements/{Require(request.Path, "path")}",

            "fhir-codesystems" => $"/api/v1/fhir/{release}/codesystems",
            "fhir-codesystem" => $"/api/v1/fhir/{release}/codesystems/lookup?system={Esc(Require(request.System, "system"))}",
            "fhir-codesystem-lookup" =>
                $"/api/v1/fhir/{release}/codesystems/concept" +
                $"?system={Esc(Require(request.System, "system"))}&code={Esc(Require(request.Code, "code"))}",
            "fhir-codesystem-concepts" =>
                $"/api/v1/fhir/{release}/codesystems/concepts?system={Esc(Require(request.System, "system"))}"
                + (request.Hierarchical == true ? "&hierarchical=true" : string.Empty),

            "fhir-valuesets" => $"/api/v1/fhir/{release}/valuesets",
            "fhir-valueset-expand" =>
                $"/api/v1/fhir/{release}/valuesets/concepts?url={Esc(Require(request.Url, "url"))}",
            "fhir-valueset-lookup" =>
                $"/api/v1/fhir/{release}/valuesets/lookup?url={Esc(Require(request.Url, "url"))}",
            "fhir-valueset-bindings" =>
                $"/api/v1/fhir/{release}/valuesets/bindings?url={Esc(Require(request.Url, "url"))}",

            "fhir-operations" => string.IsNullOrWhiteSpace(request.IdOrCode)
                ? $"/api/v1/fhir/{release}/operations"
                : $"/api/v1/fhir/{release}/operations/{Esc(request.IdOrCode)}",
            "fhir-operation" => $"/api/v1/fhir/{release}/operations/{Esc(Require(request.IdOrCode, "idOrCode"))}",
            "fhir-searchparameters" => $"/api/v1/fhir/{release}/searchparameters" + SearchParamFilters(request),
            "fhir-searchparameter" => $"/api/v1/fhir/{release}/searchparameters/{Esc(Require(request.IdOrCode, "idOrCode"))}",

            "fhir-resolve" => $"/api/v1/fhir/{release}/resolve?url={Esc(Require(request.Url, "url"))}",
            "fhir-search" => $"/api/v1/fhir/{release}/search" + SearchFilters(request),

            _ => throw new ArgumentException($"Unknown FHIR command: {request.Command}"),
        };
    }

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private static string Require(string? value, string field)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"FHIR command requires a '{field}'.")
            : value;

    private static string StructureFilters(FhirRequest r)
    {
        List<string> q = [];
        if (!string.IsNullOrWhiteSpace(r.WorkGroup)) q.Add($"workGroup={Esc(r.WorkGroup)}");
        if (r.Maturity is int m) q.Add($"maturity={m}");
        if (!string.IsNullOrWhiteSpace(r.Status)) q.Add($"status={Esc(r.Status)}");
        return ToQuery(q);
    }

    private static string SearchParamFilters(FhirRequest r)
    {
        List<string> q = [];
        if (!string.IsNullOrWhiteSpace(r.Base)) q.Add($"base={Esc(r.Base)}");
        if (!string.IsNullOrWhiteSpace(r.Code)) q.Add($"code={Esc(r.Code)}");
        return ToQuery(q);
    }

    private static string SearchFilters(FhirRequest r)
    {
        List<string> q = [$"q={Esc(Require(r.Query, "query"))}"];
        if (!string.IsNullOrWhiteSpace(r.Types)) q.Add($"types={Esc(r.Types)}");
        if (r.Limit is int limit) q.Add($"limit={limit}");
        return ToQuery(q);
    }

    private static string ToQuery(List<string> parts)
    {
        if (parts.Count == 0)
        {
            return string.Empty;
        }
        StringBuilder sb = new("?");
        sb.Append(string.Join('&', parts));
        return sb.ToString();
    }
}
