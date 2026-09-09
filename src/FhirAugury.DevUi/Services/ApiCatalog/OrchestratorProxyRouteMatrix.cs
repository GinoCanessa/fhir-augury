using System.Collections.Generic;
using FhirAugury.DevUi.Services.ApiCatalog.Catalogs;

namespace FhirAugury.DevUi.Services.ApiCatalog;

public enum OrchestratorProxyRouteDisposition
{
    OrchestratorNative,
    TypedSourceProxy,
    OrchestratorReadinessOrMetaReplacement,
}

public sealed record OrchestratorProxyRoute(
    string SourceName,
    ApiEndpointDescriptor SourceDescriptor,
    OrchestratorProxyRouteDisposition Disposition,
    ApiEndpointDescriptor GatewayDescriptor);

/// <summary>
/// Exhaustive mapping from every source API-test descriptor to the route that
/// the Dev UI invokes through the Orchestrator.
/// </summary>
public static class OrchestratorProxyRouteMatrix
{
    private static readonly string[] s_sourceNames = ["jira", "zulip", "confluence", "github", "fhir"];

    private static readonly IReadOnlyList<OrchestratorProxyRoute> s_routes = BuildRoutes();

    public static IReadOnlyList<OrchestratorProxyRoute> Routes => s_routes;

