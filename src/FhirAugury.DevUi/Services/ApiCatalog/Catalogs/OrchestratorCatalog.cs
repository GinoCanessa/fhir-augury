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
            .. ProjectMatrixProxyRoutes(),
            .. ProjectLegacyFhirLifecycleRoutes(),
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
            Id: "services.refresh",
            DisplayName: "Refresh Services Status",
            Group: "Services",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/services/refresh",
            Parameters: [],
            Description: "Runs a full Orchestrator health recheck without starting or stopping services."),

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

        new ApiEndpointDescriptor(
            Id: "processing.authoring.list",
            DisplayName: "List Authoring Runs",
            Group: "Processing",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "20",
                    ValueType: ApiParameterValueType.Int),
            ],
            Description: "Lists active and recent processor-owned authoring runs."),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.start",
            DisplayName: "Start Authoring Run",
            Group: "Processing",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter(
                    "body",
                    ApiParameterKind.Body,
                    Required: true,
                    DefaultValue: "{ \"ticketKeys\": [], \"databaseOnly\": false }",
                    ValueType: ApiParameterValueType.Json),
            ],
            Destructive: true,
            Description: "Starts a frozen processor-owned authoring run."),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.publication-refresh",
            DisplayName: "Start Publication Refresh",
            Group: "Processing",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs/{sourceRunId}/publication-refresh",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter("sourceRunId", ApiParameterKind.Path, Required: true),
            ],
            Destructive: true),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.publication-reconciliation.start",
            DisplayName: "Start Publication Reconciliation",
            Group: "Processing",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs/{sourceRunId}/publication-reconciliation",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter("sourceRunId", ApiParameterKind.Path, Required: true),
            ],
            Destructive: true),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.publication-reconciliation.status",
            DisplayName: "Publication Reconciliation Status",
            Group: "Processing",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs/{runId}/publication-reconciliation",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter("runId", ApiParameterKind.Path, Required: true),
            ]),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.publication-reconciliation.retry",
            DisplayName: "Retry Publication Reconciliation",
            Group: "Processing",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs/{runId}/publication-reconciliation/retry",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter("runId", ApiParameterKind.Path, Required: true),
            ],
            Destructive: true),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.publication-reconciliation.cancel",
            DisplayName: "Cancel Publication Reconciliation",
            Group: "Processing",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs/{runId}/publication-reconciliation/cancel",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter("runId", ApiParameterKind.Path, Required: true),
                new ApiParameter(
                    "body",
                    ApiParameterKind.Body,
                    Required: true,
                    DefaultValue: "{ \"reason\": \"frozen Jira revisions changed\" }",
                    ValueType: ApiParameterValueType.Json),
            ],
            Destructive: true,
            Description: "Terminates staged reconciliation before trusted or canonical promotion and retains its cancellation audit."),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.publication-reconciliation.abandon",
            DisplayName: "Abandon Publication Reconciliation",
            Group: "Processing",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs/{runId}/publication-reconciliation/abandon",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter("runId", ApiParameterKind.Path, Required: true),
                new ApiParameter(
                    "body",
                    ApiParameterKind.Body,
                    Required: true,
                    DefaultValue: "{ \"reason\": \"snapshot publication cannot be recovered\" }",
                    ValueType: ApiParameterValueType.Json),
            ],
            Destructive: true,
            Description: "Leaves promoted canonical data unpublished and restricts later snapshot-producing workflows."),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.canonical-epoch-recovery.start",
            DisplayName: "Start Canonical-Epoch Recovery",
            Group: "Processing",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs/{sourceRunId}/canonical-epoch-recovery",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter(
                    "sourceRunId",
                    ApiParameterKind.Path,
                    Required: true),
            ],
            Destructive: true,
            Description: "Starts or returns the existing dedicated recovery run for an unresolved canonical-unpublished abandonment."),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.canonical-epoch-recovery.status",
            DisplayName: "Canonical-Epoch Recovery Status",
            Group: "Processing",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs/{runId}/canonical-epoch-recovery",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter(
                    "runId",
                    ApiParameterKind.Path,
                    Required: true),
            ]),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.canonical-epoch-recovery.retry",
            DisplayName: "Retry Canonical-Epoch Recovery",
            Group: "Processing",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs/{runId}/canonical-epoch-recovery/retry",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter(
                    "runId",
                    ApiParameterKind.Path,
                    Required: true),
            ],
            Destructive: true,
            Description: "Resumes the same recovery run and immutable snapshot journal."),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.status",
            DisplayName: "Authoring Run Status",
            Group: "Processing",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs/{runId}",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter("runId", ApiParameterKind.Path, Required: true),
            ]),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.retry",
            DisplayName: "Retry Authoring Item",
            Group: "Processing",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs/{runId}/items/{itemId}/retry",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter("runId", ApiParameterKind.Path, Required: true),
                new ApiParameter("itemId", ApiParameterKind.Path, Required: true),
            ],
            Destructive: true),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.supersede",
            DisplayName: "Supersede Authoring Item",
            Group: "Processing",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs/{runId}/items/{itemId}/supersede",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter("runId", ApiParameterKind.Path, Required: true),
                new ApiParameter("itemId", ApiParameterKind.Path, Required: true),
                new ApiParameter(
                    "body",
                    ApiParameterKind.Body,
                    Required: true,
                    DefaultValue: "{ \"reason\": \"not actionable\" }",
                    ValueType: ApiParameterValueType.Json),
            ],
            Destructive: true,
            Description: "Supersedes one current non-receipt-backed authoring error."),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.snapshot",
            DisplayName: "Authoring Snapshot Descriptor",
            Group: "Processing",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs/{runId}/snapshot",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter("runId", ApiParameterKind.Path, Required: true),
            ]),

        new ApiEndpointDescriptor(
            Id: "processing.authoring.snapshot-bytes",
            DisplayName: "Authoring Snapshot Bytes",
            Group: "Processing",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/processing-services/{name}/authoring/runs/{runId}/snapshot/bytes",
            Parameters:
            [
                ProcessingServiceName(),
                new ApiParameter("runId", ApiParameterKind.Path, Required: true),
            ]),

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
            Placeholder: "Preparer");

    private static IEnumerable<ApiEndpointDescriptor> ProjectMatrixProxyRoutes()
    {
        foreach (OrchestratorProxyRoute route in OrchestratorProxyRouteMatrix.Routes)
        {
            if (route.Disposition != OrchestratorProxyRouteDisposition.TypedSourceProxy)
                continue;

            yield return ForOrchestratorCatalog(
                route.SourceName,
                PrefixSourceDescriptor(route.SourceName, route.SourceDescriptor));
        }
    }

    private static IEnumerable<ApiEndpointDescriptor> ProjectLegacyFhirLifecycleRoutes()
    {
        foreach (OrchestratorProxyRoute route in OrchestratorProxyRouteMatrix.GetRoutes("fhir"))
        {
            if (route.Disposition !=
                OrchestratorProxyRouteDisposition.OrchestratorReadinessOrMetaReplacement)
            {
                continue;
            }

            yield return ForOrchestratorCatalog(
                route.SourceName,
                PrefixSourceDescriptor(route.SourceName, route.SourceDescriptor));
        }
    }

    private static ApiEndpointDescriptor PrefixSourceDescriptor(
        string sourceName,
        ApiEndpointDescriptor sourceDescriptor)
    {
        const string ApiV1 = "api/v1";
        string remainder = sourceDescriptor.PathTemplate.StartsWith(ApiV1, System.StringComparison.Ordinal)
            ? sourceDescriptor.PathTemplate[ApiV1.Length..]
            : "/" + sourceDescriptor.PathTemplate.TrimStart('/');
        if (!remainder.StartsWith("/", System.StringComparison.Ordinal))
            remainder = "/" + remainder;

        return sourceDescriptor with
        {
            PathTemplate = $"api/v1/{sourceName}{remainder}",
            Parameters = sourceDescriptor.Parameters.ToList(),
        };
    }

    private static ApiEndpointDescriptor ForOrchestratorCatalog(
        string sourceName,
        ApiEndpointDescriptor descriptor)
    {
        string displayPrefix = sourceName switch
        {
            "jira" => "Jira",
            "zulip" => "Zulip",
            "confluence" => "Confluence",
            "github" => "GitHub",
            "fhir" => "FHIR",
            _ => sourceName,
        };

        return descriptor with
        {
            Id = $"{sourceName}.{descriptor.Id}",
            Group = $"{displayPrefix} / {descriptor.Group}",
            Parameters = descriptor.Parameters.ToList(),
        };
    }
}
