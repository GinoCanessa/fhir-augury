using FhirAugury.Database;
using FhirAugury.Indexing;

namespace FhirAugury.Service.Api;

/// <summary>Search endpoints for FTS5 and cross-source search.</summary>
public static class SearchEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var search = group.MapGroup("/search");

        search.MapGet("/", UnifiedSearch);
        search.MapGet("/{source}", SourceSearch);
    }

    private static IResult UnifiedSearch(
        DatabaseService dbService,
        HttpContext context)
    {
        var query = context.Request.Query["q"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(query))
        {
            return Results.BadRequest(new ProblemResponse("Missing query", "Provide ?q= parameter."));
        }

        var sourcesParam = context.Request.Query["sources"].FirstOrDefault();
        var limitStr = context.Request.Query["limit"].FirstOrDefault();
        var limit = int.TryParse(limitStr, out var l) ? l : 20;

        HashSet<string>? sources = null;
        if (!string.IsNullOrEmpty(sourcesParam))
        {
            sources = [.. sourcesParam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        using var conn = dbService.OpenConnection();
        var results = FtsSearchService.SearchAll(conn, query, sources, limit);

        return Results.Ok(results);
    }

    private static IResult SourceSearch(
        string source,
        DatabaseService dbService,
        HttpContext context)
    {
        var query = context.Request.Query["q"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(query))
        {
            return Results.BadRequest(new ProblemResponse("Missing query", "Provide ?q= parameter."));
        }

        var limitStr = context.Request.Query["limit"].FirstOrDefault();
        var limit = int.TryParse(limitStr, out var l) ? l : 20;
        var filter = context.Request.Query["filter"].FirstOrDefault();

        using var conn = dbService.OpenConnection();

        var results = source switch
        {
            "jira" => FtsSearchService.SearchJiraIssues(conn, query, limit, filter),
            "jira-comment" => FtsSearchService.SearchJiraComments(conn, query, limit),
            "zulip" => FtsSearchService.SearchZulipMessages(conn, query, limit, filter),
            "confluence" => FtsSearchService.SearchConfluencePages(conn, query, limit, filter),
            "github" => FtsSearchService.SearchGitHubIssues(conn, query, limit, filter),
            _ => null,
        };

        if (results is null)
        {
            return Results.NotFound(new ProblemResponse("Unknown source", $"Source '{source}' is not available."));
        }

        return Results.Ok(results);
    }
}
