using System.Collections.Generic;
using System.Linq;
using System.Net.Http;

namespace FhirAugury.DevUi.Services.ApiCatalog.Catalogs;

public static class OrchestratorCatalog
{
    public static IReadOnlyList<ApiEndpointDescriptor> Build()
    {
        List<ApiEndpointDescriptor> list =
        [
            .. OrchestratorOwnEndpoints(),
            .. ProjectSourceCatalog("jira", "Jira", JiraCatalog.Build()),
            .. ProjectSourceCatalog("zulip", "Zulip", ZulipCatalog.Build()),
            .. ProjectSourceCatalog("confluence", "Confluence", ConfluenceCatalog.Build()),
            .. ProjectSourceCatalog("github", "GitHub", GitHubCatalog.Build()),
        ];
        return list;
    }

    /// <summary>
    /// Endpoints native to the orchestrator (content fan-out, services,
    /// stats, ingestion roll-up, lifecycle).
    /// </summary>
    private static IReadOnlyList<ApiEndpointDescriptor> OrchestratorOwnEndpoints() =>
    [
        new ApiEndpointDescriptor(
            Id: "content.search",
            DisplayName: "Content Search",
            Group: "Content",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/content/search",
            Parameters:
            [
                new ApiParameter("values", ApiParameterKind.Query, Required: true,
                    Placeholder: "comma-separated values", Repeatable: true),
                new ApiParameter("sources", ApiParameterKind.Query, Required: false, Repeatable: true),
                new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "20",
                    ValueType: ApiParameterValueType.Int),
                new ApiParameter("sort", ApiParameterKind.Query, Required: false,
                    Placeholder: "score|date"),
            ]),

