using System.CommandLine;
using FhirAugury.Cli.OutputFormatters;
using FhirAugury.Database;
using FhirAugury.Database.Records;

namespace FhirAugury.Cli.Commands;

public static class GetCommand
{
    public static Command Create(Option<string> dbOption, Option<bool> verboseOption, Option<bool> jsonOption)
    {
        var command = new Command("get", "Retrieve a specific item by identifier");

        var sourceOption = new Option<string>("--source") { Description = "Data source: jira, zulip, confluence, github", Arity = ArgumentArity.ExactlyOne };
        var idOption = new Option<string>("--id") { Description = "Item identifier (e.g., FHIR-43499 or stream:topic)", Arity = ArgumentArity.ExactlyOne };
        var formatOption = new Option<string>("--format", "-f") { Description = "Output format: table | json | markdown", DefaultValueFactory = _ => "table" };

        command.Add(sourceOption);
        command.Add(idOption);
        command.Add(formatOption);

        command.SetAction((parseResult, _) =>
        {
            var source = parseResult.GetValue(sourceOption)!;
            var id = parseResult.GetValue(idOption)!;
            var dbPath = parseResult.GetValue(dbOption)!;
            var format = parseResult.GetValue(jsonOption) ? "json" : parseResult.GetValue(formatOption)!;

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
                    OutputFormatter.FormatJiraIssue(issue, comments, format);
                    break;
                }

                case "zulip":
                {
                    var (streamName, topic) = ParseZulipIdentifier(id);
                    if (streamName is null)
                        return Task.CompletedTask;

                    var messages = ZulipMessageRecord.SelectList(conn, StreamName: streamName, Topic: topic!);
                    if (messages.Count == 0)
                    {
                        Console.Error.WriteLine($"No messages found for '{id}'.");
                        return Task.CompletedTask;
                    }
                    OutputFormatter.FormatZulipThread(streamName, topic!, messages, format);
                    break;
                }

                case "confluence":
                {
                    var page = ConfluencePageRecord.SelectSingle(conn, ConfluenceId: id);
                    if (page is null)
                    {
                        Console.Error.WriteLine($"Confluence page '{id}' not found.");
                        return Task.CompletedTask;
                    }
                    var comments = ConfluenceCommentRecord.SelectList(conn, ConfluencePageId: id);
                    OutputFormatter.FormatConfluencePage(page, comments, format);
                    break;
                }

                case "github":
                {
                    var ghIssue = GitHubIssueRecord.SelectSingle(conn, UniqueKey: id);
                    if (ghIssue is null)
                    {
                        Console.Error.WriteLine($"GitHub issue '{id}' not found.");
                        return Task.CompletedTask;
                    }
                    var ghComments = GitHubCommentRecord.SelectList(conn, RepoFullName: ghIssue.RepoFullName, IssueNumber: ghIssue.Number);
                    OutputFormatter.FormatGitHubIssue(ghIssue, ghComments, format);
                    break;
                }

                default:
                    Console.Error.WriteLine($"Source '{source}' is not supported. Available: jira, zulip, confluence, github");
                    break;
            }

            return Task.CompletedTask;
        });

        return command;
    }

    /// <summary>Parses a "stream:topic" identifier using the last colon as separator,
    /// since stream names may contain colons.</summary>
    internal static (string? StreamName, string? Topic) ParseZulipIdentifier(string id)
    {
        var sepIdx = id.LastIndexOf(':');
        if (sepIdx <= 0)
        {
            Console.Error.WriteLine("Zulip identifier must be in 'stream:topic' format.");
            return (null, null);
        }
        return (id[..sepIdx], id[(sepIdx + 1)..]);
    }
}
