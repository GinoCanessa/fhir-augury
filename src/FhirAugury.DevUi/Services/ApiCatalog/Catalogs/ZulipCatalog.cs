using System.Collections.Generic;
using System.Net.Http;

namespace FhirAugury.DevUi.Services.ApiCatalog.Catalogs;

public static class ZulipCatalog
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
                    new ApiParameter("id", ApiParameterKind.Path, Required: true,
                        Placeholder: "123456789", ValueType: ApiParameterValueType.Int,
                        HelpText: "Public numeric Zulip message ID. Use Resolve reference for a legacy streamName:topic association."),
                    new ApiParameter("includeContent", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Bool),
                ]),
            new ApiEndpointDescriptor("items.related", "Related items", "Items",
                HttpMethod.Get, "api/v1/items/{id}/related",
                [
                    new ApiParameter("id", ApiParameterKind.Path, Required: true),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "10",
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("seedSource", ApiParameterKind.Query, Required: false),
                    new ApiParameter("seedId", ApiParameterKind.Query, Required: false),
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
            new ApiEndpointDescriptor("items.comments", "Comments (stub)", "Items",
                HttpMethod.Get, "api/v1/items/{id}/comments",
                [new ApiParameter("id", ApiParameterKind.Path, Required: true)],
                Description: "Always returns []. Zulip has no first-class comment concept; stub for shape parity with Jira."),
            new ApiEndpointDescriptor("items.links", "Links (stub)", "Items",
                HttpMethod.Get, "api/v1/items/{id}/links",
                [new ApiParameter("id", ApiParameterKind.Path, Required: true)],
                Description: "Always returns []. Zulip has no typed inter-item links; stub for shape parity with Jira."),

            // Messages
            new ApiEndpointDescriptor("messages.get", "Get message by id", "Messages",
                HttpMethod.Get, "api/v1/messages/{id}",
                [new ApiParameter("id", ApiParameterKind.Path, Required: true,
                    ValueType: ApiParameterValueType.Int)]),
            new ApiEndpointDescriptor("messages.list", "List messages", "Messages",
                HttpMethod.Get, "api/v1/messages",
                [
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "50",
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("offset", ApiParameterKind.Query, Required: false, DefaultValue: "0",
                        ValueType: ApiParameterValueType.Int),
                ]),
            new ApiEndpointDescriptor("messages.by-user", "Messages by user", "Messages",
                HttpMethod.Get, "api/v1/messages/by-user/{user}",
                [
                    new ApiParameter("user", ApiParameterKind.Path, Required: true),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "50",
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("offset", ApiParameterKind.Query, Required: false, DefaultValue: "0",
                        ValueType: ApiParameterValueType.Int),
                ]),

            // Streams
            new ApiEndpointDescriptor("streams.list", "List streams", "Streams",
                HttpMethod.Get, "api/v1/streams", []),
            new ApiEndpointDescriptor("streams.get", "Get stream", "Streams",
                HttpMethod.Get, "api/v1/streams/{zulipStreamId}",
                [new ApiParameter("zulipStreamId", ApiParameterKind.Path, Required: true,
                    ValueType: ApiParameterValueType.Int)]),
            new ApiEndpointDescriptor("streams.update", "Update stream ranking", "Streams",
                HttpMethod.Put, "api/v1/streams/{zulipStreamId}",
                [
                    new ApiParameter("zulipStreamId", ApiParameterKind.Path, Required: true,
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("body", ApiParameterKind.Body, Required: true, DefaultValue: "{}",
                        ValueType: ApiParameterValueType.Json),
                ]),
            new ApiEndpointDescriptor("streams.topics", "Topics for stream", "Streams",
                HttpMethod.Get, "api/v1/streams/topics",
                [
                    new ApiParameter("streamName", ApiParameterKind.Query, Required: true),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "50",
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("offset", ApiParameterKind.Query, Required: false, DefaultValue: "0",
                        ValueType: ApiParameterValueType.Int),
                ]),

            // References
            new ApiEndpointDescriptor("references.resolve", "Resolve reference", "References",
                HttpMethod.Get, "api/v1/references/resolve",
                [
                    new ApiParameter("reference", ApiParameterKind.Query, Required: true,
                        Placeholder: "123456789 or implementers:topic",
                        HelpText: "Original accepted numeric message ID or streamName:topic; reserved characters belong in this query value."),
                ],
                Description: "Read-only resolution against indexed source context. Never fetches or ingests upstream data; unindexed or ambiguous references are explicit failures."),

            // Threads
            new ApiEndpointDescriptor("threads.get", "Get thread", "Threads",
                HttpMethod.Get, "api/v1/threads",
                [
                    new ApiParameter("streamName", ApiParameterKind.Query, Required: true),
                    new ApiParameter("topic", ApiParameterKind.Query, Required: true),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "100",
                        ValueType: ApiParameterValueType.Int),
                ]),
            new ApiEndpointDescriptor("threads.snapshot", "Thread snapshot", "Threads",
                HttpMethod.Get, "api/v1/threads/snapshot",
                [
                    new ApiParameter("streamName", ApiParameterKind.Query, Required: true),
                    new ApiParameter("topic", ApiParameterKind.Query, Required: true),
                ]),

            // Query
            new ApiEndpointDescriptor("query.flexible", "Flexible Query", "Query",
                HttpMethod.Post, "api/v1/query",
                [
                    new ApiParameter("body", ApiParameterKind.Body, Required: true, DefaultValue: "{}",
                        ValueType: ApiParameterValueType.Json),
                ]),
        ];

        return list;
    }
}
