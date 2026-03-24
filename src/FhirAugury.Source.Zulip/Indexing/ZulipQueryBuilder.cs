using FhirAugury.Common.Text;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Zulip.Indexing;

/// <summary>
/// Builds parameterized SQL queries from ZulipQueryRequest fields.
/// All fields are optional; combined with AND. Repeated values within a field use IN (OR).
/// </summary>
public static class ZulipQueryBuilder
{
    private static readonly HashSet<string> AllowedSortColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        "timestamp", "stream_name", "topic", "sender_name", "sender_id", "zulip_message_id",
    };

    /// <summary>
    /// Builds a SELECT query with parameterized WHERE clause from the given query request.
    /// Returns the SQL string and a list of parameters to bind.
    /// </summary>
    public static (string Sql, List<SqliteParameter> Parameters) Build(Fhiraugury.ZulipQueryRequest request)
    {
        StringBuilder sb = new StringBuilder("SELECT * FROM zulip_messages WHERE 1=1");
        List<SqliteParameter> parameters = new List<SqliteParameter>();
        int paramIdx = 0;

        // Stream name filter
        AddInClause(sb, parameters, "StreamName", request.StreamNames, ref paramIdx);

        // Stream ID filter
        if (request.StreamIds.Count > 0)
        {
            List<string> names = new List<string>();
            foreach (int id in request.StreamIds)
            {
                string name = $"@p{paramIdx++}";
                names.Add(name);
                parameters.Add(new SqliteParameter(name, id));
            }
            sb.Append($" AND StreamId IN (SELECT Id FROM zulip_streams WHERE ZulipStreamId IN ({string.Join(", ", names)}))");
        }

        // Topic exact match
        if (!string.IsNullOrEmpty(request.Topic))
        {
            string name = $"@p{paramIdx++}";
            sb.Append($" AND Topic = {name}");
            parameters.Add(new SqliteParameter(name, request.Topic));
        }

        // Topic keyword (LIKE)
        if (!string.IsNullOrEmpty(request.TopicKeyword))
        {
            string name = $"@p{paramIdx++}";
            sb.Append($" AND Topic LIKE {name}");
            parameters.Add(new SqliteParameter(name, $"%{request.TopicKeyword}%"));
        }

        // Sender name filter
        AddInClause(sb, parameters, "SenderName", request.SenderNames, ref paramIdx);

        // Sender ID filter
        if (request.SenderIds.Count > 0)
        {
            List<string> names = new List<string>();
            foreach (int id in request.SenderIds)
            {
                string name = $"@p{paramIdx++}";
                names.Add(name);
                parameters.Add(new SqliteParameter(name, id));
            }
            sb.Append($" AND SenderId IN ({string.Join(", ", names)})");
        }

        // Timestamp range
        if (request.After is not null)
        {
            string name = $"@p{paramIdx++}";
            sb.Append($" AND Timestamp >= {name}");
            parameters.Add(new SqliteParameter(name, request.After.ToDateTimeOffset().ToString("o")));
        }

        if (request.Before is not null)
        {
            string name = $"@p{paramIdx++}";
            sb.Append($" AND Timestamp <= {name}");
            parameters.Add(new SqliteParameter(name, request.Before.ToDateTimeOffset().ToString("o")));
        }

        // FTS5 subquery
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            string name = $"@q{paramIdx++}";
            sb.Append($" AND Id IN (SELECT rowid FROM zulip_messages_fts WHERE zulip_messages_fts MATCH {name})");
            parameters.Add(new SqliteParameter(name, FtsQueryHelper.SanitizeFtsQuery(request.Query)));
        }

        // Sorting
        string sortBy = AllowedSortColumns.Contains(request.SortBy) ? ToColumnName(request.SortBy) : "Timestamp";
        string sortOrder = request.SortOrder?.Equals("asc", StringComparison.OrdinalIgnoreCase) == true ? "ASC" : "DESC";
        sb.Append($" ORDER BY {sortBy} {sortOrder}");

        // Pagination
        int limit = request.Limit > 0 ? Math.Min(request.Limit, 1000) : 50;
        sb.Append(" LIMIT @limit OFFSET @offset");
        parameters.Add(new SqliteParameter("@limit", limit));
        parameters.Add(new SqliteParameter("@offset", Math.Max(0, request.Offset)));

        return (sb.ToString(), parameters);
    }

    private static void AddInClause(
        StringBuilder sb, List<SqliteParameter> parameters,
        string column, Google.Protobuf.Collections.RepeatedField<string> values, ref int paramIdx)
    {
        if (values.Count == 0) return;

        List<string> names = new List<string>();
        foreach (string v in values)
        {
            string name = $"@p{paramIdx++}";
            names.Add(name);
            parameters.Add(new SqliteParameter(name, v));
        }

        sb.Append($" AND {column} IN ({string.Join(", ", names)})");
    }

    private static string ToColumnName(string sortBy) =>
        sortBy.ToLowerInvariant() switch
        {
            "timestamp" => "Timestamp",
            "stream_name" => "StreamName",
            "topic" => "Topic",
            "sender_name" => "SenderName",
            "sender_id" => "SenderId",
            "zulip_message_id" => "ZulipMessageId",
            _ => "Timestamp",
        };
}
