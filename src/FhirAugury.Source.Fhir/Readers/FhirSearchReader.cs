using FhirAugury.Common.Text;
using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Database;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Fhir.Readers;

/// <summary>
/// Queries the FTS5 sidecar index. The query is two-step to avoid the SQLite
/// FTS5 limitation that <c>bm25()</c> / <c>rank</c> cannot be referenced from a
/// statement that JOINs the FTS table: (a) read <c>ArtifactId</c> (UNINDEXED) and
/// <c>rank</c> from the FTS table alone, then (b) resolve metadata from
/// <c>fhir_artifacts</c>, applying release / kind filters.
/// </summary>
public sealed class FhirSearchReader(FhirSearchDatabase searchDb)
{
    public FhirSearchResponse Search(
        string query, string release, IReadOnlyList<string>? kinds, int limit)
    {
        string sanitized = FtsQueryHelper.SanitizeFtsQuery(query);
        if (string.IsNullOrEmpty(sanitized))
        {
            return new FhirSearchResponse(query, 0, []);
        }

        using SqliteConnection conn = searchDb.OpenConnection();

        // Step (a): FTS candidates (ArtifactId + rank), no join. Over-fetch so the
        // post-filter on release/kind can still fill the requested limit.
        int fetch = Math.Min(Math.Max(limit, 1) * 20, 500);
        Dictionary<string, double> rankById = [];
        List<string> candidateOrder = [];
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT ArtifactId, rank FROM fhir_artifacts_fts
                WHERE fhir_artifacts_fts MATCH $q
                ORDER BY rank
                LIMIT $n
                """;
            cmd.Parameters.AddWithValue("$q", sanitized);
            cmd.Parameters.AddWithValue("$n", fetch);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string id = r.GetString(0);
                rankById[id] = r.GetDouble(1);
                candidateOrder.Add(id);
            }
        }

        if (candidateOrder.Count == 0)
        {
            return new FhirSearchResponse(query, 0, []);
        }

        // Step (b): resolve metadata + filter by release (and optionally kind).
        HashSet<string>? kindFilter = kinds is { Count: > 0 }
            ? new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase)
            : null;

        Dictionary<string, ArtifactSearchHit> hitsById = [];
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            List<string> placeholders = [];
            for (int i = 0; i < candidateOrder.Count; i++)
            {
                string p = $"$id{i}";
                placeholders.Add(p);
                cmd.Parameters.AddWithValue(p, candidateOrder[i]);
            }
            cmd.Parameters.AddWithValue("$rel", release);

            cmd.CommandText =
                "SELECT ArtifactId, Kind, Release, Name, Title, Url FROM fhir_artifacts " +
                $"WHERE Release = $rel AND ArtifactId IN ({string.Join(", ", placeholders)})";

            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string kind = r.GetString(1);
                if (kindFilter is not null && !kindFilter.Contains(kind))
                {
                    continue;
                }
                string id = r.GetString(0);
                hitsById[id] = new ArtifactSearchHit(
                    Kind: kind,
                    Release: r.GetString(2),
                    Name: r.GetString(3),
                    Title: r.IsDBNull(4) ? null : r.GetString(4),
                    Url: r.IsDBNull(5) ? null : r.GetString(5),
                    Score: -rankById[id]);
            }
        }

        List<ArtifactSearchHit> hits = candidateOrder
            .Where(hitsById.ContainsKey)
            .Select(id => hitsById[id])
            .Take(limit)
            .ToList();

        return new FhirSearchResponse(query, hits.Count, hits);
    }
}
