using System.Collections.Generic;
using System.Net.Http;

namespace FhirAugury.DevUi.Services.ApiCatalog.Catalogs;

public static class ConfluenceCatalog
{
    public static IReadOnlyList<ApiEndpointDescriptor> Build()
    {
        List<ApiEndpointDescriptor> list =
        [
            .. SharedSourceEndpoints.ContentEndpoints(),
            .. SharedSourceEndpoints.LifecycleEndpoints(),
            .. SharedSourceEndpoints.IngestionEndpoints(),

            // Items
            new ApiEndpointDescriptor("items.list", "List items", "Items",
                HttpMethod.Get, "api/v1/items",
                [
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "50",
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("offset", ApiParameterKind.Query, Required: false, DefaultValue: "0",
                        ValueType: ApiParameterValueType.Int),
                ]),
            new ApiEndpointDescriptor("items.get", "Get item", "Items",
                HttpMethod.Get, "api/v1/items/{id}",
                [
                    new ApiParameter("id", ApiParameterKind.Path, Required: true),
                    new ApiParameter("includeContent", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Bool),
                ]),
            new ApiEndpointDescriptor("items.related", "Related items", "Items",
                HttpMethod.Get, "api/v1/items/{id}/related",
                [
                    new ApiParameter("id", ApiParameterKind.Path, Required: true),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "10",
                        ValueType: ApiParameterValueType.Int),
                ]),
            new ApiEndpointDescriptor("items.snapshot", "Snapshot", "Items",
                HttpMethod.Get, "api/v1/items/{id}/snapshot",
                [new ApiParameter("id", ApiParameterKind.Path, Required: true)]),
            new ApiEndpointDescriptor("items.content", "Content", "Items",
                HttpMethod.Get, "api/v1/items/{id}/content",
                [
                    new ApiParameter("id", ApiParameterKind.Path, Required: true),
                    new ApiParameter("format", ApiParameterKind.Query, Required: false),
                ]),

            // Pages
            new ApiEndpointDescriptor("pages.list", "List pages", "Pages",
                HttpMethod.Get, "api/v1/pages",
                [
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "50",
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("offset", ApiParameterKind.Query, Required: false, DefaultValue: "0",
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("spaceKey", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("pages.get", "Get page", "Pages",
                HttpMethod.Get, "api/v1/pages/{pageId}",
                [new ApiParameter("pageId", ApiParameterKind.Path, Required: true)]),
            new ApiEndpointDescriptor("pages.related", "Related pages", "Pages",
                HttpMethod.Get, "api/v1/pages/{pageId}/related",
                [
                    new ApiParameter("pageId", ApiParameterKind.Path, Required: true),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "10",
                        ValueType: ApiParameterValueType.Int),
                ]),
            new ApiEndpointDescriptor("pages.snapshot", "Snapshot", "Pages",
                HttpMethod.Get, "api/v1/pages/{pageId}/snapshot",
                [new ApiParameter("pageId", ApiParameterKind.Path, Required: true)]),
            new ApiEndpointDescriptor("pages.content", "Content", "Pages",
                HttpMethod.Get, "api/v1/pages/{pageId}/content",
                [
                    new ApiParameter("pageId", ApiParameterKind.Path, Required: true),
                    new ApiParameter("format", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("pages.comments", "Comments", "Pages",
                HttpMethod.Get, "api/v1/pages/{pageId}/comments",
                [new ApiParameter("pageId", ApiParameterKind.Path, Required: true)]),
            new ApiEndpointDescriptor("pages.children", "Child pages", "Pages",
                HttpMethod.Get, "api/v1/pages/{pageId}/children",
                [new ApiParameter("pageId", ApiParameterKind.Path, Required: true)]),
            new ApiEndpointDescriptor("pages.ancestors", "Ancestors", "Pages",
                HttpMethod.Get, "api/v1/pages/{pageId}/ancestors",
                [new ApiParameter("pageId", ApiParameterKind.Path, Required: true)]),
            new ApiEndpointDescriptor("pages.linked", "Linked pages", "Pages",
                HttpMethod.Get, "api/v1/pages/{pageId}/linked",
                [
                    new ApiParameter("pageId", ApiParameterKind.Path, Required: true),
                    new ApiParameter("direction", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("pages.by-label", "Pages by label", "Pages",
                HttpMethod.Get, "api/v1/pages/by-label/{label}",
                [
                    new ApiParameter("label", ApiParameterKind.Path, Required: true),
                    new ApiParameter("spaceKey", ApiParameterKind.Query, Required: false),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "50",
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("offset", ApiParameterKind.Query, Required: false, DefaultValue: "0",
                        ValueType: ApiParameterValueType.Int),
                ]),

            // Spaces
            new ApiEndpointDescriptor("spaces.list", "List spaces", "Spaces",
                HttpMethod.Get, "api/v1/spaces", []),
        ];

        return list;
    }
}
