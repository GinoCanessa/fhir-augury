using System.Collections.Generic;
using System.Net.Http;

namespace FhirAugury.DevUi.Services.ApiCatalog.Catalogs;

/// <summary>
/// API-tester catalog for the FHIR specification source
/// (<c>FhirAugury.Source.Fhir</c>, :5195). Covers the source's 25 routes:
/// lifecycle, releases, release-scoped structures/terminology/operations/
/// search-parameters, and the resolve/search cross-cutting endpoints.
/// </summary>
public static class FhirCatalog
{
    public static IReadOnlyList<ApiEndpointDescriptor> Build()
    {
        // Shared {release} path token reused by every release-scoped entry.
        ApiParameter release = new("release", ApiParameterKind.Path, Required: true,
            Placeholder: "R5", HelpText: "Release token — R5/R4B/R4/STU3/DSTU2/R6");

        List<ApiEndpointDescriptor> list =
        [
            .. SharedSourceEndpoints.LifecycleEndpoints(),

            // Releases
            new ApiEndpointDescriptor("releases.list", "List releases", "Releases",
                HttpMethod.Get, "api/v1/releases", []),

            // Structures
            new ApiEndpointDescriptor("structures.resources", "Resources", "Structures",
                HttpMethod.Get, "api/v1/{release}/resources",
                [
                    release,
                    new ApiParameter("workGroup", ApiParameterKind.Query, Required: false),
                    new ApiParameter("maturity", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("status", ApiParameterKind.Query, Required: false),
                    new ApiParameter("kind", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("structures.datatypes", "Data types", "Structures",
                HttpMethod.Get, "api/v1/{release}/datatypes",
                [
                    release,
                    new ApiParameter("workGroup", ApiParameterKind.Query, Required: false),
                    new ApiParameter("maturity", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("status", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("structures.profiles", "Profiles", "Structures",
                HttpMethod.Get, "api/v1/{release}/profiles",
                [
                    release,
                    new ApiParameter("workGroup", ApiParameterKind.Query, Required: false),
                    new ApiParameter("maturity", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("status", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("structures.interfaces", "Interfaces", "Structures",
                HttpMethod.Get, "api/v1/{release}/interfaces",
                [
                    release,
                    new ApiParameter("workGroup", ApiParameterKind.Query, Required: false),
                    new ApiParameter("maturity", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("status", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("structures.get", "Get structure", "Structures",
                HttpMethod.Get, "api/v1/{release}/structures/{name}",
                [
                    release,
                    new ApiParameter("name", ApiParameterKind.Path, Required: true,
                        Placeholder: "Patient"),
                ]),
            new ApiEndpointDescriptor("structures.elements", "Structure elements", "Structures",
                HttpMethod.Get, "api/v1/{release}/structures/{name}/elements",
                [
                    release,
                    new ApiParameter("name", ApiParameterKind.Path, Required: true,
                        Placeholder: "Patient"),
                    new ApiParameter("nested", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Bool),
                ]),
            new ApiEndpointDescriptor("structures.element", "Get element", "Structures",
                HttpMethod.Get, "api/v1/{release}/structures/{name}/elements/{*path}",
                [
                    release,
                    new ApiParameter("name", ApiParameterKind.Path, Required: true,
                        Placeholder: "Patient"),
                    new ApiParameter("path", ApiParameterKind.Path, Required: true,
                        Placeholder: "Patient.name", IsCatchAll: true),
                ]),

            // Terminology
            new ApiEndpointDescriptor("codesystems.list", "List code systems", "Terminology",
                HttpMethod.Get, "api/v1/{release}/codesystems", [release]),
            new ApiEndpointDescriptor("codesystems.lookup", "Look up code system", "Terminology",
                HttpMethod.Get, "api/v1/{release}/codesystems/lookup",
                [
                    release,
                    new ApiParameter("system", ApiParameterKind.Query, Required: false,
                        Placeholder: "canonical URL or id"),
                ]),
            new ApiEndpointDescriptor("codesystems.concepts", "Code system concepts", "Terminology",
                HttpMethod.Get, "api/v1/{release}/codesystems/concepts",
                [
                    release,
                    new ApiParameter("system", ApiParameterKind.Query, Required: false,
                        Placeholder: "canonical URL or id"),
                    new ApiParameter("hierarchical", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Bool),
                ]),
            new ApiEndpointDescriptor("codesystems.concept", "Look up concept", "Terminology",
                HttpMethod.Get, "api/v1/{release}/codesystems/concept",
                [
                    release,
                    new ApiParameter("system", ApiParameterKind.Query, Required: false,
                        Placeholder: "canonical URL or id"),
                    new ApiParameter("code", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("valuesets.list", "List value sets", "Terminology",
                HttpMethod.Get, "api/v1/{release}/valuesets", [release]),
            new ApiEndpointDescriptor("valuesets.lookup", "Look up value set", "Terminology",
                HttpMethod.Get, "api/v1/{release}/valuesets/lookup",
                [
                    release,
                    new ApiParameter("url", ApiParameterKind.Query, Required: false,
                        Placeholder: "canonical URL or id"),
                ]),
            new ApiEndpointDescriptor("valuesets.concepts", "Value set concepts", "Terminology",
                HttpMethod.Get, "api/v1/{release}/valuesets/concepts",
                [
                    release,
                    new ApiParameter("url", ApiParameterKind.Query, Required: false,
                        Placeholder: "canonical URL or id"),
                ]),
            new ApiEndpointDescriptor("valuesets.bindings", "Value set bindings", "Terminology",
                HttpMethod.Get, "api/v1/{release}/valuesets/bindings",
                [
                    release,
                    new ApiParameter("url", ApiParameterKind.Query, Required: false,
                        Placeholder: "canonical URL or id"),
                ]),

            // Operations
            new ApiEndpointDescriptor("operations.list", "List operations", "Operations",
                HttpMethod.Get, "api/v1/{release}/operations", [release]),
            new ApiEndpointDescriptor("operations.get", "Get operation", "Operations",
                HttpMethod.Get, "api/v1/{release}/operations/{idOrCode}",
                [
                    release,
                    new ApiParameter("idOrCode", ApiParameterKind.Path, Required: true,
                        Placeholder: "id, code, or name"),
                ]),

            // Search Parameters
            new ApiEndpointDescriptor("searchparameters.list", "List search parameters", "Search Parameters",
                HttpMethod.Get, "api/v1/{release}/searchparameters",
                [
                    release,
                    new ApiParameter("base", ApiParameterKind.Query, Required: false,
                        Placeholder: "base resource, e.g. Patient"),
                    new ApiParameter("code", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("searchparameters.get", "Get search parameter", "Search Parameters",
                HttpMethod.Get, "api/v1/{release}/searchparameters/{idOrCode}",
                [
                    release,
                    new ApiParameter("idOrCode", ApiParameterKind.Path, Required: true,
                        Placeholder: "id, code, or name"),
                ]),

            // Resolve & Search
            new ApiEndpointDescriptor("resolve.get", "Resolve canonical URL", "Resolve & Search",
                HttpMethod.Get, "api/v1/{release}/resolve",
                [
                    release,
                    new ApiParameter("url", ApiParameterKind.Query, Required: false,
                        Placeholder: "canonical URL"),
                ]),
            new ApiEndpointDescriptor("search.query", "Search artifacts", "Resolve & Search",
                HttpMethod.Get, "api/v1/{release}/search",
                [
                    release,
                    new ApiParameter("q", ApiParameterKind.Query, Required: false,
                        Placeholder: "search text"),
                    new ApiParameter("types", ApiParameterKind.Query, Required: false,
                        Placeholder: "comma-separated kinds"),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Int),
                ]),
        ];

        return list;
    }
}
