using System.Globalization;
using FhirAugury.Server.Terminology.Configuration;
using FhirAugury.Server.Terminology.Database;
using FhirAugury.Server.Terminology.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FhirAugury.Server.Terminology.Matching;

/// <summary>
/// FTS5- and Jaccard-driven scorer against the Phase 2 index. Computes
/// four sub-scores per candidate (<c>metadata_bm25</c>,
/// <c>content_bm25</c>, <c>code_jaccard</c>, <c>display_jaccard</c>)
/// and combines them into a normalized composite using
/// <see cref="LexicalWeightsOptions"/>.
/// </summary>
public sealed class LexicalMatcher : ITerminologyMatcher
{
    private const int DefaultSampleConcepts = 5;

    private readonly TerminologyDatabase _db;
    private readonly TerminologyServiceOptions _options;
    private readonly ILogger<LexicalMatcher> _logger;

    public string Mode => "lexical";

    public LexicalMatcher(
        TerminologyDatabase db,
        IOptions<TerminologyServiceOptions> options,
        ILogger<LexicalMatcher> logger)
    {
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public Task<IReadOnlyList<OverlapCandidate>> MatchAsync(
        NormalizedSubmission submission,
        OverlapCheckRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(request);

        int limit = request.Limit ?? _options.Defaults.Limit;
        double minScore = request.MinScore ?? _options.Defaults.MinScore;

        using SqliteConnection conn = _db.OpenConnection();

        Dictionary<int, ArtifactScore> scoreByArtifact = [];

        // ── Metadata signal ──────────────────────────────────────
        string metaQuery = BuildMetadataQuery(submission);
        if (!string.IsNullOrWhiteSpace(metaQuery))
        {
            QueryArtifactsFts(conn, metaQuery, scoreByArtifact, isMetadata: true, ct);
        }

        // ── Content signal ───────────────────────────────────────
        string contentQuery = BuildContentQuery(submission);
        if (!string.IsNullOrWhiteSpace(contentQuery))
        {
            QueryConceptsFts(conn, contentQuery, scoreByArtifact, ct);
        }

        if (scoreByArtifact.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<OverlapCandidate>>([]);
        }

        // ── Per-query normalization of BM25 sub-scores ───────────
        double maxMeta = scoreByArtifact.Values.Max(v => v.MetadataRaw);
        double maxContent = scoreByArtifact.Values.Max(v => v.ContentRaw);

        // ── Pull artifact rows for the candidate set ─────────────
        int[] candidateIds = scoreByArtifact.Keys.ToArray();
        Dictionary<int, ArtifactRow> rows = LoadArtifacts(conn, candidateIds);

        // ── Per-candidate Jaccard + composite score ──────────────
        HashSet<string> submissionCodes = new(
            submission.Concepts.Select(c => $"{c.SystemUrl}|{c.Code}"),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> submissionDisplayTokens =
            TokenizeDisplays(submission.Concepts.Select(c => c.DisplayNormalized ?? string.Empty));

        Dictionary<int, ConceptStats> conceptStatsById = LoadConceptStats(conn, candidateIds, submissionCodes);

        double weightMeta = _options.LexicalWeights.Url + _options.LexicalWeights.Title
                          + _options.LexicalWeights.Name + _options.LexicalWeights.Description;
        double weightContent = _options.LexicalWeights.Concepts;
        double weightSum = weightMeta + weightContent;
        if (weightSum <= 0)
        {
            weightSum = 1.0; // avoid divide-by-zero on a pathological config
        }

        List<OverlapCandidate> output = new(candidateIds.Length);
        foreach ((int artifactId, ArtifactScore raw) in scoreByArtifact)
        {
            if (!rows.TryGetValue(artifactId, out ArtifactRow? row)) continue;

            double metaNorm = maxMeta > 0 ? raw.MetadataRaw / maxMeta : 0.0;
            double contentNorm = maxContent > 0 ? raw.ContentRaw / maxContent : 0.0;

            ConceptStats stats = conceptStatsById.TryGetValue(artifactId, out ConceptStats? cs)
                ? cs
                : new ConceptStats();

            double codeJaccard = Jaccard(submissionCodes.Count, stats.CandidateCodeCount, stats.SharedCodeCount);
            double displayJaccard = Jaccard(
                submissionDisplayTokens.Count,
                stats.DisplayTokenCount,
                CountIntersection(submissionDisplayTokens, stats.DisplayTokens));

            // Blend content_bm25 + jaccards into the "content" half.
            double contentBlend = (contentNorm + codeJaccard + displayJaccard) / 3.0;

            double composite = (weightMeta * metaNorm + weightContent * contentBlend) / weightSum;
            if (composite < minScore) continue;

            string matchCategory = (raw.MetadataRaw > 0, raw.ContentRaw > 0 || stats.SharedCodeCount > 0) switch
            {
                (true, true) => "both",
                (true, false) => "metadata",
                (false, true) => "content",
                _ => "metadata",
            };

            CodeDisplay[] sample = stats.SampleConcepts
                .Take(DefaultSampleConcepts)
                .ToArray();

            output.Add(new OverlapCandidate
            {
                CanonicalUrl = row.CanonicalUrl,
                Version = row.Version,
                Title = row.Title,
                Kind = row.Kind,
                FhirVersion = row.FhirVersion,
                MatchCategory = matchCategory,
                Score = Math.Round(composite, 4),
                SubScores = new Dictionary<string, double>
                {
                    ["metadata_bm25"] = Math.Round(metaNorm, 4),
                    ["content_bm25"] = Math.Round(contentNorm, 4),
                    ["code_jaccard"] = Math.Round(codeJaccard, 4),
                    ["display_jaccard"] = Math.Round(displayJaccard, 4),
                },
                Reasons = BuildReasons(metaNorm, contentNorm, codeJaccard, displayJaccard, stats),
                SampleConcepts = sample,
                CrossVersion = !string.Equals(row.FhirVersion, submission.FhirVersion, StringComparison.OrdinalIgnoreCase),
            });
        }

        IReadOnlyList<OverlapCandidate> ranked = output
            .OrderByDescending(c => c.Score)
            .Take(limit)
            .ToList();

        return Task.FromResult(ranked);
    }

    // ── Sub-score helpers ────────────────────────────────────────────

    private static string BuildMetadataQuery(NormalizedSubmission s)
    {
        IEnumerable<string?> parts = [s.CanonicalUrl, s.Title, s.Name, s.Purpose, s.Description];
        return BuildFtsQuery(parts);
    }

    private static string BuildContentQuery(NormalizedSubmission s)
    {
        // Concatenate codes + displays; tokenize lightly.
        IEnumerable<string> tokens = s.Concepts
            .SelectMany(c => new[] { c.Code, c.Display ?? string.Empty })
            .Where(t => !string.IsNullOrWhiteSpace(t));
        return BuildFtsQuery(tokens);
    }

    private static string BuildFtsQuery(IEnumerable<string?> parts)
    {
        HashSet<string> tokens = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? p in parts)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            foreach (string raw in p.Split(
                [' ', '\t', '\r', '\n', ',', '.', ';', ':', '/', '\\', '|', '(', ')', '[', ']', '{', '}', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries))
            {
                string t = raw.Trim().ToLowerInvariant();
                if (t.Length < 3) continue;
                if (IsFtsStopword(t)) continue;
                tokens.Add(t);
            }
        }
        // FTS5 OR-of-terms; each term quoted to neutralize syntax chars.
        return string.Join(" OR ", tokens.Take(64).Select(t => $"\"{t.Replace("\"", "\"\"")}\""));
    }

    private static bool IsFtsStopword(string t) =>
        t is "the" or "and" or "for" or "with" or "are" or "this" or "that" or "from"
            or "http" or "https" or "www" or "org" or "com" or "net" or "html"
            or "uri" or "url";

    private static void QueryArtifactsFts(
        SqliteConnection conn, string ftsExpr,
        Dictionary<int, ArtifactScore> sink,
        bool isMetadata,
        CancellationToken ct)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT rowid, bm25(terminology_artifacts_fts) AS rank
            FROM terminology_artifacts_fts
            WHERE terminology_artifacts_fts MATCH $q
            ORDER BY rank LIMIT 200;
            """;
        cmd.Parameters.AddWithValue("$q", ftsExpr);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            int rowid = r.GetInt32(0);
            double bm25 = r.GetDouble(1);
            // FTS5 bm25 returns 0 (no match) or a negative number (lower = better).
            double inv = bm25 == 0 ? 0 : Math.Max(0, -bm25);

            if (!sink.TryGetValue(rowid, out ArtifactScore? slot))
            {
                slot = new ArtifactScore();
                sink[rowid] = slot;
            }

            if (isMetadata) slot.MetadataRaw = inv;
        }
    }

    private static void QueryConceptsFts(
        SqliteConnection conn, string ftsExpr,
        Dictionary<int, ArtifactScore> sink,
        CancellationToken ct)
    {
        // Step 1: pull rowid + bm25 from FTS only (auxiliary funcs require
        // bm25 to be in the same SELECT as the FTS MATCH; joins disable it).
        List<(int Rowid, double Rank)> hits = new(256);
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT rowid, bm25(terminology_concepts_fts) AS rank
                FROM terminology_concepts_fts
                WHERE terminology_concepts_fts MATCH $q
                ORDER BY rank LIMIT 1000;
                """;
            cmd.Parameters.AddWithValue("$q", ftsExpr);
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                hits.Add((r.GetInt32(0), r.GetDouble(1)));
            }
        }
        if (hits.Count == 0) return;

        // Step 2: aggregate -bm25 per ArtifactId via a join on the
        // (de-duplicated) rowid set.
        string placeholders = string.Join(",", hits.Select((_, i) => $"$r{i}"));
        Dictionary<int, double> rankByRowid = hits
            .GroupBy(h => h.Rowid)
            .ToDictionary(g => g.Key, g => g.Min(x => x.Rank));

        using SqliteCommand lookup = conn.CreateCommand();
        lookup.CommandText = $"""
            SELECT Id, ArtifactId
            FROM terminology_concepts
            WHERE Id IN ({placeholders});
            """;
        int idx = 0;
        foreach (int rowid in rankByRowid.Keys)
        {
            lookup.Parameters.AddWithValue($"$r{idx}", rowid);
            idx++;
        }

        Dictionary<int, double> aggByArtifact = [];
        using (SqliteDataReader r = lookup.ExecuteReader())
        {
            while (r.Read())
            {
                ct.ThrowIfCancellationRequested();
                int conceptId = r.GetInt32(0);
                int artifactId = r.GetInt32(1);
                if (!rankByRowid.TryGetValue(conceptId, out double rank)) continue;
                double inv = Math.Max(0, -rank);
                aggByArtifact[artifactId] = aggByArtifact.TryGetValue(artifactId, out double acc)
                    ? acc + inv
                    : inv;
            }
        }

        foreach ((int artifactId, double agg) in aggByArtifact)
        {
            if (!sink.TryGetValue(artifactId, out ArtifactScore? slot))
            {
                slot = new ArtifactScore();
                sink[artifactId] = slot;
            }
            slot.ContentRaw = agg;
        }
    }

    private static Dictionary<int, ArtifactRow> LoadArtifacts(SqliteConnection conn, int[] ids)
    {
        if (ids.Length == 0) return [];
        string placeholders = string.Join(",", ids.Select((_, i) => $"$id{i}"));
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT Id, Kind, CanonicalUrl, Version, FhirVersion, Title
            FROM terminology_artifacts
            WHERE Id IN ({placeholders});
            """;
        for (int i = 0; i < ids.Length; i++)
        {
            cmd.Parameters.AddWithValue($"$id{i}", ids[i]);
        }
        Dictionary<int, ArtifactRow> result = [];
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            result[r.GetInt32(0)] = new ArtifactRow
            {
                Kind = r.GetString(1),
                CanonicalUrl = r.GetString(2),
                Version = r.IsDBNull(3) ? null : r.GetString(3),
                FhirVersion = r.GetString(4),
                Title = r.IsDBNull(5) ? null : r.GetString(5),
            };
        }
        return result;
    }

    private static Dictionary<int, ConceptStats> LoadConceptStats(
        SqliteConnection conn, int[] artifactIds, HashSet<string> submissionCodes)
    {
        Dictionary<int, ConceptStats> map = [];
        if (artifactIds.Length == 0) return map;

        string placeholders = string.Join(",", artifactIds.Select((_, i) => $"$id{i}"));
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT ArtifactId, SystemUrl, Code, Display, DisplayNormalized
            FROM terminology_concepts
            WHERE ArtifactId IN ({placeholders});
            """;
        for (int i = 0; i < artifactIds.Length; i++)
        {
            cmd.Parameters.AddWithValue($"$id{i}", artifactIds[i]);
        }

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            int aid = r.GetInt32(0);
            string sys = r.GetString(1);
            string code = r.GetString(2);
            string? display = r.IsDBNull(3) ? null : r.GetString(3);
            string? displayNorm = r.IsDBNull(4) ? null : r.GetString(4);

            if (!map.TryGetValue(aid, out ConceptStats? stats))
            {
                stats = new ConceptStats();
                map[aid] = stats;
            }

            stats.CandidateCodeCount++;
            string key = $"{sys}|{code}";
            if (submissionCodes.Contains(key))
            {
                stats.SharedCodeCount++;
                if (stats.SampleConcepts.Count < 16)
                {
                    stats.SampleConcepts.Add(new CodeDisplay(code, display, sys));
                }
            }

            if (!string.IsNullOrEmpty(displayNorm))
            {
                foreach (string tok in TokenizeDisplay(displayNorm))
                {
                    stats.DisplayTokens.Add(tok);
                }
            }
        }

        foreach (ConceptStats s in map.Values)
        {
            s.DisplayTokenCount = s.DisplayTokens.Count;
        }

        return map;
    }

    private static HashSet<string> TokenizeDisplays(IEnumerable<string> displays)
    {
        HashSet<string> set = new(StringComparer.OrdinalIgnoreCase);
        foreach (string d in displays)
        {
            foreach (string t in TokenizeDisplay(d))
            {
                set.Add(t);
            }
        }
        return set;
    }

    private static IEnumerable<string> TokenizeDisplay(string display)
    {
        if (string.IsNullOrWhiteSpace(display)) yield break;
        foreach (string raw in display.Split(
            [' ', '\t', '\r', '\n', ',', '.', ';', ':', '/', '\\', '|', '(', ')', '[', ']', '{', '}', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries))
        {
            string t = raw.Trim().ToLowerInvariant();
            if (t.Length < 3) continue;
            if (IsFtsStopword(t)) continue;
            yield return t;
        }
    }

    private static int CountIntersection(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        HashSet<string> smaller = a.Count <= b.Count ? a : b;
        HashSet<string> larger = ReferenceEquals(smaller, a) ? b : a;
        int n = 0;
        foreach (string s in smaller)
        {
            if (larger.Contains(s)) n++;
        }
        return n;
    }

    private static double Jaccard(int aCount, int bCount, int shared)
    {
        if (aCount == 0 && bCount == 0) return 0.0;
        int union = aCount + bCount - shared;
        return union == 0 ? 0.0 : (double)shared / union;
    }

    private static string[] BuildReasons(
        double metaNorm, double contentNorm,
        double codeJaccard, double displayJaccard,
        ConceptStats stats)
    {
        List<string> r = [];
        if (metaNorm >= 0.6) r.Add("strong title/url/description overlap");
        else if (metaNorm >= 0.3) r.Add("partial metadata overlap");

        if (stats.SharedCodeCount > 0)
        {
            r.Add(string.Format(CultureInfo.InvariantCulture,
                "{0} concept{1} share (system, code)",
                stats.SharedCodeCount, stats.SharedCodeCount == 1 ? "" : "s"));
        }

        if (codeJaccard >= 0.5) r.Add("majority of submitted codes overlap");
        else if (codeJaccard >= 0.2) r.Add("notable code overlap");

        if (displayJaccard >= 0.5) r.Add("majority of display tokens overlap");

        if (contentNorm >= 0.5) r.Add("strong text overlap on concept displays");

        return r.ToArray();
    }

    // ── Internal types ────────────────────────────────────────────

    private sealed class ArtifactScore
    {
        public double MetadataRaw { get; set; }
        public double ContentRaw { get; set; }
    }

    private sealed record ArtifactRow
    {
        public required string Kind { get; init; }
        public required string CanonicalUrl { get; init; }
        public required string FhirVersion { get; init; }
        public string? Version { get; init; }
        public string? Title { get; init; }
    }

    private sealed class ConceptStats
    {
        public int CandidateCodeCount { get; set; }
        public int SharedCodeCount { get; set; }
        public HashSet<string> DisplayTokens { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int DisplayTokenCount { get; set; }
        public List<CodeDisplay> SampleConcepts { get; } = [];
    }
}
