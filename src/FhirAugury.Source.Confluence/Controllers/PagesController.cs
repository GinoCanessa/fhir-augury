using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Controllers;

[ApiController]
[Route("api/v1")]
public class PagesController(ConfluenceDatabase db, IOptions<ConfluenceServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("pages/{pageId}")]
    public IActionResult GetPage([FromRoute] string pageId)
    {
        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
        if (page is null)
            return NotFound(new { error = $"Page {pageId} not found" });

        List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);
        List<ConfluencePageLinkRecord> outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: page.ConfluenceId);

        return Ok(BuildItemResponse(options, page, comments, outLinks));
    }

    [HttpGet("pages/{pageId}/related")]
    public IActionResult GetRelatedPages([FromRoute] string pageId, [FromQuery] int? limit)
    {
        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 10, 50);

        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
        if (page is null)
            return NotFound(new { error = $"Page {pageId} not found" });

        List<RelatedItem> results = BuildRelatedItems(connection, options, pageId, maxResults);
        return Ok(new FindRelatedResponse(SourceSystems.Confluence, pageId, page.Title, results));
    }

    [HttpGet("pages/{pageId}/snapshot")]
    public IActionResult GetPageSnapshot([FromRoute] string pageId)
    {
        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
        if (page is null)
            return NotFound(new { error = $"Page {pageId} not found" });

        List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);
        string md = ConfluenceUrlHelper.BuildMarkdownSnapshot(page, comments);

        return Ok(new SnapshotResponse(
            pageId, SourceSystems.Confluence, md,
            ConfluenceUrlHelper.BuildPageUrl(options, pageId, page.Url), "page"));
    }

    [HttpGet("pages/{pageId}/content")]
    public IActionResult GetPageContent([FromRoute] string pageId, [FromQuery] string? format)
    {
        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
        if (page is null)
            return NotFound(new { error = $"Page {pageId} not found" });

        string resolvedFormat = ResolveFormat(format);
        string content = resolvedFormat switch
        {
            "raw" => page.BodyStorage ?? "",
            _ => page.BodyPlain ?? "",
        };

        return Ok(new ContentResponse(
            pageId, SourceSystems.Confluence, content, resolvedFormat,
            ConfluenceUrlHelper.BuildPageUrl(options, pageId, page.Url), null, "page"));
    }

    [HttpGet("pages/{pageId}/comments")]
    public IActionResult GetPageComments([FromRoute] string pageId)
    {
        using SqliteConnection connection = db.OpenConnection();
        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
        if (page is null)
            return NotFound(new { error = $"Page {pageId} not found" });

        List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);

        return Ok(comments.Select(c => new
        {
            id = c.Id.ToString(),
            pageId = c.PageId,
            author = c.Author,
            body = c.Body ?? "",
            createdAt = c.CreatedAt,
            url = page.Url ?? "",
        }));
    }

    [HttpGet("pages/{pageId}/children")]
    public IActionResult GetPageChildren([FromRoute] string pageId)
    {
        using SqliteConnection connection = db.OpenConnection();
        ConfluencePageRecord? parentPage = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
        if (parentPage is null)
            return NotFound(new { error = $"Page {pageId} not found" });

        string sql = "SELECT Id, ConfluenceId, SpaceKey, Title, Url, LastModifiedAt FROM confluence_pages WHERE ParentId = @parentId";
        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@parentId", parentPage.ConfluenceId);

        List<object> children = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            children.Add(new
            {
                id = reader.GetInt32(0),
                confluenceId = reader.IsDBNull(1) ? null : reader.GetString(1),
                spaceKey = reader.GetString(2),
                title = reader.GetString(3),
                url = reader.IsDBNull(4) ? "" : reader.GetString(4),
                lastModifiedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }

        return Ok(children);
    }

    [HttpGet("pages/{pageId}/ancestors")]
    public IActionResult GetPageAncestors([FromRoute] string pageId)
    {
        using SqliteConnection connection = db.OpenConnection();
        ConfluencePageRecord? current = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
        if (current is null)
            return NotFound(new { error = $"Page {pageId} not found" });

        List<object> ancestors = [];
        HashSet<string> visited = [];
        string? parentId = current.ParentId;

        while (!string.IsNullOrEmpty(parentId) && !visited.Contains(parentId))
        {
            visited.Add(parentId);
            ConfluencePageRecord? parent = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: parentId);
            if (parent is null) break;

            ancestors.Add(new
            {
                id = parent.Id,
                confluenceId = parent.ConfluenceId,
                spaceKey = parent.SpaceKey,
                title = parent.Title,
                url = parent.Url ?? "",
                lastModifiedAt = parent.LastModifiedAt,
            });

            parentId = parent.ParentId;
        }

        // Root-first order
        ancestors.Reverse();
        return Ok(ancestors);
    }

    [HttpGet("pages/{pageId}/linked")]
    public IActionResult GetLinkedPages([FromRoute] string pageId, [FromQuery] string? direction)
    {
        using SqliteConnection connection = db.OpenConnection();
        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
        if (page is null)
            return NotFound(new { error = $"Page {pageId} not found" });

        List<string> linkedPageIds = [];
        string dir = direction?.ToLowerInvariant() ?? "both";

        if (dir is "outgoing" or "both")
        {
            List<ConfluencePageLinkRecord> outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: page.ConfluenceId);
            linkedPageIds.AddRange(outLinks.Select(l => l.TargetPageId));
        }

        if (dir is "incoming" or "both")
        {
            List<ConfluencePageLinkRecord> inLinks = ConfluencePageLinkRecord.SelectList(connection, TargetPageId: page.ConfluenceId);
            linkedPageIds.AddRange(inLinks.Select(l => l.SourcePageId));
        }

        List<object> results = [];
        foreach (string linkedId in linkedPageIds.Distinct())
        {
            ConfluencePageRecord? linked = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: linkedId);
            if (linked is null) continue;

            results.Add(new
            {
                id = linked.Id,
                confluenceId = linked.ConfluenceId,
                spaceKey = linked.SpaceKey,
                title = linked.Title,
                url = linked.Url ?? "",
                lastModifiedAt = linked.LastModifiedAt,
            });
        }

        return Ok(results);
    }

    [HttpGet("pages/by-label/{label}")]
    public IActionResult GetPagesByLabel([FromRoute] string label, [FromQuery] string? spaceKey, [FromQuery] int? limit, [FromQuery] int? offset)
    {
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        string sql = "SELECT Id, ConfluenceId, SpaceKey, Title, Url, LastModifiedAt FROM confluence_pages WHERE Labels LIKE @label";
        List<SqliteParameter> parameters = [new("@label", $"%{label}%")];

        if (!string.IsNullOrEmpty(spaceKey))
        {
            sql += " AND SpaceKey = @spaceKey";
            parameters.Add(new SqliteParameter("@spaceKey", spaceKey));
        }

        sql += " ORDER BY LastModifiedAt DESC LIMIT @limit OFFSET @offset";
        parameters.Add(new SqliteParameter("@limit", maxResults));
        parameters.Add(new SqliteParameter("@offset", skip));

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

        List<object> items = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new
            {
                id = reader.GetInt32(0),
                confluenceId = reader.IsDBNull(1) ? null : reader.GetString(1),
                spaceKey = reader.IsDBNull(2) ? null : reader.GetString(2),
                title = reader.GetString(3),
                url = reader.IsDBNull(4) ? "" : reader.GetString(4),
                lastModifiedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }

        return Ok(new { label, total = items.Count, items });
    }

    [HttpGet("pages")]
    public IActionResult GetPages([FromQuery] int? limit, [FromQuery] int? offset, [FromQuery] string? spaceKey)
    {
        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        List<ItemSummary> items = ListPageSummaries(connection, options, maxResults, skip, spaceKey);
        return Ok(new ItemListResponse(items.Count, items));
    }

    // --- Shared helpers used by both PagesController and ItemsController ---

    internal static ItemListResponse BuildItemList(
        SqliteConnection connection, ConfluenceServiceOptions options, int maxResults, int skip, string? spaceKey)
    {
        List<ItemSummary> items = ListPageSummaries(connection, options, maxResults, skip, spaceKey);
        return new ItemListResponse(items.Count, items);
    }

    internal static ItemResponse BuildItemResponse(
        ConfluenceServiceOptions options,
        ConfluencePageRecord page,
        List<ConfluenceCommentRecord> comments,
        List<ConfluencePageLinkRecord> outLinks)
    {
        Dictionary<string, string> metadata = new()
        {
            ["space_key"] = page.SpaceKey,
            ["version"] = page.VersionNumber.ToString(),
        };
        if (page.Labels is not null) metadata["labels"] = page.Labels;
        if (page.ParentId is not null) metadata["parent_id"] = page.ParentId;
        if (page.LastModifiedBy is not null) metadata["last_modified_by"] = page.LastModifiedBy;

        return new ItemResponse
        {
            Source = SourceSystems.Confluence,
            ContentType = "page",
            Id = page.ConfluenceId,
            Title = page.Title,
            Content = page.BodyPlain,
            Url = ConfluenceUrlHelper.BuildPageUrl(options, page.ConfluenceId, page.Url),
            UpdatedAt = page.LastModifiedAt,
            Metadata = metadata,
            Comments = comments.Select(c => new CommentInfo(
                c.Id.ToString(), c.Author, c.Body ?? "", c.CreatedAt, null)).ToList(),
        };
    }

    internal static List<RelatedItem> BuildRelatedItems(
        SqliteConnection connection, ConfluenceServiceOptions options, string pageId, int maxResults)
    {
        List<ConfluencePageLinkRecord> outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: pageId);
        List<ConfluencePageLinkRecord> inLinks = ConfluencePageLinkRecord.SelectList(connection, TargetPageId: pageId);

        List<string> relatedIds = outLinks.Select(l => l.TargetPageId)
            .Concat(inLinks.Select(l => l.SourcePageId))
            .Distinct()
            .Take(maxResults)
            .ToList();

        List<RelatedItem> results = [];
        foreach (string relId in relatedIds)
        {
            ConfluencePageRecord? related = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: relId);
            if (related is null) continue;
            results.Add(new RelatedItem
            {
                Source = SourceSystems.Confluence,
                Id = related.ConfluenceId,
                Title = related.Title,
                Url = ConfluenceUrlHelper.BuildPageUrl(options, related.ConfluenceId, related.Url),
                Relationship = "page_link",
            });
        }

        return results;
    }

    internal static string ResolveFormat(string? format)
    {
        return format?.ToLowerInvariant() switch
        {
            "raw" or "storage" => "raw",
            "html" => "html",
            _ => "text",
        };
    }

    private static List<ItemSummary> ListPageSummaries(
        SqliteConnection connection, ConfluenceServiceOptions options, int maxResults, int skip, string? spaceKey)
    {
        string sql = "SELECT ConfluenceId, Title, SpaceKey, LastModifiedAt FROM confluence_pages";
        List<SqliteParameter> parameters = [];

        if (!string.IsNullOrEmpty(spaceKey))
        {
            sql += " WHERE SpaceKey = @spaceKey";
            parameters.Add(new SqliteParameter("@spaceKey", spaceKey));
        }

        sql += " ORDER BY LastModifiedAt DESC LIMIT @limit OFFSET @offset";
        parameters.Add(new SqliteParameter("@limit", maxResults));
        parameters.Add(new SqliteParameter("@offset", skip));

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

        List<ItemSummary> items = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string pageId = reader.GetString(0);
            items.Add(new ItemSummary
            {
                Id = pageId,
                Title = reader.GetString(1),
                Url = ConfluenceUrlHelper.BuildPageUrl(options, pageId, null),
                UpdatedAt = ConfluenceUrlHelper.ParseTimestamp(reader.IsDBNull(3) ? null : reader.GetString(3)),
                Metadata = new Dictionary<string, string>
                {
                    ["space_key"] = reader.IsDBNull(2) ? "" : reader.GetString(2),
                },
            });
        }

        return items;
    }
}