        new ApiEndpointDescriptor(
            Id: "content.refers-to",
            DisplayName: "Refers To",
            Group: "Content",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/content/refers-to",
            Parameters:
            [
                new ApiParameter("value", ApiParameterKind.Query, Required: true),
                new ApiParameter("sourceType", ApiParameterKind.Query, Required: false),
                new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "20",
                    ValueType: ApiParameterValueType.Int),
                new ApiParameter("sort", ApiParameterKind.Query, Required: false),
            ]),

        new ApiEndpointDescriptor(
            Id: "content.referred-by",
            DisplayName: "Referred By",
            Group: "Content",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/content/referred-by",
            Parameters:
            [
                new ApiParameter("value", ApiParameterKind.Query, Required: true),
                new ApiParameter("sourceType", ApiParameterKind.Query, Required: false),
                new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "20",
                    ValueType: ApiParameterValueType.Int),
                new ApiParameter("sort", ApiParameterKind.Query, Required: false),
            ]),

        new ApiEndpointDescriptor(
            Id: "content.cross-referenced",
            DisplayName: "Cross-Referenced",
            Group: "Content",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/content/cross-referenced",
            Parameters:
            [
                new ApiParameter("value", ApiParameterKind.Query, Required: true),
                new ApiParameter("sourceType", ApiParameterKind.Query, Required: false),
                new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "20",
                    ValueType: ApiParameterValueType.Int),
                new ApiParameter("sort", ApiParameterKind.Query, Required: false),
            ]),

        new ApiEndpointDescriptor(
            Id: "content.get-item",
            DisplayName: "Get Item",
            Group: "Content",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/content/item/{source}/{**id}",
            Parameters:
            [
                new ApiParameter("source", ApiParameterKind.Path, Required: true),
                new ApiParameter("id", ApiParameterKind.Path, Required: true, IsCatchAll: true),
                new ApiParameter("includeContent", ApiParameterKind.Query, Required: false,
                    DefaultValue: "true", ValueType: ApiParameterValueType.Bool),
                new ApiParameter("includeComments", ApiParameterKind.Query, Required: false,
                    DefaultValue: "false", ValueType: ApiParameterValueType.Bool),
                new ApiParameter("includeSnapshot", ApiParameterKind.Query, Required: false,
                    DefaultValue: "false", ValueType: ApiParameterValueType.Bool),
            ]),

        new ApiEndpointDescriptor(
            Id: "content.keywords",
            DisplayName: "Keywords",
            Group: "Content",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/content/keywords/{source}/{**id}",
            Parameters:
            [
                new ApiParameter("source", ApiParameterKind.Path, Required: true),
                new ApiParameter("id", ApiParameterKind.Path, Required: true, IsCatchAll: true),
                new ApiParameter("keywordType", ApiParameterKind.Query, Required: false),
                new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "20",
                    ValueType: ApiParameterValueType.Int),
            ]),

        new ApiEndpointDescriptor(
            Id: "content.related-by-keyword",
            DisplayName: "Related by Keyword",
            Group: "Content",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/content/related-by-keyword/{source}/{**id}",
            Parameters:
            [
                new ApiParameter("source", ApiParameterKind.Path, Required: true),
                new ApiParameter("id", ApiParameterKind.Path, Required: true, IsCatchAll: true),
                new ApiParameter("minScore", ApiParameterKind.Query, Required: false,
                    DefaultValue: "0.1", ValueType: ApiParameterValueType.Double),
                new ApiParameter("keywordType", ApiParameterKind.Query, Required: false),
                new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "20",
                    ValueType: ApiParameterValueType.Int),
            ]),

        new ApiEndpointDescriptor(
            Id: "services.list",
            DisplayName: "Services Status",
            Group: "Services",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/services",
            Parameters: []),

        new ApiEndpointDescriptor(
            Id: "stats.aggregate",
            DisplayName: "Aggregate Stats",
            Group: "Services",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/stats",
            Parameters: []),

        new ApiEndpointDescriptor(
            Id: "ingestion.rebuild-index",
            DisplayName: "Rebuild Index (all sources)",
            Group: "Ingestion",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/rebuild-index",
            Parameters:
            [
                new ApiParameter("type", ApiParameterKind.Query, Required: false, DefaultValue: "all",
                    Placeholder: "all|bm25|fts|cross-refs|lookup-tables"),
                new ApiParameter("sources", ApiParameterKind.Query, Required: false),
            ],
            Destructive: true,
            Description: "Triggers index rebuild on every enabled source."),

        new ApiEndpointDescriptor(
            Id: "ingestion.trigger",
            DisplayName: "Trigger Sync (all sources)",
            Group: "Ingestion",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/ingest/trigger",
            Parameters:
            [
                new ApiParameter("type", ApiParameterKind.Query, Required: false, DefaultValue: "incremental",
                    Placeholder: "incremental|full"),
            ]),

        new ApiEndpointDescriptor(
            Id: "lifecycle.health",
            DisplayName: "Health (orchestrator)",
            Group: "Lifecycle",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/health",
            Parameters: [],
            Description: "Cheap orchestrator liveness probe. Always 200; performs no outbound calls."),

        new ApiEndpointDescriptor(
            Id: "lifecycle.status",
            DisplayName: "Status (orchestrator)",
            Group: "Lifecycle",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/status",
            Parameters: [],
            Description: "Orchestrator-local readiness. 200 when source registry hydrated, 503 otherwise. Does not call sources."),
    ];

    /// <summary>
    /// Projects every endpoint from a per-source catalog into its
    /// orchestrator typed-proxy URL. The source catalog uses URLs of the
    /// form <c>api/v1/items</c>; the typed proxy exposes them at
    /// <c>api/v1/{sourceName}/items</c>. Endpoints that the orchestrator
    /// already exposes natively (search, content fan-out, lifecycle health,
    /// aggregate stats, services list) are filtered out so the orchestrator
    /// catalog does not double-list them.
    /// </summary>
    private static IEnumerable<ApiEndpointDescriptor> ProjectSourceCatalog(
        string sourceName, string displayPrefix, IReadOnlyList<ApiEndpointDescriptor> sourceEntries)
    {
        const string ApiV1 = "api/v1";
        string typedPrefix = $"api/v1/{sourceName}";

        foreach (ApiEndpointDescriptor entry in sourceEntries)
        {
            // Skip endpoints surfaced by the orchestrator directly rather than
            // through a typed source proxy — the content fan-out subtree, the
            // orchestrator's own lifecycle probes, and the aggregate stats /
            // services endpoints. Each source still re-exports its own
            // copies under its DevUI tab; we just don't repeat them under
            // the orchestrator tab.
            if (entry.PathTemplate.StartsWith("api/v1/content/", System.StringComparison.Ordinal)
                || string.Equals(entry.PathTemplate, "api/v1/content/search", System.StringComparison.Ordinal)
                || string.Equals(entry.PathTemplate, "api/v1/health", System.StringComparison.Ordinal)
                || string.Equals(entry.PathTemplate, "api/v1/status", System.StringComparison.Ordinal)
                || string.Equals(entry.PathTemplate, "api/v1/stats", System.StringComparison.Ordinal))
            {
                continue;
            }

            string remainder = entry.PathTemplate.StartsWith(ApiV1, System.StringComparison.Ordinal)
                ? entry.PathTemplate.Substring(ApiV1.Length)
                : entry.PathTemplate;
            if (!remainder.StartsWith("/", System.StringComparison.Ordinal))
                remainder = "/" + remainder;
            string newPath = typedPrefix + remainder;

            yield return entry with
            {
                Id = $"{sourceName}.{entry.Id}",
                Group = $"{displayPrefix} / {entry.Group}",
                PathTemplate = newPath,
                Parameters = entry.Parameters.ToList(),
            };
        }
    }
}
