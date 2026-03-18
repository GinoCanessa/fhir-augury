using System.CommandLine;
using FhirAugury.Database;
using FhirAugury.Database.Records;

namespace FhirAugury.Cli.Commands;

public static class SnapshotCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption)
    {
        var command = new Command("snapshot", "Render a detailed view of an item");

        var sourceOption = new Option<string>("--source") { Description = "Data source: jira", Arity = ArgumentArity.ExactlyOne };
        var idOption = new Option<string>("--id") { Description = "Item identifier (e.g., FHIR-43499)", Arity = ArgumentArity.ExactlyOne };

        command.Add(sourceOption);
        command.Add(idOption);

        command.SetAction((parseResult, _) =>
        {
            var source = parseResult.GetValue(sourceOption)!;
            var id = parseResult.GetValue(idOption)!;
            var dbPath = parseResult.GetValue(dbOption)!;

            if (source != "jira")
            {
                Console.Error.WriteLine($"Source '{source}' is not supported in Phase 1.");
                return Task.CompletedTask;
            }

            var dbService = new DatabaseService(dbPath);
            dbService.InitializeDatabase();

            using var conn = dbService.OpenConnection();
            var issue = JiraIssueRecord.SelectSingle(conn, Key: id);

            if (issue is null)
            {
                Console.Error.WriteLine($"Issue '{id}' not found.");
                return Task.CompletedTask;
            }

            var comments = JiraCommentRecord.SelectList(conn, IssueKey: id);
            RenderSnapshot(issue, comments);
            return Task.CompletedTask;
        });

        return command;
    }

    private static void RenderSnapshot(JiraIssueRecord issue, List<JiraCommentRecord> comments)
    {
        Console.WriteLine($"# {issue.Key}: {issue.Title}");
        Console.WriteLine();
        Console.WriteLine($"**Type:** {issue.Type}  |  **Priority:** {issue.Priority}  |  **Status:** {issue.Status}");

        if (!string.IsNullOrEmpty(issue.Resolution))
            Console.WriteLine($"**Resolution:** {issue.Resolution}");

        Console.WriteLine($"**Assignee:** {issue.Assignee ?? "Unassigned"}  |  **Reporter:** {issue.Reporter ?? "Unknown"}");
        Console.WriteLine($"**Created:** {issue.CreatedAt:yyyy-MM-dd}  |  **Updated:** {issue.UpdatedAt:yyyy-MM-dd}");

        if (issue.ResolvedAt.HasValue)
            Console.WriteLine($"**Resolved:** {issue.ResolvedAt.Value:yyyy-MM-dd}");

        Console.WriteLine();

        // Custom fields
        var customFields = new (string Label, string? Value)[]
        {
            ("Specification", issue.Specification),
            ("Work Group", issue.WorkGroup),
            ("Raised in Version", issue.RaisedInVersion),
            ("Selected Ballot", issue.SelectedBallot),
            ("Change Type", issue.ChangeType),
            ("Impact", issue.Impact),
            ("Vote", issue.Vote),
            ("Related Artifacts", issue.RelatedArtifacts),
            ("Related Issues", issue.RelatedIssues),
            ("Duplicate Of", issue.DuplicateOf),
            ("Applied Versions", issue.AppliedVersions),
            ("Labels", issue.Labels),
        };

        var hasCustom = false;
        foreach (var (label, value) in customFields)
        {
            if (!string.IsNullOrEmpty(value))
            {
                if (!hasCustom)
                {
                    Console.WriteLine("## Custom Fields");
                    hasCustom = true;
                }
                Console.WriteLine($"- **{label}:** {value}");
            }
        }

        if (hasCustom) Console.WriteLine();

        // Description
        if (!string.IsNullOrEmpty(issue.Description))
        {
            Console.WriteLine("## Description");
            Console.WriteLine(issue.Description);
            Console.WriteLine();
        }

        // Resolution Description
        if (!string.IsNullOrEmpty(issue.ResolutionDescription))
        {
            Console.WriteLine("## Resolution Description");
            Console.WriteLine(issue.ResolutionDescription);
            Console.WriteLine();
        }

        // Comments
        if (comments.Count > 0)
        {
            Console.WriteLine($"## Comments ({comments.Count})");
            Console.WriteLine();
            foreach (var comment in comments.OrderBy(c => c.CreatedAt))
            {
                Console.WriteLine($"### {comment.Author} — {comment.CreatedAt:yyyy-MM-dd HH:mm}");
                Console.WriteLine(comment.Body);
                Console.WriteLine();
            }
        }

        Console.WriteLine($"---");
        Console.WriteLine($"*URL: https://jira.hl7.org/browse/{issue.Key}*");
    }
}
