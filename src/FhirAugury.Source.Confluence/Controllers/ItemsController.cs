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
public class ItemsController(ConfluenceDatabase db, IOptions<ConfluenceServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("items")]
    public IActionResult ListItems([FromQuery] int? limit, [FromQuery] int? offset)
    {
        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        ItemListResponse response = PagesController.BuildItemList(connection, options, maxResults, skip, spaceKey: null);
        return Ok(response);
    }

    [HttpGet("items/{id}")]
    public IActionResult GetItem([FromRoute] string id, [FromQuery] bool? includeContent)
    {
        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: id);
        if (page is null)
            return NotFound(new { error = $"Page {id} not found" });

        List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);
        List<ConfluencePageLinkRecord> outLinks = ConfluencePageLinkRecord.SelectList(connection, SourcePageId: page.ConfluenceId);

        ItemResponse response = PagesController.BuildItemResponse(options, page, comments, outLinks);
        if (includeContent == false)
            response = response with { Content = null };

        return Ok(response);
    }

    [HttpGet("items/{id}/related")]
    public IActionResult GetRelatedItems([FromRoute] string id, [FromQuery] int? limit)
    {
        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 10, 50);

        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: id);
        if (page is null)
            return NotFound(new { error = $"Page {id} not found" });

        List<RelatedItem> results = PagesController.BuildRelatedItems(connection, options, id, maxResults);
        return Ok(new FindRelatedResponse(SourceSystems.Confluence, id, page.Title, results));
    }

    [HttpGet("items/{id}/snapshot")]
    public IActionResult GetSnapshot([FromRoute] string id)
    {
        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: id);
        if (page is null)
            return NotFound(new { error = $"Page {id} not found" });

        List<ConfluenceCommentRecord> comments = ConfluenceCommentRecord.SelectList(connection, PageId: page.Id);
        string md = ConfluenceUrlHelper.BuildMarkdownSnapshot(page, comments);

        return Ok(new SnapshotResponse(
            id, SourceSystems.Confluence, md,
            ConfluenceUrlHelper.BuildPageUrl(options, id, page.Url), "page"));
    }

    [HttpGet("items/{id}/content")]
    public IActionResult GetContent([FromRoute] string id, [FromQuery] string? format)
    {
        ConfluenceServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        ConfluencePageRecord? page = ConfluencePageRecord.SelectSingle(connection, ConfluenceId: id);
        if (page is null)
            return NotFound(new { error = $"Page {id} not found" });

        string resolvedFormat = PagesController.ResolveFormat(format);
        string content = resolvedFormat switch
        {
            "raw" => page.BodyStorage ?? "",
            _ => page.BodyPlain ?? "",
        };

        return Ok(new ContentResponse(
            id, SourceSystems.Confluence, content, resolvedFormat,
            ConfluenceUrlHelper.BuildPageUrl(options, id, page.Url), null, "page"));
    }
}
