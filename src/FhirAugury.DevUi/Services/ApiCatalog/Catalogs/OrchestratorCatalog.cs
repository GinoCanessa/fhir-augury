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
            .. ProjectSourceCatalog("fhir", "FHIR", FhirCatalog.Build(), skipOrchestratorNative: false),
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

        // ── Processing services (start/stop/inspect processors) ───────────────
        new ApiEndpointDescriptor(
            Id: "processing.list",
            DisplayName: "List Processing Services",
            Group: "Processing",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/processing-services",
            Parameters: [],
            Description: "Lists configured processing services with cached health."),

        new ApiEndpointDescriptor(
            Id: "processing.status",
            DisplayName: "Processor Status",
            Group: "Processing",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/processing-services/{name}/status",
            Parameters: [ProcessingServiceName()]),

        new ApiEndpointDescriptor(
            Id: "processing.queue",
            DisplayName: "Processor Queue",
            Group: "Processing",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/processing-services/{name}/queue",
            Parameters: [ProcessingServiceName()]),

        new ApiEndpointDescriptor(
            Id: "processing.start",
            DisplayName: "Start Processor",
            Group: "Processing",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/processing-services/{name}/start",
            Parameters: [ProcessingServiceName()]),

        new ApiEndpointDescriptor(
            Id: "processing.stop",
            DisplayName: "Stop Processor",
            Group: "Processing",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/processing-services/{name}/stop",
            Parameters: [ProcessingServiceName()]),

        new ApiEndpointDescriptor(
            Id: "processing.health",
            DisplayName: "Processor Health",
            Group: "Processing",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/processing-services/{name}/health",
            Parameters: [ProcessingServiceName()]),

        // ── Services ──────────────────────────────────────────────────────────
        new ApiEndpointDescriptor(
            Id: "services.endpoints",
            DisplayName: "Service Endpoints",
            Group: "Services",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/endpoints",
            Parameters: [],
            Description: "Configured HTTP addresses for every enabled source."),

        // ── Ingestion ─────────────────────────────────────────────────────────
        new ApiEndpointDescriptor(
            Id: "ingestion.notify",
            DisplayName: "Notify Ingestion",
            Group: "Ingestion",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/notify-ingestion",
            Parameters:
            [
                new ApiParameter("body", ApiParameterKind.Body, Required: false,
                    DefaultValue: "{ \"source\": \"jira\", \"completedAt\": null }",
                    ValueType: ApiParameterValueType.Json),
            ],
            Destructive: true,
            Description: "Peer ingestion-completion notification; fans out to every other source."),

        // ── Meta / OpenAPI ────────────────────────────────────────────────────
        new ApiEndpointDescriptor(
            Id: "meta.openapi-json",
            DisplayName: "Merged OpenAPI (JSON)",
            Group: "Meta",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/openapi.json",
            Parameters:
            [
                new ApiParameter("include", ApiParameterKind.Query, Required: false,
                    Placeholder: "internal"),
            ]),

        new ApiEndpointDescriptor(
            Id: "meta.openapi-yaml",
            DisplayName: "Merged OpenAPI (YAML)",
            Group: "Meta",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/openapi.yaml",
            Parameters:
            [
                new ApiParameter("include", ApiParameterKind.Query, Required: false,
                    Placeholder: "internal"),
            ]),

        new ApiEndpointDescriptor(
            Id: "meta.source-orchestrator-openapi",
            DisplayName: "Orchestrator OpenAPI",
            Group: "Meta",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/source/orchestrator/openapi.json",
            Parameters: []),

        new ApiEndpointDescriptor(
            Id: "meta.source-openapi",
            DisplayName: "Source OpenAPI",
            Group: "Meta",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/source/{name}/openapi.json",
            Parameters:
            [
                new ApiParameter("name", ApiParameterKind.Path, Required: true,
                    Placeholder: "jira"),
            ]),

        new ApiEndpointDescriptor(
            Id: "meta.list-sources",
            DisplayName: "List Sources",
            Group: "Meta",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/source/orchestrator/list-sources",
            Parameters: []),
    ];

    /// <summary>
    /// The required <c>{name}</c> path parameter shared by the
    /// processing-services control endpoints.
    /// </summary>
    private static ApiParameter ProcessingServiceName() =>
        new("name", ApiParameterKind.Path, Required: true,
            Placeholder: "processor-jira-fhir-preparer");

    /// <summary>
    /// Projects every endpoint from a per-source catalog into its
    /// orchestrator typed-proxy URL. The source catalog uses URLs of the
    /// form <c>api/v1/items</c>; the typed proxy exposes them at
    /// <c>api/v1/{sourceName}/items</c>. Endpoints that the orchestrator
    /// already exposes natively (search, content fan-out, lifecycle health,
    /// aggregate stats, services list) are filtered out so the orchestrator
    /// catalog does not double-list them.
    /// </summary>
    /// <param name="skipOrchestratorNative">
    /// When <c>true</c> (the default), the proxied <c>health</c>/<c>status</c>/
    /// <c>stats</c> lifecycle routes are dropped because the orchestrator
    /// surfaces its own copies. The FHIR proxy uniquely re-exposes
    /// <c>api/v1/fhir/health|status|stats</c>, so it passes <c>false</c> to keep
    /// those projected entries. The <c>content/*</c> fan-out subtree is always
    /// dropped regardless of this flag.
    /// </param>
    private static IEnumerable<ApiEndpointDescriptor> ProjectSourceCatalog(
        string sourceName, string displayPrefix, IReadOnlyList<ApiEndpointDescriptor> sourceEntries,
        bool skipOrchestratorNative = true)
    {
        const string ApiV1 = "api/v1";
        string typedPrefix = $"api/v1/{sourceName}";

        foreach (ApiEndpointDescriptor entry in sourceEntries)
        {
            // The content fan-out subtree is always orchestrator-native and is
            // never projected under a source tab.
            if (entry.PathTemplate.StartsWith("api/v1/content/", System.StringComparison.Ordinal))
            {
                continue;
            }

            // The orchestrator's own lifecycle probes and aggregate stats are
            // normally surfaced once (under the orchestrator's own groups), so
            // the proxied copies are skipped — unless the proxy genuinely
            // re-exposes them (FHIR), in which case skipOrchestratorNative is
            // false. Each source still re-exports its own copies under its
            // DevUI tab.
            if (skipOrchestratorNative
                && (string.Equals(entry.PathTemplate, "api/v1/health", System.StringComparison.Ordinal)
                    || string.Equals(entry.PathTemplate, "api/v1/status", System.StringComparison.Ordinal)
                    || string.Equals(entry.PathTemplate, "api/v1/stats", System.StringComparison.Ordinal)))
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
