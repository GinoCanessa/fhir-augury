using FhirAugury.Database;
using FhirAugury.Database.Records;
using FhirAugury.Indexing;

namespace FhirAugury.Service.Api;

/// <summary>Confluence-specific data access endpoints.</summary>
public static class ConfluenceEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var confluence = group.MapGroup("/confluence");

        confluence.MapGet("/pages", ListPages);
        confluence.MapGet("/pages/{id}", GetPage);
    }

    private static IResult ListPages(
        DatabaseService dbService,
        HttpContext context)
    {
        var limitStr = context.Request.Query["limit"].FirstOrDefault();
        var offsetStr = context.Request.Query["offset"].FirstOrDefault();
        var limit = int.TryParse(limitStr, out var l) ? l : 50;
        var offset = int.TryParse(offsetStr, out var o) ? o : 0;

        var spaceKey = context.Request.Query["space"].FirstOrDefault();
        var query = context.Request.Query["q"].FirstOrDefault();

        using var conn = dbService.OpenConnection();

        if (!string.IsNullOrEmpty(query))
        {
            var results = FtsSearchService.SearchConfluencePages(conn, query, limit, spaceKey);
            return Results.Ok(results);
        }

        List<ConfluencePageRecord> pages;
        if (!string.IsNullOrEmpty(spaceKey))
        {
            pages = ConfluencePageRecord.SelectList(conn, SpaceKey: spaceKey);
        }
        else
        {
            pages = ConfluencePageRecord.SelectList(conn);
        }

        var paged = pages
            .OrderByDescending(p => p.LastModifiedAt)
            .Skip(offset)
            .Take(limit)
            .Select(p => new
            {
                p.ConfluenceId,
                p.Title,
                p.SpaceKey,
                p.Labels,
                p.VersionNumber,
                p.LastModifiedBy,
                LastModifiedAt = p.LastModifiedAt.ToString("o"),
                p.Url,
            });

        return Results.Ok(new
        {
            Total = pages.Count,
            Offset = offset,
            Limit = limit,
            Items = paged,
        });
    }

    private static IResult GetPage(
        string id,
        DatabaseService dbService)
    {
        using var conn = dbService.OpenConnection();
        var page = ConfluencePageRecord.SelectSingle(conn, ConfluenceId: id);

        if (page is null)
        {
            return Results.NotFound(new ProblemResponse("Not found", $"Page '{id}' not found."));
        }

        var comments = ConfluenceCommentRecord.SelectList(conn, ConfluencePageId: id);

        return Results.Ok(new
        {
            Page = page,
            Comments = comments,
        });
    }
}
