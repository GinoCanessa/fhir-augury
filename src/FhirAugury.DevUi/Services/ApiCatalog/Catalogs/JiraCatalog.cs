using System.Collections.Generic;
using System.Linq;
using System.Net.Http;

namespace FhirAugury.DevUi.Services.ApiCatalog.Catalogs;

public static class JiraCatalog
{
    public static IReadOnlyList<ApiEndpointDescriptor> Build()
    {
        ApiParameter projectIngest = new("project", ApiParameterKind.Query, Required: false,
            Placeholder: "Optional: limit to a single Jira project key");
        List<ApiEndpointDescriptor> list =
        [
            .. SharedSourceEndpoints.ContentEndpoints(),
            .. SharedSourceEndpoints.LifecycleEndpoints(),
            .. SharedSourceEndpoints.IngestionEndpoints([projectIngest]),

            // Items
            new ApiEndpointDescriptor("items.list", "List items", "Items",
                HttpMethod.Get, "api/v1/items",
                [
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "50",
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("offset", ApiParameterKind.Query, Required: false, DefaultValue: "0",
                        ValueType: ApiParameterValueType.Int),
                ]),
            new ApiEndpointDescriptor("items.get", "Get item by key", "Items",
                HttpMethod.Get, "api/v1/items/{key}",
                [
                    new ApiParameter("key", ApiParameterKind.Path, Required: true, Placeholder: "FHIR-55001"),
                    new ApiParameter("includeContent", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Bool),
                    new ApiParameter("includeComments", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Bool),
                ]),
            new ApiEndpointDescriptor("items.related", "Related items", "Items",
                HttpMethod.Get, "api/v1/items/{key}/related",
                [
                    new ApiParameter("key", ApiParameterKind.Path, Required: true),
                    new ApiParameter("seedSource", ApiParameterKind.Query, Required: false),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "10",
                        ValueType: ApiParameterValueType.Int),
                ]),
            new ApiEndpointDescriptor("items.snapshot", "Snapshot", "Items",
                HttpMethod.Get, "api/v1/items/{key}/snapshot",
                [
                    new ApiParameter("key", ApiParameterKind.Path, Required: true),
                    new ApiParameter("includeComments", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Bool),
                    new ApiParameter("includeRefs", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Bool),
                ]),
            new ApiEndpointDescriptor("items.content", "Item content", "Items",
                HttpMethod.Get, "api/v1/items/{key}/content",
                [
                    new ApiParameter("key", ApiParameterKind.Path, Required: true),
                    new ApiParameter("format", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("items.comments", "Item comments", "Items",
                HttpMethod.Get, "api/v1/items/{key}/comments",
                [new ApiParameter("key", ApiParameterKind.Path, Required: true)]),
            new ApiEndpointDescriptor("items.links", "Item links", "Items",
                HttpMethod.Get, "api/v1/items/{key}/links",
                [new ApiParameter("key", ApiParameterKind.Path, Required: true)]),

            // Projects
            new ApiEndpointDescriptor("projects.list", "List projects", "Projects",
                HttpMethod.Get, "api/v1/projects", []),
            new ApiEndpointDescriptor("projects.get", "Get project", "Projects",
                HttpMethod.Get, "api/v1/projects/{key}",
                [new ApiParameter("key", ApiParameterKind.Path, Required: true)]),
            new ApiEndpointDescriptor("projects.update", "Update project ranking", "Projects",
                HttpMethod.Put, "api/v1/projects/{key}",
                [
                    new ApiParameter("key", ApiParameterKind.Path, Required: true),
                    new ApiParameter("body", ApiParameterKind.Body, Required: true,
                        DefaultValue: "{ \"enabled\": true, \"baselineValue\": 5 }",
                        ValueType: ApiParameterValueType.Json),
                ]),

            // Query
            new ApiEndpointDescriptor("query.flexible", "Flexible Query", "Query",
                HttpMethod.Post, "api/v1/query",
                [
                    new ApiParameter("body", ApiParameterKind.Body, Required: true, DefaultValue: "{}",
                        ValueType: ApiParameterValueType.Json),
                ]),
            new ApiEndpointDescriptor("query.labels", "List labels", "Query",
                HttpMethod.Get, "api/v1/labels", []),
            new ApiEndpointDescriptor("query.statuses", "List statuses", "Query",
                HttpMethod.Get, "api/v1/statuses", []),
            new ApiEndpointDescriptor("query.users", "List users", "Query",
                HttpMethod.Get, "api/v1/users", []),
            new ApiEndpointDescriptor("query.inpersons", "List in-person requesters", "Query",
                HttpMethod.Get, "api/v1/inpersons", []),

            // Specifications
            new ApiEndpointDescriptor("specs.list", "List specifications", "Specifications",
                HttpMethod.Get, "api/v1/specifications", []),
            new ApiEndpointDescriptor("specs.get", "Issues for specification", "Specifications",
                HttpMethod.Get, "api/v1/specifications/{spec}",
                [
                    new ApiParameter("spec", ApiParameterKind.Path, Required: true),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "50",
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("offset", ApiParameterKind.Query, Required: false, DefaultValue: "0",
                        ValueType: ApiParameterValueType.Int),
                ]),
            new ApiEndpointDescriptor("specs.issue-numbers", "Issue numbers", "Specifications",
                HttpMethod.Get, "api/v1/issue-numbers",
                [new ApiParameter("project", ApiParameterKind.Query, Required: false)]),

            // Work groups
            new ApiEndpointDescriptor("work-groups.list", "List work groups", "Work Groups",
                HttpMethod.Get, "api/v1/work-groups", []),
            new ApiEndpointDescriptor("work-groups.issues-by-code", "Issues for work group (by code)", "Work Groups",
                HttpMethod.Get, "api/v1/work-groups/{groupCode}/issues",
                [
                    new ApiParameter("groupCode", ApiParameterKind.Path, Required: true,
                        Placeholder: "fhir",
                        HelpText: "HL7 work group code (e.g. fhir, pc). NameClean accepted as alternate."),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "50",
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("offset", ApiParameterKind.Query, Required: false, DefaultValue: "0",
                        ValueType: ApiParameterValueType.Int),
                ]),
            new ApiEndpointDescriptor("work-groups.issues", "Issues for work group", "Work Groups",
                HttpMethod.Get, "api/v1/work-groups/issues",
                [
                    new ApiParameter("groupCode", ApiParameterKind.Query, Required: false,
                        Placeholder: "fhir",
                        HelpText: "Optional HL7 work group code (e.g. fhir, pc)."),
                    new ApiParameter("groupName", ApiParameterKind.Query, Required: false,
                        Placeholder: "FHIR Infrastructure",
                        HelpText: "Optional canonical work group name. AND-ed with groupCode when both are provided."),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "50",
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("offset", ApiParameterKind.Query, Required: false, DefaultValue: "0",
                        ValueType: ApiParameterValueType.Int),
                ]),

            // Local processing (FR-02)
            new ApiEndpointDescriptor("local-processing.tickets", "Tickets (filtered)", "Local Processing",
                HttpMethod.Post, "api/v1/local-processing/tickets",
                [
                    new ApiParameter("body", ApiParameterKind.Body, Required: true, DefaultValue: "{}",
                        HelpText: "JiraLocalProcessingListRequest", ValueType: ApiParameterValueType.Json),
                ]),
            new ApiEndpointDescriptor("local-processing.random-ticket", "Random Ticket", "Local Processing",
                HttpMethod.Post, "api/v1/local-processing/random-ticket",
                [
                    new ApiParameter("body", ApiParameterKind.Body, Required: false, DefaultValue: "{}",
                        ValueType: ApiParameterValueType.Json),
                ]),
            new ApiEndpointDescriptor("local-processing.set-processed", "Set Processed", "Local Processing",
                HttpMethod.Post, "api/v1/local-processing/set-processed",
                [
                    new ApiParameter("body", ApiParameterKind.Body, Required: true,
                        DefaultValue: "{ \"key\": \"FHIR-55001\", \"processedLocally\": true }",
                        ValueType: ApiParameterValueType.Json),
                ],
                Destructive: true),
            new ApiEndpointDescriptor("local-processing.clear-all-processed", "Clear All Processed",
                "Local Processing",
                HttpMethod.Post, "api/v1/local-processing/clear-all-processed", [],
                Destructive: true),

            // Project Scope Statements (PSS-*)
            new ApiEndpointDescriptor("pss.list", "List PSS items", "Project Scope Statements",
                HttpMethod.Get, "api/v1/pss",
                [
                    new ApiParameter("workGroup", ApiParameterKind.Query, Required: false),
                    new ApiParameter("status", ApiParameterKind.Query, Required: false),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "50",
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("offset", ApiParameterKind.Query, Required: false, DefaultValue: "0",
                        ValueType: ApiParameterValueType.Int),
                ]),
            new ApiEndpointDescriptor("pss.get", "Get PSS by key", "Project Scope Statements",
                HttpMethod.Get, "api/v1/pss/{key}",
                [
                    new ApiParameter("key", ApiParameterKind.Path, Required: true, Placeholder: "PSS-1234"),
                ]),

            // Ballot Definitions (BALDEF-*)
            new ApiEndpointDescriptor("baldef.list", "List Ballot Definitions", "Ballot Definitions",
                HttpMethod.Get, "api/v1/baldef",
                [
                    new ApiParameter("cycle", ApiParameterKind.Query, Required: false),
                    new ApiParameter("level", ApiParameterKind.Query, Required: false),
                    new ApiParameter("workGroup", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("baldef.get", "Get Ballot Definition by key", "Ballot Definitions",
                HttpMethod.Get, "api/v1/baldef/{key}",
                [
                    new ApiParameter("key", ApiParameterKind.Path, Required: true, Placeholder: "BALDEF-1234"),
                ]),

            // Ballot Votes (BALLOT-*)
            new ApiEndpointDescriptor("ballot.list", "List Ballot Votes", "Ballot Votes",
                HttpMethod.Get, "api/v1/ballot",
                [
                    new ApiParameter("cycle", ApiParameterKind.Query, Required: false),
                    new ApiParameter("specification", ApiParameterKind.Query, Required: false),
                    new ApiParameter("disposition", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("ballot.get", "Get Ballot Vote by key", "Ballot Votes",
                HttpMethod.Get, "api/v1/ballot/{key}",
                [
                    new ApiParameter("key", ApiParameterKind.Path, Required: true, Placeholder: "BALLOT-1234"),
                ]),
        ];

        return list;
    }
}
