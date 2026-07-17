using FhirAugury.Source.Zulip.Configuration;
using FhirAugury.Source.Zulip.Database;
using FhirAugury.Source.Zulip.Indexing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Zulip.Controllers;

[ApiController]
[Route("api/v1")]
public class QueryController(ZulipDatabase db, IOptions<ZulipServiceOptions> optsAccessor) : ControllerBase
{
    [HttpPost("query")]
    public IActionResult FlexibleQuery([FromBody] ZulipQueryRequest request)
    {
        ZulipServiceOptions options = optsAccessor.Value;
        using SqliteConnection connection = db.OpenConnection();
        (string sql, List<SqliteParameter> parameters) = ZulipQueryBuilder.Build(request);

        using SqliteCommand cmd = new SqliteCommand(sql, connection);
        foreach (SqliteParameter p in parameters) cmd.Parameters.Add(p);

        List<object> results = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int msgId = reader["ZulipMessageId"] is long l ? (int)l : 0;
            string streamName = reader["StreamName"]?.ToString() ?? "";
            string topic = reader["Topic"]?.ToString() ?? "";
            string content = reader["ContentPlain"]?.ToString() ?? "";

            results.Add(new
            {
                id = msgId,
                streamName,
                topic,
                senderName = reader["SenderName"]?.ToString() ?? "",
                snippet = content.Length > 200 ? content[..200] : content,
                timestamp = reader["Timestamp"]?.ToString(),
                url = ZulipUrlHelper.BuildMessageUrl(options, streamName, topic, msgId),
            });
        }

        return Ok(new { total = results.Count, results });
    }
}