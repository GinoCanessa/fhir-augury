using FhirAugury.Common;
using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Controllers;

[ApiController]
[Route("api/v1")]
public class SearchController(ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) : ControllerBase
{
    [HttpGet("search")]
    public IActionResult Search([FromQuery] string? q, [FromQuery] int? limit, [FromQuery] int? offset)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Query parameter 'q' is required" });

        using SqliteConnection connection = db.OpenConnection();
        string ftsQuery = FtsQueryHelper.SanitizeFtsQuery(q);
        if (string.IsNullOrEmpty(ftsQuery))
            return Ok(new SearchResponse(q, 0, [], null));

        int maxResults = Math.Min(limit ?? 20, 200);
        int skip = Math.Max(offset ?? 0, 0);

        string sql = """
            SELECT zm.ZulipMessageId, zm.StreamName, zm.Topic,
                   snippet(zulip_messages_fts, 0, '<b>', '</b>', '...', 20) as Snippet,
                   zulip_messages_fts.rank, zm.SenderName, zm.Timestamp,
                   COALESCE(zs.BaselineValue, 5) as BaselineValue
            FROM zulip_messages_fts
            JOIN zulip_messages zm ON zm.Id = zulip_messages_fts.rowid
            LEFT JOIN zulip_streams zs ON zs.Id = zm.StreamId
            WHERE zulip_messages_fts MATCH @query
            ORDER BY (zulip_messages_fts.rank * COALESCE(zs.BaselineValue, 5) / 5.0)
            LIMIT @limit OFFSET @offset
            """;

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", maxResults);
        cmd.Parameters.AddWithValue("@offset", skip);

        List<SearchResult> results = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int msgId = reader.GetInt32(0);
            string streamName = reader.GetString(1);
            string topic = reader.GetString(2);
            double rawRank = reader.GetDouble(4);
            int baselineValue = reader.GetInt32(7);
            results.Add(new SearchResult
            {
                Source = SourceSystems.Zulip,
                Id = msgId.ToString(),
                Title = $"[{streamName}] {topic}",
                Snippet = reader.IsDBNull(3) ? null : reader.GetString(3),
                Score = -rawRank * (baselineValue / 5.0),
                Url = ZulipUrlHelper.BuildMessageUrl(options, streamName, topic, msgId),
                UpdatedAt = ZulipUrlHelper.ParseTimestamp(reader, 6),
            });
        }

        return Ok(new SearchResponse(q, results.Count, results, null));
    }
}