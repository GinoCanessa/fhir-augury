using System.ComponentModel;
using System.Text;
using Fhiraugury;
using FhirAugury.Common.Text;
using Grpc.Core;
using ModelContextProtocol.Server;

namespace FhirAugury.Mcp.Tools;

[McpServerToolType]
public static class UnifiedTools
{
    [McpServerTool, Description("Search across all FHIR community sources (Zulip, Jira, Confluence, GitHub) using unified search.")]
    public static async Task<string> Search(
        OrchestratorService.OrchestratorServiceClient orchestrator,
        [Description("Search query text")] string query,
        [Description("Comma-separated source filter: zulip,jira,confluence,github")] string? sources = null,
        [Description("Maximum results to return (default 20)")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new UnifiedSearchRequest { Query = query, Limit = limit };

            if (!string.IsNullOrWhiteSpace(sources))
                CsvParser.AddToRepeatedField(request.Sources, sources);

            var response = await orchestrator.UnifiedSearchAsync(request, cancellationToken: cancellationToken);
            return FormatSearchResults(response, query);
        }
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Find items across all sources related to a given item.")]
    public static async Task<string> FindRelated(
        OrchestratorService.OrchestratorServiceClient orchestrator,
        [Description("Source type (jira, zulip, confluence, github)")] string source,
        [Description("Item identifier")] string id,
        [Description("Maximum results (default 20)")] int limit = 20,
        [Description("Comma-separated target sources to search")] string? targetSources = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new FindRelatedRequest { Source = source, Id = id, Limit = limit };

            if (!string.IsNullOrWhiteSpace(targetSources))
                CsvParser.AddToRepeatedField(request.TargetSources, targetSources);

            var response = await orchestrator.FindRelatedAsync(request, cancellationToken: cancellationToken);

            var sb = new StringBuilder();
            sb.AppendLine($"## Related Items for [{response.SeedSource}] {response.SeedId}");
            if (!string.IsNullOrEmpty(response.SeedTitle))
                sb.AppendLine($"**Seed:** {response.SeedTitle}");
            sb.AppendLine();

            if (response.Items.Count == 0)
            {
                sb.AppendLine("No related items found.");
                return sb.ToString();
            }

            foreach (var item in response.Items)
            {
                sb.AppendLine($"### [{item.Source}] {item.Id} — {item.Title}");
                sb.AppendLine($"- **Relevance:** {item.RelevanceScore:F2}");
                if (!string.IsNullOrEmpty(item.Relationship))
                    sb.AppendLine($"- **Relationship:** {item.Relationship}");
                if (!string.IsNullOrEmpty(item.Url))
                    sb.AppendLine($"- **URL:** {item.Url}");
                if (!string.IsNullOrEmpty(item.Snippet))
                    sb.AppendLine($"- **Snippet:** {item.Snippet}");
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get cross-references for a specific item showing links to other sources.")]
    public static async Task<string> GetCrossReferences(
        OrchestratorService.OrchestratorServiceClient orchestrator,
        [Description("Source type (jira, zulip, confluence, github)")] string source,
        [Description("Item identifier")] string id,
        [Description("Direction: outgoing, incoming, or both (default both)")] string direction = "both",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await orchestrator.GetCrossReferencesAsync(
                new GetXRefRequest { Source = source, Id = id, Direction = direction },
                cancellationToken: cancellationToken);

            if (response.References.Count == 0)
                return $"No cross-references found for [{source}] {id}.";

            var sb = new StringBuilder();
            sb.AppendLine($"## Cross-References for [{source}] {id} ({response.References.Count})");
            sb.AppendLine();

            foreach (var xref in response.References)
            {
                var arrow = xref.SourceType == source && xref.SourceId == id ? "→" : "←";
                var otherType = arrow == "→" ? xref.TargetType : xref.SourceType;
                var otherId = arrow == "→" ? xref.TargetId : xref.SourceId;

                sb.AppendLine($"- {arrow} [{otherType}] {otherId}");
                if (!string.IsNullOrEmpty(xref.TargetTitle))
                    sb.AppendLine($"  **Title:** {xref.TargetTitle}");
                if (!string.IsNullOrEmpty(xref.LinkType))
                    sb.AppendLine($"  **Type:** {xref.LinkType}");
                if (!string.IsNullOrEmpty(xref.Context))
                    sb.AppendLine($"  **Context:** {xref.Context}");
            }

            return sb.ToString();
        }
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Get status and statistics of all connected services.")]
    public static async Task<string> GetStats(
        OrchestratorService.OrchestratorServiceClient orchestrator,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await orchestrator.GetServicesStatusAsync(new ServicesStatusRequest(),
                cancellationToken: cancellationToken);

            var sb = new StringBuilder();
            sb.AppendLine("## Services Status");
            sb.AppendLine();
            sb.AppendLine($"**Cross-Reference Links:** {response.CrossRefLinks}");
            if (response.LastXrefScanAt is not null)
                sb.AppendLine($"**Last XRef Scan:** {response.LastXrefScanAt.ToDateTimeOffset():yyyy-MM-dd HH:mm}");
            sb.AppendLine();

            foreach (var svc in response.Services)
            {
                sb.AppendLine($"### {svc.Name}");
                sb.AppendLine($"- **Status:** {svc.Status}");
                sb.AppendLine($"- **Address:** {svc.GrpcAddress}");
                sb.AppendLine($"- **Items:** {svc.ItemCount}");
                if (svc.DbSizeBytes > 0)
                    sb.AppendLine($"- **DB Size:** {FormatBytes(svc.DbSizeBytes)}");
                if (svc.LastSyncAt is not null)
                    sb.AppendLine($"- **Last Sync:** {svc.LastSyncAt.ToDateTimeOffset():yyyy-MM-dd HH:mm}");
                if (!string.IsNullOrEmpty(svc.LastError))
                    sb.AppendLine($"- **Last Error:** {svc.LastError}");
                sb.AppendLine();
            }

            return sb.ToString();
        }
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool, Description("Trigger synchronization/ingestion across source services.")]
    public static async Task<string> TriggerSync(
        OrchestratorService.OrchestratorServiceClient orchestrator,
        [Description("Comma-separated sources to sync (empty for all)")] string? sources = null,
        [Description("Sync type: incremental, full, rebuild (default incremental)")] string type = "incremental",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new TriggerSyncRequest { Type = type };

            if (!string.IsNullOrWhiteSpace(sources))
                CsvParser.AddToRepeatedField(request.Sources, sources);

            var response = await orchestrator.TriggerSyncAsync(request, cancellationToken: cancellationToken);

            var sb = new StringBuilder();
            sb.AppendLine("## Sync Triggered");
            sb.AppendLine();

            foreach (var status in response.Statuses)
            {
                sb.AppendLine($"- **{status.Source}:** {status.Status}");
                if (!string.IsNullOrEmpty(status.Message))
                    sb.AppendLine($"  {status.Message}");
            }

            return sb.ToString();
        }
        catch (RpcException ex)
        {
            return $"Error: {ex.Status.Detail} (Status: {ex.StatusCode})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return $"Error: {ex.Message}";
        }
    }

    internal static string FormatSearchResults(SearchResponse response, string query)
    {
        if (response.Results.Count == 0)
            return $"No results found for \"{query}\".";

        var sb = new StringBuilder();
        sb.AppendLine($"## Search Results ({response.TotalResults} total, showing {response.Results.Count})");
        sb.AppendLine();

        foreach (var r in response.Results)
        {
            sb.AppendLine($"### [{r.Source}] {r.Id} — {r.Title}");
            sb.AppendLine($"- **Score:** {r.Score:F2}");
            if (r.UpdatedAt is not null)
                sb.AppendLine($"- **Updated:** {r.UpdatedAt.ToDateTimeOffset():yyyy-MM-dd}");
            if (!string.IsNullOrEmpty(r.Url))
                sb.AppendLine($"- **URL:** {r.Url}");
            if (!string.IsNullOrEmpty(r.Snippet))
                sb.AppendLine($"- **Snippet:** {r.Snippet}");
            sb.AppendLine();
        }

        if (response.Warnings.Count > 0)
        {
            sb.AppendLine("**Warnings:**");
            foreach (var w in response.Warnings)
                sb.AppendLine($"- {w}");
        }

        return sb.ToString();
    }

    private static string FormatBytes(long bytes) => FormatHelpers.FormatBytes(bytes);
}
