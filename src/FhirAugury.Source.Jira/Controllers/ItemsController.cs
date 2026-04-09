using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Database.Records;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Controllers;

[ApiController]
[Route("api/v1")]
public class ItemsController(JiraDatabase db, IOptions<JiraServiceOptions> optionsAccessor) : ControllerBase
{
    [HttpGet("items/{key}")]
    public IActionResult GetItem([FromRoute] string key, [FromQuery] bool? includeContent, [FromQuery] bool? includeComments)
    {
        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: key);
        if (issue is null)
            return NotFound(new { error = $"Issue {key} not found" });

        Dictionary<string, string> metadata = new()
        {
            ["status"] = issue.Status,
            ["type"] = issue.Type,
            ["priority"] = issue.Priority,
        };
        if (issue.WorkGroup is not null) metadata["work_group"] = issue.WorkGroup;
        if (issue.Specification is not null) metadata["specification"] = issue.Specification;
        if (issue.Resolution is not null) metadata["resolution"] = issue.Resolution;
        if (issue.Assignee is not null) metadata["assignee"] = issue.Assignee;
        if (issue.Reporter is not null) metadata["reporter"] = issue.Reporter;
        if (issue.Labels is not null) metadata["labels"] = issue.Labels;
        if (issue.ResolutionDescription is not null) metadata["resolution_description"] = issue.ResolutionDescription;

        List<CommentInfo>? comments = null;
        if (includeComments == true)
        {
            List<JiraCommentRecord> commentRecords = JiraCommentRecord.SelectList(connection, IssueKey: key);
            comments = commentRecords.Select(c => new CommentInfo(
                c.Id.ToString(), c.Author, c.Body, c.CreatedAt, null)).ToList();
        }

        ItemResponse response = new ItemResponse
        {
            Source = SourceSystems.Jira,
            Id = issue.Key,
            Title = issue.Title,
            Content = includeContent == true ? issue.Description : null,
            Url = $"{options.BaseUrl}/browse/{issue.Key}",
            CreatedAt = issue.CreatedAt,
            UpdatedAt = issue.UpdatedAt,
            Metadata = metadata,
            Comments = comments,
        };

