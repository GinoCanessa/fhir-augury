using System.CommandLine;
using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Indexing;

namespace FhirAugury.Cli.Commands;

public static class SnapshotCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption)
    {
        var command = new Command("snapshot", "Render a detailed view of an item");

        var sourceOption = new Option<string>("--source") { Description = "Data source: jira, zulip", Arity = ArgumentArity.ExactlyOne };
        var idOption = new Option<string>("--id") { Description = "Item identifier (e.g., FHIR-43499 or stream:topic)", Arity = ArgumentArity.ExactlyOne };
        var includeXrefOption = new Option<bool>("--include-xref") { Description = "Include related items from cross-references", DefaultValueFactory = _ => false };

        command.Add(sourceOption);
        command.Add(idOption);
        command.Add(includeXrefOption);

        command.SetAction((parseResult, _) =>
        {
            var source = parseResult.GetValue(sourceOption)!;
            var id = parseResult.GetValue(idOption)!;
            var dbPath = parseResult.GetValue(dbOption)!;
            var includeXref = parseResult.GetValue(includeXrefOption);

            var dbService = new DatabaseService(dbPath);
            dbService.InitializeDatabase();

            using var conn = dbService.OpenConnection();

            switch (source)
            {
                case "jira":
                {
                    var issue = JiraIssueRecord.SelectSingle(conn, Key: id);
                    if (issue is null)
                    {
                        Console.Error.WriteLine($"Issue '{id}' not found.");
                        return Task.CompletedTask;
                    }
                    var comments = JiraCommentRecord.SelectList(conn, IssueKey: id);
                    RenderJiraSnapshot(issue, comments);

                    if (includeXref)
                    {
                        RenderRelatedItems(conn, source, id);
                    }
                    break;
                }

                case "zulip":
                {
                    var sepIdx = id.IndexOf(':');
                    if (sepIdx < 0)
                    {
                        Console.Error.WriteLine("Zulip identifier must be in 'stream:topic' format.");
                        return Task.CompletedTask;
                    }
                    var streamName = id[..sepIdx];
                    var topic = id[(sepIdx + 1)..];

                    var messages = ZulipMessageRecord.SelectList(conn, StreamName: streamName, Topic: topic);
                    if (messages.Count == 0)
                    {
                        Console.Error.WriteLine($"No messages found for '{id}'.");
                        return Task.CompletedTask;
                    }
                    RenderZulipSnapshot(streamName, topic, messages);

                    if (includeXref)
                    {
                        RenderRelatedItems(conn, source, id);
                    }
                    break;
                }

                default:
                    Console.Error.WriteLine($"Source '{source}' is not supported. Available: jira, zulip");
                    break;
            }

            return Task.CompletedTask;
        });

        return command;
    }

    private static void RenderRelatedItems(Microsoft.Data.Sqlite.SqliteConnection conn, string sourceType, string sourceId)
    {
        var xrefLinks = CrossRefQueryService.GetRelatedItems(conn, sourceType, sourceId);
        if (xrefLinks.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("## Related Items (Cross-References)");
        Console.WriteLine();

        foreach (var link in xrefLinks)
        {
            var direction = link.SourceType == sourceType && link.SourceId == sourceId ? "→" : "←";
            var otherType = direction == "→" ? link.TargetType : link.SourceType;
            var otherId = direction == "→" ? link.TargetId : link.SourceId;

            Console.WriteLine($"- {direction} [{otherType}] {otherId}");
            if (!string.IsNullOrEmpty(link.Context))
            {
                Console.WriteLine($"  Context: {link.Context}");
            }
        }
    }

    private static void RenderJiraSnapshot(JiraIssueRecord issue, List<JiraCommentRecord> comments)
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

    private static void RenderZulipSnapshot(string streamName, string topic, List<ZulipMessageRecord> messages)
    {
        Console.WriteLine($"# {streamName} > {topic}");
        Console.WriteLine();
        Console.WriteLine($"**Messages:** {messages.Count}");

        var ordered = messages.OrderBy(m => m.Timestamp).ToList();
        if (ordered.Count > 0)
        {
            Console.WriteLine($"**First message:** {ordered[0].Timestamp:yyyy-MM-dd HH:mm}");
            Console.WriteLine($"**Last message:** {ordered[^1].Timestamp:yyyy-MM-dd HH:mm}");
        }

        var uniqueSenders = messages.Select(m => m.SenderName).Distinct().ToList();
        Console.WriteLine($"**Participants:** {string.Join(", ", uniqueSenders)}");
        Console.WriteLine();

        Console.WriteLine("## Messages");
        Console.WriteLine();

        foreach (var msg in ordered)
        {
            Console.WriteLine($"### {msg.SenderName} — {msg.Timestamp:yyyy-MM-dd HH:mm}");
            Console.WriteLine(msg.ContentPlain);
            Console.WriteLine();
        }

        Console.WriteLine($"---");
        Console.WriteLine($"*URL: https://chat.fhir.org/#narrow/stream/{Uri.EscapeDataString(streamName)}/topic/{Uri.EscapeDataString(topic)}*");
    }
}