    public static IReadOnlyList<OrchestratorProxyRoute> GetRoutes(string sourceName)
    {
        if (string.IsNullOrWhiteSpace(sourceName))
            return [];

        return s_routes
            .Where(route => string.Equals(route.SourceName, sourceName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static IReadOnlyList<ApiEndpointDescriptor> GetGatewayCatalog(string sourceName) =>
        GetRoutes(sourceName).Select(route => route.GatewayDescriptor).ToList();

    public static bool IsKnownSource(string sourceName) =>
        s_sourceNames.Contains(sourceName, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<OrchestratorProxyRoute> BuildRoutes()
    {
        Dictionary<string, OrchestratorProxyRouteDisposition> policies = BuildPolicies();
        List<OrchestratorProxyRoute> routes = [];

        AddSourceRoutes("jira", JiraCatalog.Build(), policies, routes);
        AddSourceRoutes("zulip", ZulipCatalog.Build(), policies, routes);
        AddSourceRoutes("confluence", ConfluenceCatalog.Build(), policies, routes);
        AddSourceRoutes("github", GitHubCatalog.Build(), policies, routes);
        AddSourceRoutes("fhir", FhirCatalog.Build(), policies, routes);

        if (policies.Count > 0)
        {
            throw new InvalidOperationException(
                "The Orchestrator route matrix contains stale entries: " +
                string.Join(", ", policies.Keys.Order(StringComparer.OrdinalIgnoreCase)));
        }

        return routes.AsReadOnly();
    }

    private static void AddSourceRoutes(
        string sourceName,
        IReadOnlyList<ApiEndpointDescriptor> sourceCatalog,
        Dictionary<string, OrchestratorProxyRouteDisposition> policies,
        List<OrchestratorProxyRoute> routes)
    {
        HashSet<string> descriptorIds = new(StringComparer.OrdinalIgnoreCase);
        foreach (ApiEndpointDescriptor descriptor in sourceCatalog)
        {
            if (!descriptorIds.Add(descriptor.Id))
            {
                throw new InvalidOperationException(
                    $"Source catalog '{sourceName}' contains duplicate descriptor id '{descriptor.Id}'.");
            }

            string key = PolicyKey(sourceName, descriptor.Id);
            if (!policies.Remove(key, out OrchestratorProxyRouteDisposition disposition))
            {
                throw new InvalidOperationException(
                    $"Source operation '{sourceName}:{descriptor.Id}' has no route-matrix disposition.");
            }

            routes.Add(new OrchestratorProxyRoute(
                sourceName,
                descriptor,
                disposition,
                BuildGatewayDescriptor(sourceName, descriptor, disposition)));
        }
    }

    private static ApiEndpointDescriptor BuildGatewayDescriptor(
        string sourceName,
        ApiEndpointDescriptor sourceDescriptor,
        OrchestratorProxyRouteDisposition disposition)
    {
        return disposition switch
        {
            OrchestratorProxyRouteDisposition.OrchestratorNative =>
                BuildNativeDescriptor(sourceName, sourceDescriptor),
            OrchestratorProxyRouteDisposition.TypedSourceProxy =>
                BuildTypedProxyDescriptor(sourceName, sourceDescriptor),
            OrchestratorProxyRouteDisposition.OrchestratorReadinessOrMetaReplacement =>
                BuildReplacementDescriptor(sourceDescriptor),
            _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null),
        };
    }

    private static ApiEndpointDescriptor BuildNativeDescriptor(
        string sourceName,
        ApiEndpointDescriptor sourceDescriptor)
    {
        string path = sourceDescriptor.Id switch
        {
            "content.search" => $"api/v1/content/search?sources={sourceName}",
            "content.get-item" => $"api/v1/content/item/{sourceName}/{{**id}}",
            "content.keywords" => $"api/v1/content/keywords/{sourceName}/{{**id}}",
            _ => throw new InvalidOperationException(
                $"No native gateway projection is defined for '{sourceName}:{sourceDescriptor.Id}'."),
        };

        IReadOnlyList<ApiParameter> parameters = sourceDescriptor.Parameters
            .Where(parameter => !IsInjectedSourceParameter(sourceDescriptor.Id, parameter.Name))
            .ToList();

        return sourceDescriptor with
        {
            PathTemplate = path,
            Parameters = parameters,
            Description = AppendGatewayDescription(
                sourceDescriptor.Description,
                $"Runs through the Orchestrator with source '{sourceName}' fixed by this tab."),
        };
    }

    private static ApiEndpointDescriptor BuildTypedProxyDescriptor(
        string sourceName,
        ApiEndpointDescriptor sourceDescriptor)
    {
        const string ApiV1 = "api/v1";
        string remainder = sourceDescriptor.PathTemplate.StartsWith(ApiV1, StringComparison.Ordinal)
            ? sourceDescriptor.PathTemplate[ApiV1.Length..]
            : "/" + sourceDescriptor.PathTemplate.TrimStart('/');
        if (!remainder.StartsWith("/", StringComparison.Ordinal))
            remainder = "/" + remainder;

        string path = $"api/v1/{sourceName}{remainder}";
        IReadOnlyList<ApiParameter> parameters = sourceDescriptor.Parameters.ToList();

        if (sourceDescriptor.Id == "content.related-by-keyword")
        {
            path = $"api/v1/{sourceName}/content/related-by-keyword/{sourceName}/{{**id}}";
            parameters = parameters
                .Where(parameter => !string.Equals(parameter.Name, "source", StringComparison.Ordinal))
                .ToList();
        }

        return sourceDescriptor with
        {
            PathTemplate = path,
            Parameters = parameters,
            Description = AppendGatewayDescription(
                sourceDescriptor.Description,
                $"Proxied through the Orchestrator to the {sourceName} source."),
        };
    }

    private static ApiEndpointDescriptor BuildReplacementDescriptor(ApiEndpointDescriptor sourceDescriptor)
    {
        string path = sourceDescriptor.Id switch
        {
            "lifecycle.stats" => "api/v1/stats",
            "lifecycle.health" or "lifecycle.status" => "api/v1/services",
            _ => throw new InvalidOperationException(
                $"No readiness/meta replacement is defined for '{sourceDescriptor.Id}'."),
        };

        return sourceDescriptor with
        {
            DisplayName = sourceDescriptor.Id == "lifecycle.stats"
                ? "Stats (gateway aggregate)"
                : $"{sourceDescriptor.DisplayName} (gateway readiness)",
            PathTemplate = path,
            Parameters = [],
            Description = AppendGatewayDescription(
                sourceDescriptor.Description,
                "Uses the Orchestrator's typed aggregate contract instead of a source-local lifecycle response."),
        };
    }

    private static bool IsInjectedSourceParameter(string descriptorId, string parameterName) =>
        descriptorId switch
        {
            "content.search" => string.Equals(parameterName, "sources", StringComparison.Ordinal),
            "content.get-item" or "content.keywords" =>
                string.Equals(parameterName, "source", StringComparison.Ordinal),
            _ => false,
        };

    private static string AppendGatewayDescription(string? description, string gatewayDescription) =>
        string.IsNullOrWhiteSpace(description)
            ? gatewayDescription
            : $"{description} {gatewayDescription}";

    private static Dictionary<string, OrchestratorProxyRouteDisposition> BuildPolicies()
    {
        Dictionary<string, OrchestratorProxyRouteDisposition> policies =
            new(StringComparer.OrdinalIgnoreCase);

        string[] contentSources = ["jira", "zulip", "confluence", "github"];
        AddPolicies(policies, contentSources,
            ["content.search", "content.get-item", "content.keywords"],
            OrchestratorProxyRouteDisposition.OrchestratorNative);
        AddPolicies(policies, contentSources,
            [
                "content.refers-to",
                "content.referred-by",
                "content.cross-referenced",
                "content.related-by-keyword",
            ],
            OrchestratorProxyRouteDisposition.TypedSourceProxy);

        AddPolicies(policies, s_sourceNames,
            ["lifecycle.status", "lifecycle.stats", "lifecycle.health"],
            OrchestratorProxyRouteDisposition.OrchestratorReadinessOrMetaReplacement);

        AddPolicies(policies, contentSources,
            [
                "ingestion.ingest",
                "ingestion.trigger",
                "ingestion.rebuild",
                "ingestion.rebuild-index",
                "ingestion.notify-peer",
            ],
            OrchestratorProxyRouteDisposition.TypedSourceProxy);

        AddPolicies(policies, ["jira"],
            [
                "items.list", "items.get", "items.related", "items.snapshot", "items.content",
                "items.comments", "items.links",
                "projects.list", "projects.get", "projects.update",
                "query.flexible", "query.labels", "query.statuses", "query.users", "query.inpersons",
                "specs.list", "specs.get", "specs.issue-numbers",
                "work-groups.list", "work-groups.issues-by-code", "work-groups.issues",
                "local-processing.tickets", "local-processing.random-ticket",
                "local-processing.set-processed", "local-processing.clear-all-processed",
                "pss.list", "pss.get", "baldef.list", "baldef.get", "ballot.list", "ballot.get",
            ],
            OrchestratorProxyRouteDisposition.TypedSourceProxy);

        AddPolicies(policies, ["zulip"],
            [
                "items.list", "items.get", "items.related", "items.snapshot", "items.content",
                "items.comments", "items.links",
                "messages.get", "messages.list", "messages.by-user",
                "streams.list", "streams.get", "streams.update", "streams.topics",
                "threads.get", "threads.snapshot", "query.flexible",
            ],
            OrchestratorProxyRouteDisposition.TypedSourceProxy);

        AddPolicies(policies, ["confluence"],
            [
                "ingestion.block", "ingestion.block-clear",
                "items.list", "items.get", "items.related", "items.snapshot", "items.content",
                "pages.list", "pages.get", "pages.related", "pages.snapshot", "pages.content",
                "pages.comments", "pages.children", "pages.ancestors", "pages.linked",
                "pages.by-label", "spaces.list", "cache.reconcileReport",
            ],
            OrchestratorProxyRouteDisposition.TypedSourceProxy);

        AddPolicies(policies, ["github"],
            [
                "items.list", "items.get", "items.related", "items.snapshot", "items.content",
                "items.comments", "items.commits", "items.pr", "items.pr-tickets",
                "items.ticket-prs",
                "repos.list", "repos.get", "tags.list", "tags.files", "tags.search",
                "jira-specs.list", "jira-specs.get", "jira-specs.artifacts", "jira-specs.pages",
                "jira-specs.versions", "jira-specs.resolve-artifact", "jira-specs.resolve-page",
                "jira-specs.workgroups", "jira-specs.families", "jira-specs.by-git-url",
                "jira-specs.by-canonical",
                "workgroups.list", "workgroups.files", "workgroups.artifacts",
                "workgroups.resolve", "workgroups.unresolved",
            ],
            OrchestratorProxyRouteDisposition.TypedSourceProxy);

        AddPolicies(policies, ["fhir"],
            [
                "releases.list",
                "structures.resources", "structures.datatypes", "structures.profiles",
                "structures.interfaces", "structures.get", "structures.elements",
                "structures.element",
                "codesystems.list", "codesystems.lookup", "codesystems.concepts",
                "codesystems.concept",
                "valuesets.list", "valuesets.lookup", "valuesets.concepts", "valuesets.bindings",
                "operations.list", "operations.get",
                "searchparameters.list", "searchparameters.get",
                "resolve.get", "search.query",
            ],
            OrchestratorProxyRouteDisposition.TypedSourceProxy);

        return policies;
    }

    private static void AddPolicies(
        Dictionary<string, OrchestratorProxyRouteDisposition> policies,
        IEnumerable<string> sourceNames,
        IEnumerable<string> descriptorIds,
        OrchestratorProxyRouteDisposition disposition)
    {
        foreach (string sourceName in sourceNames)
        {
            foreach (string descriptorId in descriptorIds)
            {
                string key = PolicyKey(sourceName, descriptorId);
                if (!policies.TryAdd(key, disposition))
                {
                    throw new InvalidOperationException(
                        $"Source operation '{key}' has more than one route-matrix disposition.");
                }
            }
        }
    }

    private static string PolicyKey(string sourceName, string descriptorId) =>
        $"{sourceName}:{descriptorId}";
}