        return Ok(response);
    }

    [HttpGet("items")]
    public IActionResult ListItems([FromQuery] int? limit, [FromQuery] int? offset)
    {
        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 50, 500);
        int skip = Math.Max(offset ?? 0, 0);

        using SqliteCommand cmd = new SqliteCommand(
            "SELECT Key, Title, Status, Type, UpdatedAt FROM jira_issues ORDER BY UpdatedAt DESC LIMIT @limit OFFSET @offset",
            connection);
        cmd.Parameters.AddWithValue("@limit", maxResults);
        cmd.Parameters.AddWithValue("@offset", skip);

        List<ItemSummary> items = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(0);
            items.Add(new ItemSummary
            {
                Id = key,
                Title = reader.GetString(1),
                Url = $"{options.BaseUrl}/browse/{key}",
                UpdatedAt = JiraUrlHelper.ParseTimestamp(reader, 4),
                Metadata = new Dictionary<string, string>
                {
                    ["status"] = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    ["type"] = reader.IsDBNull(3) ? "" : reader.GetString(3),
                },
            });
        }

        return Ok(new ItemListResponse(items.Count, items));
    }

    [HttpGet("items/{key}/related")]
    public IActionResult GetRelatedItems([FromRoute] string key, [FromQuery] string? seedSource, [FromQuery] int? limit)
    {
        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        int maxResults = Math.Min(limit ?? 10, 50);

        // Cross-source related: Zulip seed
        if (!string.IsNullOrEmpty(seedSource) &&
            !string.Equals(seedSource, SourceSystems.Jira, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(seedSource, SourceSystems.Zulip, StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = key.Split(':', 2);
                if (parts.Length == 2 && int.TryParse(parts[0], out int streamId))
                {
                    string topicName = parts[1];
                    List<ZulipXRefRecord> refs = ZulipXRefRecord.SelectList(connection,
                        StreamId: streamId, TopicName: topicName);

                    HashSet<string> seen = [];
                    List<RelatedItem> crossItems = [];
                    foreach (ZulipXRefRecord zRef in refs)
                    {
                        if (!seen.Add(zRef.SourceId)) continue;
                        JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: zRef.SourceId);
                        if (issue is null) continue;
                        crossItems.Add(new RelatedItem
                        {
                            Source = SourceSystems.Jira,
                            Id = issue.Key,
                            Title = issue.Title,
                            Url = $"{options.BaseUrl}/browse/{issue.Key}",
                            RelevanceScore = 1.0,
                            Relationship = "zulip_xref",
                        });
                        if (crossItems.Count >= maxResults) break;
                    }
                    return Ok(new FindRelatedResponse(seedSource, key, null, crossItems));
                }
            }

            // Unknown cross-source — no results
            return Ok(new FindRelatedResponse(seedSource, key, null, []));
        }

        // Same-source related via issue links
        List<JiraIssueLinkRecord> outLinks = JiraIssueLinkRecord.SelectList(connection, SourceKey: key);
        List<JiraIssueLinkRecord> inLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: key);

        IEnumerable<(string Key, string LinkType)> relatedKeys = outLinks.Select(l => (Key: l.TargetKey, l.LinkType))
            .Concat(inLinks.Select(l => (Key: l.SourceKey, l.LinkType)))
            .DistinctBy(x => x.Key)
            .Take(maxResults);

        List<RelatedItem> results = [];
        foreach ((string relKey, string linkType) in relatedKeys)
        {
            JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: relKey);
            if (issue is null) continue;
            results.Add(new RelatedItem
            {
                Source = SourceSystems.Jira,
                Id = issue.Key,
                Title = issue.Title,
                Url = $"{options.BaseUrl}/browse/{issue.Key}",
                Relationship = linkType,
            });
        }

        JiraIssueRecord? seedIssue = JiraIssueRecord.SelectSingle(connection, Key: key);
        return Ok(new FindRelatedResponse(SourceSystems.Jira, key, seedIssue?.Title, results));
    }

    [HttpGet("items/{key}/snapshot")]
    public IActionResult GetSnapshot([FromRoute] string key, [FromQuery] bool? includeComments, [FromQuery] bool? includeRefs)
    {
        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: key);
        if (issue is null)
            return NotFound(new { error = $"Issue {key} not found" });

        string md = JiraUrlHelper.BuildMarkdownSnapshot(connection, issue, includeComments ?? true, includeRefs ?? true);

        return Ok(new SnapshotResponse(issue.Key, SourceSystems.Jira, md,
            $"{options.BaseUrl}/browse/{issue.Key}", "issue"));
    }

    [HttpGet("items/{key}/content")]
    public IActionResult GetContent([FromRoute] string key, [FromQuery] string? format)
    {
        JiraServiceOptions options = optionsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: key);
        if (issue is null)
            return NotFound(new { error = $"Issue {key} not found" });

        return Ok(new ContentResponse(issue.Key, SourceSystems.Jira,
            issue.Description ?? "", format ?? ContentFormats.Text,
            $"{options.BaseUrl}/browse/{issue.Key}", null, "issue"));
    }

    [HttpGet("items/{key}/comments")]
    public IActionResult GetComments([FromRoute] string key)
    {
        using SqliteConnection connection = db.OpenConnection();
        JiraIssueRecord? issue = JiraIssueRecord.SelectSingle(connection, Key: key);
        if (issue is null)
            return NotFound(new { error = $"Issue {key} not found" });

        List<JiraCommentRecord> comments = JiraCommentRecord.SelectList(connection, IssueKey: key);
        List<JiraCommentEntry> entries = comments.Select(c =>
            new JiraCommentEntry(c.Id.ToString(), c.IssueKey, c.Author, c.Body, c.CreatedAt)).ToList();

        return Ok(entries);
    }

    [HttpGet("items/{key}/links")]
    public IActionResult GetLinks([FromRoute] string key)
    {
        using SqliteConnection connection = db.OpenConnection();
        List<JiraIssueLinkRecord> outLinks = JiraIssueLinkRecord.SelectList(connection, SourceKey: key);
        List<JiraIssueLinkRecord> inLinks = JiraIssueLinkRecord.SelectList(connection, TargetKey: key);

        List<JiraIssueLinkEntry> links = outLinks.Select(l => new JiraIssueLinkEntry(l.SourceKey, l.TargetKey, l.LinkType))
            .Concat(inLinks.Select(l => new JiraIssueLinkEntry(l.SourceKey, l.TargetKey, l.LinkType)))
            .ToList();

        return Ok(links);
    }
}