using System.Collections.Generic;
using System.Net.Http;

namespace FhirAugury.DevUi.Services.ApiCatalog.Catalogs;

public static class OrchestratorCatalog
{
    public static IReadOnlyList<ApiEndpointDescriptor> Build() =>
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
            PathTemplate: "api/v1/content/item/{source}/{*id}",
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
            PathTemplate: "api/v1/content/keywords/{source}/{*id}",
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
            PathTemplate: "api/v1/content/related-by-keyword/{source}/{*id}",
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
    ];
}
