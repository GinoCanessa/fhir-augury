using System.Text.Json;
using FhirAugury.Database.Records;
using FhirAugury.Models;

namespace FhirAugury.Cli.OutputFormatters;

public static class OutputFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Format(List<SearchResult> results, string format)
    {
        switch (format.ToLowerInvariant())
        {
            case "json":
                FormatJson(results);
                break;
            case "markdown":
            case "md":
                FormatMarkdown(results);
                break;
            default:
                FormatTable(results);
                break;
        }
    }

    public static void FormatJiraIssue(JiraIssueRecord issue, List<JiraCommentRecord> comments, string format)
    {
        switch (format.ToLowerInvariant())
        {
            case "json":
                Console.WriteLine(JsonSerializer.Serialize(new { issue, comments }, JsonOptions));
                break;
            case "markdown":
            case "md":
                FormatJiraIssueMarkdown(issue, comments);
                break;
            default:
                FormatJiraIssueTable(issue, comments);
                break;
        }
    }

    private static void FormatTable(List<SearchResult> results)
    {
        Console.WriteLine($"{"Source",-12} {"ID",-16} {"Title",-45} {"Score",8} {"Updated",-12}");
        Console.WriteLine($"{"─────────",-12} {"──────────────",-16} {"───────────────────────────────────────────",-45} {"──────",8} {"──────────",-12}");

        foreach (var r in results)
        {
            var title = r.Title.Length > 43 ? r.Title[..40] + "..." : r.Title;
            var updated = r.UpdatedAt?.ToString("yyyy-MM-dd") ?? "";
            var score = r.NormalizedScore?.ToString("F2") ?? r.Score.ToString("F1");
            Console.WriteLine($"{r.Source,-12} {r.Id,-16} {title,-45} {score,8} {updated,-12}");
        }

        Console.WriteLine();
        Console.WriteLine($"{results.Count} result(s)");
    }

    private static void FormatJson(List<SearchResult> results)
    {
        Console.WriteLine(JsonSerializer.Serialize(results, JsonOptions));
    }

    private static void FormatMarkdown(List<SearchResult> results)
    {
        Console.WriteLine("| Source | ID | Title | Score | Updated |");
        Console.WriteLine("|--------|-----|-------|-------|---------|");
        foreach (var r in results)
        {
            var score = r.NormalizedScore?.ToString("F2") ?? r.Score.ToString("F1");
            var updated = r.UpdatedAt?.ToString("yyyy-MM-dd") ?? "";
            Console.WriteLine($"| {r.Source} | {r.Id} | {r.Title} | {score} | {updated} |");
        }
    }

    private static void FormatJiraIssueTable(JiraIssueRecord issue, List<JiraCommentRecord> comments)
    {
        Console.WriteLine($"Key:          {issue.Key}");
        Console.WriteLine($"Title:        {issue.Title}");
        Console.WriteLine($"Type:         {issue.Type}");
        Console.WriteLine($"Priority:     {issue.Priority}");
        Console.WriteLine($"Status:       {issue.Status}");
        Console.WriteLine($"Resolution:   {issue.Resolution ?? "Unresolved"}");
        Console.WriteLine($"Assignee:     {issue.Assignee ?? "Unassigned"}");
        Console.WriteLine($"Reporter:     {issue.Reporter ?? "Unknown"}");
        Console.WriteLine($"Created:      {issue.CreatedAt:yyyy-MM-dd}");
        Console.WriteLine($"Updated:      {issue.UpdatedAt:yyyy-MM-dd}");

        if (!string.IsNullOrEmpty(issue.Specification))
            Console.WriteLine($"Specification:{issue.Specification}");
        if (!string.IsNullOrEmpty(issue.WorkGroup))
            Console.WriteLine($"Work Group:   {issue.WorkGroup}");
        if (!string.IsNullOrEmpty(issue.Labels))
            Console.WriteLine($"Labels:       {issue.Labels}");

        if (comments.Count > 0)
        {
            Console.WriteLine($"\nComments ({comments.Count}):");
            foreach (var c in comments.OrderBy(c => c.CreatedAt))
            {
                Console.WriteLine($"  [{c.CreatedAt:yyyy-MM-dd}] {c.Author}: {Truncate(c.Body, 100)}");
            }
        }
    }

    private static void FormatJiraIssueMarkdown(JiraIssueRecord issue, List<JiraCommentRecord> comments)
    {
        Console.WriteLine($"## {issue.Key}: {issue.Title}");
        Console.WriteLine();
        Console.WriteLine($"| Field | Value |");
        Console.WriteLine($"|-------|-------|");
        Console.WriteLine($"| Type | {issue.Type} |");
        Console.WriteLine($"| Priority | {issue.Priority} |");
        Console.WriteLine($"| Status | {issue.Status} |");
        Console.WriteLine($"| Assignee | {issue.Assignee ?? "Unassigned"} |");
        Console.WriteLine($"| Reporter | {issue.Reporter ?? "Unknown"} |");

        if (!string.IsNullOrEmpty(issue.Description))
        {
            Console.WriteLine();
            Console.WriteLine("### Description");
            Console.WriteLine(issue.Description);
        }

        if (comments.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"### Comments ({comments.Count})");
            foreach (var c in comments.OrderBy(c => c.CreatedAt))
            {
                Console.WriteLine($"\n**{c.Author}** ({c.CreatedAt:yyyy-MM-dd}):\n{c.Body}");
            }
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length > maxLength ? singleLine[..(maxLength - 3)] + "..." : singleLine;
    }
}
