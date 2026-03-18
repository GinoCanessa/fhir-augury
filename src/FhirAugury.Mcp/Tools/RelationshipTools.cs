using System.ComponentModel;
using System.Text;
using FhirAugury.Database;
using FhirAugury.Indexing;
using ModelContextProtocol.Server;

namespace FhirAugury.Mcp.Tools;

[McpServerToolType]
public static class RelationshipTools
{
    [McpServerTool, Description("Find items related to a given item across all sources, using keyword similarity and cross-references.")]
    public static string FindRelated(
        DatabaseService db,
        [Description("Source type: zulip, jira, confluence, github")] string source,
        [Description("Item identifier (e.g., FHIR-43499 for jira, stream:topic for zulip, page ID for confluence, repo#number for github)")] string id,
        [Description("Maximum results (default 20)")] int limit = 20)
    {
        using var conn = db.OpenConnection();
        var results = SimilaritySearchService.FindRelated(conn, source, id, limit);

        if (results.Count == 0)
            return $"No related items found for [{source}] {id}.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Related Items for [{source}] {id} ({results.Count} matches)");
        sb.AppendLine();

        foreach (var r in results)
        {
            sb.AppendLine($"- [{r.Source}] **{r.Id}** — {r.Title} (score: {r.Score:F2})");
            if (!string.IsNullOrEmpty(r.Url))
                sb.AppendLine($"  URL: {r.Url}");
        }

        return sb.ToString();
    }

    [McpServerTool, Description("Get explicit cross-references for an item (mentions, links from/to other sources).")]
    public static string GetCrossReferences(
        DatabaseService db,
        [Description("Source type: zulip, jira, confluence, github")] string source,
        [Description("Item identifier")] string id)
    {
        using var conn = db.OpenConnection();
        var xrefs = CrossRefQueryService.GetRelatedItems(conn, source, id);

        if (xrefs.Count == 0)
            return $"No cross-references found for [{source}] {id}.";

        var sb = new StringBuilder();
        sb.AppendLine($"## Cross-References for [{source}] {id} ({xrefs.Count})");
        sb.AppendLine();

        var outbound = xrefs.Where(x => x.SourceType == source && x.SourceId == id).ToList();
        var inbound = xrefs.Where(x => x.TargetType == source && x.TargetId == id).ToList();

        if (outbound.Count > 0)
        {
            sb.AppendLine("### Outbound (this item references)");
            foreach (var link in outbound)
            {
                sb.AppendLine($"- → [{link.TargetType}] {link.TargetId}");
                if (!string.IsNullOrEmpty(link.Context))
                    sb.AppendLine($"  Context: {link.Context}");
            }
            sb.AppendLine();
        }

        if (inbound.Count > 0)
        {
            sb.AppendLine("### Inbound (referenced by)");
            foreach (var link in inbound)
            {
                sb.AppendLine($"- ← [{link.SourceType}] {link.SourceId}");
                if (!string.IsNullOrEmpty(link.Context))
                    sb.AppendLine($"  Context: {link.Context}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}
