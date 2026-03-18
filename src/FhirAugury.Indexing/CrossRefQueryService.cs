using FhirAugury.Database.Records;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Indexing;

/// <summary>
/// Provides query methods for cross-reference links.
/// </summary>
public static class CrossRefQueryService
{
    /// <summary>
    /// Gets items that reference or are referenced by the given item.
    /// </summary>
    public static List<CrossRefLinkRecord> GetRelatedItems(
        SqliteConnection connection,
        string sourceType,
        string sourceId)
    {
        var results = new List<CrossRefLinkRecord>();

        // Items this item references (outgoing)
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Id, SourceType, SourceId, TargetType, TargetId, LinkType, Context
                FROM xref_links
                WHERE SourceType = @type AND SourceId = @id
                """;
            cmd.Parameters.AddWithValue("@type", sourceType);
            cmd.Parameters.AddWithValue("@id", sourceId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadRecord(reader));
            }
        }

        // Items that reference this item (incoming)
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Id, SourceType, SourceId, TargetType, TargetId, LinkType, Context
                FROM xref_links
                WHERE TargetType = @type AND TargetId = @id
                """;
            cmd.Parameters.AddWithValue("@type", sourceType);
            cmd.Parameters.AddWithValue("@id", sourceId);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(ReadRecord(reader));
            }
        }

        return results;
    }

    /// <summary>
    /// Gets the most-referenced items of a given target type.
    /// </summary>
    public static List<(string TargetType, string TargetId, int ReferenceCount)> GetMostReferenced(
        SqliteConnection connection,
        string? targetType = null,
        int limit = 20)
    {
        using var cmd = connection.CreateCommand();

        var sql = """
            SELECT TargetType, TargetId, COUNT(*) as RefCount
            FROM xref_links
            """;

        if (!string.IsNullOrEmpty(targetType))
        {
            sql += " WHERE TargetType = @type";
        }

        sql += " GROUP BY TargetType, TargetId ORDER BY RefCount DESC LIMIT @limit";

        cmd.CommandText = sql;
        if (!string.IsNullOrEmpty(targetType))
        {
            cmd.Parameters.AddWithValue("@type", targetType);
        }
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<(string, string, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return results;
    }

    /// <summary>
    /// Performs multi-hop graph traversal from a seed item.
    /// </summary>
    public static List<CrossRefLinkRecord> GetReferenceGraph(
        SqliteConnection connection,
        string sourceType,
        string sourceId,
        int depth = 2)
    {
        var visited = new HashSet<(string, string)> { (sourceType, sourceId) };
        var allLinks = new List<CrossRefLinkRecord>();
        var frontier = new List<(string Type, string Id)> { (sourceType, sourceId) };

        for (var hop = 0; hop < depth && frontier.Count > 0; hop++)
        {
            var nextFrontier = new List<(string Type, string Id)>();

            foreach (var (type, id) in frontier)
            {
                var links = GetRelatedItems(connection, type, id);
                foreach (var link in links)
                {
                    allLinks.Add(link);

                    // Add outgoing targets to frontier
                    var target = (link.TargetType, link.TargetId);
                    if (visited.Add(target))
                    {
                        nextFrontier.Add(target);
                    }

                    // Add incoming sources to frontier
                    var source = (link.SourceType, link.SourceId);
                    if (visited.Add(source))
                    {
                        nextFrontier.Add(source);
                    }
                }
            }

            frontier = nextFrontier;
        }

        return allLinks;
    }

    private static CrossRefLinkRecord ReadRecord(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        SourceType = reader.GetString(1),
        SourceId = reader.GetString(2),
        TargetType = reader.GetString(3),
        TargetId = reader.GetString(4),
        LinkType = reader.GetString(5),
        Context = reader.IsDBNull(6) ? null : reader.GetString(6),
    };
}
