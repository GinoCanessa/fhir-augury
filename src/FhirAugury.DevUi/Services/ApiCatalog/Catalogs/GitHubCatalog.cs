using System.Collections.Generic;
using System.Net.Http;

namespace FhirAugury.DevUi.Services.ApiCatalog.Catalogs;

public static class GitHubCatalog
{
    public static IReadOnlyList<ApiEndpointDescriptor> Build()
    {
        ApiParameter idParam = new("id", ApiParameterKind.Path, Required: true,
            Placeholder: "owner/repo#number", Encoding: ApiEncoding.IdSlashPreserving, IsCatchAll: true);

        // Override Content endpoints to use IdSlashPreserving for the id segment.
        List<ApiEndpointDescriptor> content =
        [
            .. SharedSourceEndpoints.ContentEndpoints(idEncoding: ApiEncoding.IdSlashPreserving),
        ];

        ApiParameter keyParam = new("key", ApiParameterKind.Path, Required: true,
            Placeholder: "owner/repo#number", Encoding: ApiEncoding.IdSlashPreserving, IsCatchAll: true);

        List<ApiEndpointDescriptor> list =
        [
            .. content,
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
                HttpMethod.Get, "api/v1/items/{*key}",
                [
                    keyParam,
                    new ApiParameter("includeContent", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Bool),
                    new ApiParameter("includeComments", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Bool),
                ]),
            new ApiEndpointDescriptor("items.related", "Related items", "Items",
                HttpMethod.Get, "api/v1/items/related/{*key}",
                [
                    keyParam,
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false, DefaultValue: "10",
                        ValueType: ApiParameterValueType.Int),
                    new ApiParameter("seedSource", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("items.snapshot", "Snapshot", "Items",
                HttpMethod.Get, "api/v1/items/snapshot/{*key}",
                [
                    keyParam,
                    new ApiParameter("includeComments", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Bool),
                    new ApiParameter("includeRefs", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Bool),
                ]),
            new ApiEndpointDescriptor("items.content", "Content", "Items",
                HttpMethod.Get, "api/v1/items/content/{*key}",
                [
                    keyParam,
                    new ApiParameter("format", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("items.comments", "Comments", "Items",
                HttpMethod.Get, "api/v1/items/comments/{*key}",
                [keyParam]),
            new ApiEndpointDescriptor("items.commits", "Commits", "Items",
                HttpMethod.Get, "api/v1/items/commits/{*key}",
                [keyParam]),
            new ApiEndpointDescriptor("items.pr", "Pull request", "Items",
                HttpMethod.Get, "api/v1/items/pr/{*key}",
                [keyParam]),

            // Repos
            new ApiEndpointDescriptor("repos.list", "List repositories", "Repos",
                HttpMethod.Get, "api/v1/repos", []),

            // Tags
            new ApiEndpointDescriptor("tags.list", "Tags for repo", "Tags",
                HttpMethod.Get, "api/v1/repos/{owner}/{name}/tags",
                [
                    new ApiParameter("owner", ApiParameterKind.Path, Required: true, Placeholder: "HL7"),
                    new ApiParameter("name", ApiParameterKind.Path, Required: true, Placeholder: "fhir"),
                    new ApiParameter("category", ApiParameterKind.Query, Required: false),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Int),
                ]),
            new ApiEndpointDescriptor("tags.files", "Tag files", "Tags",
                HttpMethod.Get, "api/v1/repos/{owner}/{name}/tags/files",
                [
                    new ApiParameter("owner", ApiParameterKind.Path, Required: true),
                    new ApiParameter("name", ApiParameterKind.Path, Required: true),
                    new ApiParameter("category", ApiParameterKind.Query, Required: false),
                    new ApiParameter("tagName", ApiParameterKind.Query, Required: false),
                    new ApiParameter("modifier", ApiParameterKind.Query, Required: false),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Int),
                ]),
            new ApiEndpointDescriptor("tags.search", "Search tags", "Tags",
                HttpMethod.Get, "api/v1/repos/{owner}/{name}/tags/search",
                [
                    new ApiParameter("owner", ApiParameterKind.Path, Required: true),
                    new ApiParameter("name", ApiParameterKind.Path, Required: true),
                    new ApiParameter("query", ApiParameterKind.Query, Required: false),
                    new ApiParameter("category", ApiParameterKind.Query, Required: false),
                    new ApiParameter("tagName", ApiParameterKind.Query, Required: false),
                    new ApiParameter("limit", ApiParameterKind.Query, Required: false,
                        ValueType: ApiParameterValueType.Int),
                ]),

            // Jira-spec subtree
            new ApiEndpointDescriptor("jira-specs.list", "List jira-specs", "Jira Specs",
                HttpMethod.Get, "api/v1/jira-specs",
                [
                    new ApiParameter("family", ApiParameterKind.Query, Required: false),
                    new ApiParameter("workgroup", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("jira-specs.get", "Get jira-spec", "Jira Specs",
                HttpMethod.Get, "api/v1/jira-specs/{specKey}",
                [new ApiParameter("specKey", ApiParameterKind.Path, Required: true)]),
            new ApiEndpointDescriptor("jira-specs.artifacts", "Artifacts for spec", "Jira Specs",
                HttpMethod.Get, "api/v1/jira-specs/{specKey}/artifacts",
                [new ApiParameter("specKey", ApiParameterKind.Path, Required: true)]),
            new ApiEndpointDescriptor("jira-specs.pages", "Pages for spec", "Jira Specs",
                HttpMethod.Get, "api/v1/jira-specs/{specKey}/pages",
                [new ApiParameter("specKey", ApiParameterKind.Path, Required: true)]),
            new ApiEndpointDescriptor("jira-specs.versions", "Versions for spec", "Jira Specs",
                HttpMethod.Get, "api/v1/jira-specs/{specKey}/versions",
                [new ApiParameter("specKey", ApiParameterKind.Path, Required: true)]),
            new ApiEndpointDescriptor("jira-specs.resolve-artifact", "Resolve artifact", "Jira Specs",
                HttpMethod.Get, "api/v1/jira-specs/resolve-artifact/{artifactKey}",
                [
                    new ApiParameter("artifactKey", ApiParameterKind.Path, Required: true),
                    new ApiParameter("specKey", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("jira-specs.resolve-page", "Resolve page", "Jira Specs",
                HttpMethod.Get, "api/v1/jira-specs/resolve-page/{pageKey}",
                [
                    new ApiParameter("pageKey", ApiParameterKind.Path, Required: true),
                    new ApiParameter("specKey", ApiParameterKind.Query, Required: false),
                ]),
            new ApiEndpointDescriptor("jira-specs.workgroups", "Work groups", "Jira Specs",
                HttpMethod.Get, "api/v1/jira-specs/workgroups", []),
            new ApiEndpointDescriptor("jira-specs.families", "Families", "Jira Specs",
                HttpMethod.Get, "api/v1/jira-specs/families", []),
            new ApiEndpointDescriptor("jira-specs.by-git-url", "By git URL", "Jira Specs",
                HttpMethod.Get, "api/v1/jira-specs/by-git-url",
                [new ApiParameter("url", ApiParameterKind.Query, Required: false)]),
            new ApiEndpointDescriptor("jira-specs.by-canonical", "By canonical URL", "Jira Specs",
                HttpMethod.Get, "api/v1/jira-specs/by-canonical",
                [new ApiParameter("url", ApiParameterKind.Query, Required: false)]),
        ];

        return list;
    }
}
