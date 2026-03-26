using System.Text;
using System.Text.Json;
using Fhiraugury;
using static FhirAugury.Common.Text.FormatHelpers;

namespace FhirAugury.Cli.OutputFormatters;

public static class OutputFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void FormatSearchResults(SearchResponse response, string format)
    {
        switch (format.ToLowerInvariant())
        {
            case "json":
                PrintJson(response.Results.Select(r => new
                {
                    r.Source, r.Id, r.Title, r.Score,
                    Updated = r.UpdatedAt?.ToDateTimeOffset().ToString("yyyy-MM-dd"),
                    r.Url, r.Snippet,
                }));
                break;
            case "markdown":
            case "md":
                Console.WriteLine("| Source | ID | Title | Score | Updated |");
                Console.WriteLine("|--------|-----|-------|-------|---------|");
                foreach (SearchResultItem? r in response.Results)
                {
                    string updated = r.UpdatedAt?.ToDateTimeOffset().ToString("yyyy-MM-dd") ?? "";
                    Console.WriteLine($"| {r.Source} | {r.Id} | {r.Title} | {r.Score:F2} | {updated} |");
                }
                break;
            default:
                Console.WriteLine($"{"Source",-12} {"ID",-16} {"Title",-45} {"Score",8} {"Updated",-12}");
                Console.WriteLine($"{"─────────",-12} {"──────────────",-16} {"───────────────────────────────────────────",-45} {"──────",8} {"──────────",-12}");
                foreach (SearchResultItem? r in response.Results)
                {
                    string title = r.Title.Length > 43 ? r.Title[..40] + "..." : r.Title;
                    string updated = r.UpdatedAt?.ToDateTimeOffset().ToString("yyyy-MM-dd") ?? "";
                    Console.WriteLine($"{r.Source,-12} {r.Id,-16} {title,-45} {r.Score,8:F2} {updated,-12}");
                }
                Console.WriteLine();
                Console.WriteLine($"{response.TotalResults} result(s)");
                break;
        }

        if (response.Warnings.Count > 0)
        {
            Console.Error.WriteLine();
            foreach (string? w in response.Warnings)
                Console.Error.WriteLine($"Warning: {w}");
        }
    }

    public static void FormatItem(ItemResponse item, string format)
    {
        switch (format.ToLowerInvariant())
        {
            case "json":
                PrintJson(new
                {
                    item.Source, item.Id, item.Title, item.Content, item.Url,
                    Created = item.CreatedAt?.ToDateTimeOffset(),
                    Updated = item.UpdatedAt?.ToDateTimeOffset(),
                    item.Metadata,
                    Comments = item.Comments.Select(c => new { c.Id, c.Author, c.Body, Created = c.CreatedAt?.ToDateTimeOffset() }),
                });
                break;
            case "markdown":
            case "md":
                Console.WriteLine($"## {item.Id}: {item.Title}");
                Console.WriteLine();
                Console.WriteLine("| Field | Value |");
                Console.WriteLine("|-------|-------|");
                foreach ((string? k, string? v) in item.Metadata)
                    Console.WriteLine($"| {FormatKey(k)} | {v} |");
                if (item.CreatedAt is not null)
                    Console.WriteLine($"| Created | {item.CreatedAt.ToDateTimeOffset():yyyy-MM-dd} |");
                if (item.UpdatedAt is not null)
                    Console.WriteLine($"| Updated | {item.UpdatedAt.ToDateTimeOffset():yyyy-MM-dd} |");
                if (!string.IsNullOrEmpty(item.Content))
                {
                    Console.WriteLine();
                    Console.WriteLine("### Description");
                    Console.WriteLine(item.Content);
                }
                if (item.Comments.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"### Comments ({item.Comments.Count})");
                    foreach (Comment? c in item.Comments)
                        Console.WriteLine($"\n**{c.Author}** ({c.CreatedAt?.ToDateTimeOffset():yyyy-MM-dd}):\n{c.Body}");
                }
                break;
            default:
                Console.WriteLine($"ID:       {item.Id}");
                Console.WriteLine($"Source:   {item.Source}");
                Console.WriteLine($"Title:    {item.Title}");
                foreach ((string? k, string? v) in item.Metadata)
                    Console.WriteLine($"{FormatKey(k) + ":",-14}{v}");
                if (item.CreatedAt is not null)
                    Console.WriteLine($"Created:  {item.CreatedAt.ToDateTimeOffset():yyyy-MM-dd}");
                if (item.UpdatedAt is not null)
                    Console.WriteLine($"Updated:  {item.UpdatedAt.ToDateTimeOffset():yyyy-MM-dd}");
                if (!string.IsNullOrEmpty(item.Url))
                    Console.WriteLine($"URL:      {item.Url}");
                if (!string.IsNullOrEmpty(item.Content))
                {
                    Console.WriteLine();
                    Console.WriteLine(Truncate(item.Content, 500));
                }
                if (item.Comments.Count > 0)
                {
                    Console.WriteLine($"\nComments ({item.Comments.Count}):");
                    foreach (Comment? c in item.Comments)
                        Console.WriteLine($"  [{c.CreatedAt?.ToDateTimeOffset():yyyy-MM-dd}] {c.Author}: {Truncate(c.Body, 100)}");
                }
                break;
        }
    }

    public static void FormatRelated(FindRelatedResponse response, string format)
    {
        switch (format.ToLowerInvariant())
        {
            case "json":
                PrintJson(response.Items.Select(i => new
                {
                    i.Source, i.Id, i.Title, i.RelevanceScore, i.Relationship, i.Url, i.Snippet,
                }));
                break;
            case "markdown":
            case "md":
                Console.WriteLine($"## Related to [{response.SeedSource}] {response.SeedId}: {response.SeedTitle}");
                Console.WriteLine();
                Console.WriteLine("| Source | ID | Title | Relevance | Relationship |");
                Console.WriteLine("|--------|-----|-------|-----------|-------------|");
                foreach (RelatedItem? i in response.Items)
                    Console.WriteLine($"| {i.Source} | {i.Id} | {i.Title} | {i.RelevanceScore:F2} | {i.Relationship} |");
                break;
            default:
                Console.WriteLine($"Related to [{response.SeedSource}] {response.SeedId}: {response.SeedTitle}");
                Console.WriteLine();
                Console.WriteLine($"{"Source",-12} {"ID",-16} {"Title",-40} {"Score",8} {"Relationship",-15}");
                Console.WriteLine($"{"─────────",-12} {"──────────────",-16} {"──────────────────────────────────────",-40} {"──────",8} {"────────────",-15}");
                foreach (RelatedItem? i in response.Items)
                {
                    string title = i.Title.Length > 38 ? i.Title[..35] + "..." : i.Title;
                    Console.WriteLine($"{i.Source,-12} {i.Id,-16} {title,-40} {i.RelevanceScore,8:F2} {i.Relationship,-15}");
                }
                Console.WriteLine();
                Console.WriteLine($"{response.Items.Count} related item(s)");
                break;
        }
    }

    public static void FormatCrossReferences(GetXRefResponse response, string sourceType, string sourceId, string format)
    {
        switch (format.ToLowerInvariant())
        {
            case "json":
                PrintJson(response.References.Select(x => new
                {
                    x.SourceType, x.SourceId, x.TargetType, x.TargetId, x.LinkType, x.Context, x.TargetTitle,
                }));
                break;
            case "markdown":
            case "md":
                Console.WriteLine($"## Cross-References for [{sourceType}] {sourceId}");
                Console.WriteLine();
                Console.WriteLine("| Direction | Type | ID | Link Type | Title |");
                Console.WriteLine("|-----------|------|-----|-----------|-------|");
                foreach (CrossReference? x in response.References)
                {
                    (string? arrow, string? otherType, string? otherId) = GetDirection(x, sourceType, sourceId);
                    Console.WriteLine($"| {arrow} | {otherType} | {otherId} | {x.LinkType} | {x.TargetTitle} |");
                }
                break;
            default:
                Console.WriteLine($"Cross-references for [{sourceType}] {sourceId} ({response.References.Count}):");
                Console.WriteLine();
                foreach (CrossReference? x in response.References)
                {
                    (string? arrow, string? otherType, string? otherId) = GetDirection(x, sourceType, sourceId);
                    Console.WriteLine($"  {arrow} [{otherType}] {otherId}  ({x.LinkType})");
                    if (!string.IsNullOrEmpty(x.TargetTitle))
                        Console.WriteLine($"    Title: {x.TargetTitle}");
                    if (!string.IsNullOrEmpty(x.Context))
                        Console.WriteLine($"    Context: {x.Context}");
                }
                break;
        }
    }

    public static void FormatServicesStatus(ServicesStatusResponse response, string format)
    {
        switch (format.ToLowerInvariant())
        {
            case "json":
                PrintJson(new
                {
                    LastXrefScan = response.LastXrefScanAt?.ToDateTimeOffset(),
                    Services = response.Services.Select(s => new
                    {
                        s.Name, s.Status, s.GrpcAddress, s.ItemCount, s.DbSizeBytes,
                        LastSync = s.LastSyncAt?.ToDateTimeOffset(), s.LastError,
                    }),
                });
                break;
            case "markdown":
            case "md":
                Console.WriteLine("## Services Status");
                Console.WriteLine();
                Console.WriteLine($"**Last XRef Scan:** {response.LastXrefScanAt?.ToDateTimeOffset():yyyy-MM-dd HH:mm}");
                Console.WriteLine();
                Console.WriteLine("| Service | Status | Items | DB Size | Last Sync |");
                Console.WriteLine("|---------|--------|-------|---------|-----------|");
                foreach (ServiceHealth? s in response.Services)
                {
                    string dbSize = FormatBytes(s.DbSizeBytes);
                    string lastSync = s.LastSyncAt?.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm") ?? "never";
                    Console.WriteLine($"| {s.Name} | {s.Status} | {s.ItemCount} | {dbSize} | {lastSync} |");
                }
                break;
            default:
                if (response.LastXrefScanAt is not null)
                    Console.WriteLine($"Last XRef Scan:        {response.LastXrefScanAt.ToDateTimeOffset():yyyy-MM-dd HH:mm}");
                Console.WriteLine();
                Console.WriteLine($"{"Service",-12} {"Status",-10} {"Items",8} {"DB Size",10} {"Last Sync",-20} {"Error",-30}");
                Console.WriteLine($"{"─────────",-12} {"────────",-10} {"──────",8} {"────────",10} {"──────────────────",-20} {"────────────────────────────",-30}");
                foreach (ServiceHealth? s in response.Services)
                {
                    string dbSize = FormatBytes(s.DbSizeBytes);
                    string lastSync = s.LastSyncAt?.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm") ?? "never";
                    string error = string.IsNullOrEmpty(s.LastError) ? "" : Truncate(s.LastError, 28);
                    Console.WriteLine($"{s.Name,-12} {s.Status,-10} {s.ItemCount,8} {dbSize,10} {lastSync,-20} {error,-30}");
                }
                break;
        }
    }

    public static void FormatSyncStatus(TriggerSyncResponse response, string format)
    {
        switch (format.ToLowerInvariant())
        {
            case "json":
                PrintJson(response.Statuses.Select(s => new { s.Source, s.Status, s.Message }));
                break;
            default:
                Console.WriteLine("Sync triggered:");
                foreach (SourceSyncStatus? s in response.Statuses)
                {
                    Console.WriteLine($"  {s.Source}: {s.Status}");
                    if (!string.IsNullOrEmpty(s.Message))
                        Console.WriteLine($"    {s.Message}");
                }
                break;
        }
    }

    private static (string Arrow, string OtherType, string OtherId) GetDirection(
        CrossReference xref, string sourceType, string sourceId) =>
        xref.SourceType == sourceType && xref.SourceId == sourceId
            ? ("→", xref.TargetType, xref.TargetId)
            : ("←", xref.SourceType, xref.SourceId);

    internal static string FormatKey(string key) => FhirAugury.Common.Text.FormatHelpers.FormatKey(key);

    internal static string FormatBytes(long bytes) => FhirAugury.Common.Text.FormatHelpers.FormatBytes(bytes);

    private static string Truncate(string text, int maxLength)
    {
        string singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length > maxLength ? singleLine[..(maxLength - 3)] + "..." : singleLine;
    }

    private static void PrintJson<T>(T obj) =>
        Console.WriteLine(JsonSerializer.Serialize(obj, JsonOptions));
}
