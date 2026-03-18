using FhirAugury.Database;
using FhirAugury.Database.Records;

namespace FhirAugury.Service.Api;

/// <summary>Database statistics endpoints.</summary>
public static class StatsEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var stats = group.MapGroup("/stats");

        stats.MapGet("/", GetOverview);
        stats.MapGet("/{source}", GetSourceStats);
    }

    private static IResult GetOverview(DatabaseService dbService)
    {
        using var conn = dbService.OpenConnection();

        var jiraIssues = JiraIssueRecord.SelectCount(conn);
        var jiraComments = JiraCommentRecord.SelectCount(conn);
        var zulipStreams = ZulipStreamRecord.SelectCount(conn);
        var zulipMessages = ZulipMessageRecord.SelectCount(conn);
        var confluenceSpaces = ConfluenceSpaceRecord.SelectCount(conn);
        var confluencePages = ConfluencePageRecord.SelectCount(conn);
        var githubRepos = GitHubRepoRecord.SelectCount(conn);
        var githubIssues = GitHubIssueRecord.SelectCount(conn);
        var xrefLinks = CrossRefLinkRecord.SelectCount(conn);
        var keywords = KeywordRecord.SelectCount(conn);

        return Results.Ok(new
        {
            JiraIssues = jiraIssues,
            JiraComments = jiraComments,
            ZulipStreams = zulipStreams,
            ZulipMessages = zulipMessages,
            ConfluenceSpaces = confluenceSpaces,
            ConfluencePages = confluencePages,
            GitHubRepos = githubRepos,
            GitHubIssues = githubIssues,
            CrossRefLinks = xrefLinks,
            Keywords = keywords,
            TotalItems = jiraIssues + jiraComments + zulipStreams + zulipMessages + confluencePages + githubIssues,
        });
    }

    private static IResult GetSourceStats(
        string source,
        DatabaseService dbService)
    {
        using var conn = dbService.OpenConnection();

        object? stats = source switch
        {
            "jira" => new
            {
                Issues = JiraIssueRecord.SelectCount(conn),
                Comments = JiraCommentRecord.SelectCount(conn),
                SyncState = GetSyncInfo(conn, "jira"),
            },
            "zulip" => new
            {
                Streams = ZulipStreamRecord.SelectCount(conn),
                Messages = ZulipMessageRecord.SelectCount(conn),
                SyncState = GetSyncInfo(conn, "zulip"),
            },
            "confluence" => new
            {
                Spaces = ConfluenceSpaceRecord.SelectCount(conn),
                Pages = ConfluencePageRecord.SelectCount(conn),
                SyncState = GetSyncInfo(conn, "confluence"),
            },
            "github" => new
            {
                Repos = GitHubRepoRecord.SelectCount(conn),
                Issues = GitHubIssueRecord.SelectCount(conn),
                Comments = GitHubCommentRecord.SelectCount(conn),
                SyncState = GetSyncInfo(conn, "github"),
            },
            _ => null,
        };

        if (stats is null)
        {
            return Results.NotFound(new ProblemResponse("Unknown source", $"Source '{source}' is not available."));
        }

        return Results.Ok(stats);
    }

    private static object? GetSyncInfo(Microsoft.Data.Sqlite.SqliteConnection conn, string sourceName)
    {
        var syncState = SyncStateRecord.SelectSingle(conn, SourceName: sourceName);
        if (syncState is null) return null;

        return new
        {
            syncState.Status,
            LastSyncAt = syncState.LastSyncAt.ToString("o"),
            syncState.ItemsIngested,
            syncState.LastError,
        };
    }
}
