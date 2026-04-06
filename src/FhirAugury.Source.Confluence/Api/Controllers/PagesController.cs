using System.Text;
using FhirAugury.Source.Confluence.Configuration;
using FhirAugury.Source.Confluence.Database;
using FhirAugury.Source.Confluence.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Confluence.Api.Controllers;

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

        return Ok(new
        {
            page.ConfluenceId,
            page.SpaceKey,
            page.Title,
            bodyPlain = page.BodyPlain,
            page.Labels,
            page.VersionNumber,
            page.LastModifiedBy,
            page.LastModifiedAt,
            page.ParentId,
            url = page.Url ?? $"{options.BaseUrl}/pages/{pageId}",
            comments = comments.Select(c => new { c.Author, c.Body, c.CreatedAt }),
            links = outLinks.Select(l => new { l.TargetPageId, l.LinkType }),
        });
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

        List<ConfluencePageLinkRecord> outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: pageId);
        List<ConfluencePageLinkRecord> inLinks = ConfluencePageLinkRecord.SelectList(connection, TargetPageId: pageId);

        List<string> relatedIds = outLinks.Select(l => l.TargetPageId)
            .Concat(inLinks.Select(l => l.SourcePageId))
            .Distinct()
            .Take(maxResults)
            .ToList();

        List<object> results = [];
        foreach (string relId in relatedIds)
        {
            ConfluencePageRecord? related = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: relId);
            if (related is null) continue;
            results.Add(new
            {
                pageId = related.ConfluenceId,
                title = related.Title,
                spaceKey = related.SpaceKey,
                url = related.Url ?? $"{options.BaseUrl}/pages/{related.ConfluenceId}",
            });
        }

        return Ok(new { sourceKey = pageId, related = results });
    }

    [HttpGet("pages/{pageId}/snapshot")]
    public IActionResult GetPageSnapshot([FromRoute] string pageId)
    {
        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
        if (page is null)
            return NotFound(new { error = $"Page {pageId} not found" });

        StringBuilder md = new StringBuilder();
        md.AppendLine($"# {page.Title}");
        md.AppendLine();
        md.AppendLine($"**Space:** {page.SpaceKey}  ");
        md.AppendLine($"**Version:** {page.VersionNumber}  ");
        if (page.LastModifiedBy is not null) md.AppendLine($"**Last Modified By:** {page.LastModifiedBy}  ");
        md.AppendLine($"**Last Modified:** {page.LastModifiedAt:yyyy-MM-dd}  ");
        if (page.Labels is not null) md.AppendLine($"**Labels:** {page.Labels}  ");
        md.AppendLine();
        if (!string.IsNullOrEmpty(page.BodyPlain))
        {
            md.AppendLine("## Content");
            md.AppendLine();
            md.AppendLine(page.BodyPlain);
            md.AppendLine();
        }

        List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);
        if (comments.Count > 0)
        {
            md.AppendLine("## Comments");
            foreach (ConfluenceCommentRecord c in comments) { md.AppendLine($"**{c.Author}** ({c.CreatedAt:yyyy-MM-dd}): {c.Body}"); md.AppendLine(); }
        }

        return Ok(new { key = pageId, markdown = md.ToString(), url = page.Url ?? $"{options.BaseUrl}/pages/{pageId}" });
    }

    [HttpGet("pages/{pageId}/content")]
    public IActionResult GetPageContent([FromRoute] string pageId, [FromQuery] string? format)
    {
        using SqliteConnection connection = db.OpenConnection();
        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: pageId);
        if (page is null)
            return NotFound(new { error = $"Page {pageId} not found" });

        string content = format?.Equals("storage", StringComparison.OrdinalIgnoreCase) == true
            ? (page.BodyStorage ?? "")
            : (page.BodyPlain ?? "");

        return Ok(new { key = pageId, content, format = format ?? "text" });
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
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

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

        List<object> items = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new
            {
                pageId = reader.GetString(0),
                title = reader.GetString(1),
                spaceKey = reader.IsDBNull(2) ? null : reader.GetString(2),
                lastModifiedAt = reader.IsDBNull(3) ? null : reader.GetString(3),
            });
        }

        return Ok(new { total = items.Count, items });
    }
}