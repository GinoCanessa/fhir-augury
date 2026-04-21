using System;
using System.Collections.Generic;
using System.Linq;

namespace FhirAugury.DevUi.Services.ApiCatalog;

/// <summary>
/// Resolves the per-source <see cref="ApiEndpointDescriptor"/> catalog used by the
/// API tester page.
/// </summary>
public static class SourceApiCatalog
{
    public const string Orchestrator = "orchestrator";
    public const string Jira = "jira";
    public const string Zulip = "zulip";
    public const string GitHub = "github";
    public const string Confluence = "confluence";

    private static readonly Dictionary<string, IReadOnlyList<ApiEndpointDescriptor>> _byName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [Orchestrator] = Catalogs.OrchestratorCatalog.Build(),
            [Jira] = Catalogs.JiraCatalog.Build(),
            [Zulip] = Catalogs.ZulipCatalog.Build(),
            [GitHub] = Catalogs.GitHubCatalog.Build(),
            [Confluence] = Catalogs.ConfluenceCatalog.Build(),
        };

    public static IReadOnlyList<ApiEndpointDescriptor> GetCatalog(string sourceName)
    {
        if (string.IsNullOrEmpty(sourceName)) return [];
        return _byName.TryGetValue(sourceName, out IReadOnlyList<ApiEndpointDescriptor>? list) ? list : [];
    }

    public static IEnumerable<string> KnownSources => _byName.Keys;
}
