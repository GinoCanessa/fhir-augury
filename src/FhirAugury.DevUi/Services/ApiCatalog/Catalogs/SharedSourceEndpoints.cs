using System.Collections.Generic;
using System.Net.Http;

namespace FhirAugury.DevUi.Services.ApiCatalog.Catalogs;

/// <summary>
/// Builders for the endpoints shared by every source service:
/// 4 cross-reference queries, search, get-item, keywords/related-by-keyword,
/// status, stats, health.
/// </summary>
public static class SharedSourceEndpoints
{
    public static IEnumerable<ApiEndpointDescriptor> ContentEndpoints(ApiEncoding idEncoding = ApiEncoding.Default) =>
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
                    Placeholder: "comma-separated values, e.g. patient resource, FHIR-50783",
                    Repeatable: true),
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
                new ApiParameter("value", ApiParameterKind.Query, Required: true,
                    Placeholder: "e.g. FHIR-50783"),
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
            PathTemplate: "api/v1/content/item/{source}/{*id}",
            Parameters:
            [
                new ApiParameter("source", ApiParameterKind.Path, Required: true),
                new ApiParameter("id", ApiParameterKind.Path, Required: true,
                    Encoding: idEncoding, IsCatchAll: true),
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
                new ApiParameter("id", ApiParameterKind.Path, Required: true,
                    Encoding: idEncoding, IsCatchAll: true),
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
                new ApiParameter("id", ApiParameterKind.Path, Required: true,
                    Encoding: idEncoding, IsCatchAll: true),
                new ApiParameter("minScore", ApiParameterKind.Query, Required: false,
                    DefaultValue: "0.1", ValueType: ApiParameterValueType.Double),
                new ApiParameter("keywordType", ApiParameterKind.Query, Required: false),
                new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "20",
                    ValueType: ApiParameterValueType.Int),
            ]),
    ];

    public static IEnumerable<ApiEndpointDescriptor> LifecycleEndpoints() =>
    [
        new ApiEndpointDescriptor(
            Id: "lifecycle.status",
            DisplayName: "Status",
            Group: "Lifecycle",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/status",
            Parameters: []),

        new ApiEndpointDescriptor(
            Id: "lifecycle.stats",
            DisplayName: "Stats",
            Group: "Lifecycle",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/stats",
            Parameters: []),

        new ApiEndpointDescriptor(
            Id: "lifecycle.health",
            DisplayName: "Health",
            Group: "Lifecycle",
            Method: HttpMethod.Get,
            PathTemplate: "api/v1/health",
            Parameters: []),
    ];

    public static IEnumerable<ApiEndpointDescriptor> IngestionEndpoints(
        IReadOnlyList<ApiParameter>? extraIngestParams = null) =>
    [
        new ApiEndpointDescriptor(
            Id: "ingestion.ingest",
            DisplayName: "Ingest (run)",
            Group: "Ingestion",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/ingest",
            Parameters:
            [
                new ApiParameter("type", ApiParameterKind.Query, Required: false,
                    DefaultValue: "incremental", Placeholder: "incremental|full"),
                .. extraIngestParams ?? [],
            ]),

        new ApiEndpointDescriptor(
            Id: "ingestion.trigger",
            DisplayName: "Ingest Trigger (queue)",
            Group: "Ingestion",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/ingest/trigger",
            Parameters:
            [
                new ApiParameter("type", ApiParameterKind.Query, Required: false,
                    DefaultValue: "incremental", Placeholder: "incremental|full"),
                .. extraIngestParams ?? [],
            ]),

        new ApiEndpointDescriptor(
            Id: "ingestion.rebuild",
            DisplayName: "Rebuild",
            Group: "Ingestion",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/rebuild",
            Parameters: [],
            Destructive: true,
            Description: "Rebuilds the source's indexed datasets."),

        new ApiEndpointDescriptor(
            Id: "ingestion.rebuild-index",
            DisplayName: "Rebuild Index",
            Group: "Ingestion",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/rebuild-index",
            Parameters:
            [
                new ApiParameter("type", ApiParameterKind.Query, Required: false,
                    DefaultValue: "all", Placeholder: "all|bm25|fts|cross-refs|lookup-tables"),
            ],
            Destructive: true),

        new ApiEndpointDescriptor(
            Id: "ingestion.notify-peer",
            DisplayName: "Notify Peer",
            Group: "Ingestion",
            Method: HttpMethod.Post,
            PathTemplate: "api/v1/notify-peer",
            Parameters:
            [
                new ApiParameter("body", ApiParameterKind.Body, Required: false, DefaultValue: "{}",
                    ValueType: ApiParameterValueType.Json),
            ],
            Destructive: true),
    ];
}
