using FhirAugury.Common.Api;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.GitHub.Controllers;

/// <summary>Endpoints for querying resolved HL7 work-group attribution.</summary>
[ApiController]
[Route("api/v1/workgroups")]
public class WorkGroupsController(GitHubDatabase db) : ControllerBase
{
    private const int DefaultLimit = 100;
    private const int MaxLimit = 1000;

    /// <summary>List canonical HL7 work-groups with per-repo file/artifact counts.</summary>
    [HttpGet]
    public IActionResult ListWorkGroups()
    {
        using SqliteConnection conn = db.OpenConnection();

        Dictionary<string, (string Name, bool Retired)> wgMeta = new(StringComparer.OrdinalIgnoreCase);
        foreach (Hl7WorkGroupRecord wg in Hl7WorkGroupRecord.SelectList(conn))
        {
            wgMeta[wg.Code] = (wg.Name, wg.Retired);
        }

        Dictionary<string, Dictionary<string, (int Files, int Artifacts)>> counts =
            new(StringComparer.OrdinalIgnoreCase);

        AggregateCounts(conn, "github_spec_file_map", isFiles: true, counts);
        AggregateCounts(conn, "github_canonical_artifacts", isFiles: false, counts);
        AggregateCounts(conn, "github_structure_definitions", isFiles: false, counts);

        List<WorkGroupSummary> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string code, (string name, bool retired)) in wgMeta)
        {
            seen.Add(code);
            counts.TryGetValue(code, out Dictionary<string, (int Files, int Artifacts)>? perRepo);
            result.Add(BuildSummary(code, name, retired, perRepo));
        }
        // Surface attributed codes that are no longer in hl7_workgroups (retired
        // mid-flight, or attributed in error) so callers can see the residue.
        foreach ((string code, Dictionary<string, (int Files, int Artifacts)> perRepo) in counts)
        {
            if (seen.Contains(code)) continue;
            result.Add(BuildSummary(code, code, retired: false, perRepo));
        }

        result.Sort(static (a, b) => string.CompareOrdinal(a.Code, b.Code));
        return Ok(new WorkGroupListResponse(result));
    }

    /// <summary>List spec_file_map rows attributed to the given work-group.</summary>
    [HttpGet("files")]
    public IActionResult ListFiles(
        [FromQuery] string repo,
        [FromQuery] string workgroup,
        [FromQuery] int? limit = null,
        [FromQuery] int? offset = null)
    {
        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(workgroup))
            return BadRequest("repo and workgroup are required");

        (int normLimit, int normOffset) = NormalizePaging(limit, offset);

        using SqliteConnection conn = db.OpenConnection();
        int total;
        using (SqliteCommand cnt = conn.CreateCommand())
        {
            cnt.CommandText = "SELECT COUNT(*) FROM github_spec_file_map WHERE RepoFullName = @r AND WorkGroup = @w";
            cnt.Parameters.AddWithValue("@r", repo);
            cnt.Parameters.AddWithValue("@w", workgroup);
            total = Convert.ToInt32(cnt.ExecuteScalar());
        }

        List<WorkGroupFileItem> items = [];
        using (SqliteCommand sel = conn.CreateCommand())
        {
            sel.CommandText = """
                SELECT RepoFullName, ArtifactKey, FilePath, MapType, WorkGroup, WorkGroupRaw
                FROM github_spec_file_map
                WHERE RepoFullName = @r AND WorkGroup = @w
                ORDER BY FilePath
                LIMIT @lim OFFSET @off
                """;
            sel.Parameters.AddWithValue("@r", repo);
            sel.Parameters.AddWithValue("@w", workgroup);
            sel.Parameters.AddWithValue("@lim", normLimit);
            sel.Parameters.AddWithValue("@off", normOffset);
            using SqliteDataReader r = sel.ExecuteReader();
            while (r.Read())
            {
                items.Add(new WorkGroupFileItem(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5)));
            }
        }

        return Ok(new WorkGroupFileListResponse(items, new PageMetadata(total, normLimit, normOffset)));
    }

    /// <summary>List artifacts attributed to the given work-group.</summary>
    [HttpGet("artifacts")]
    public IActionResult ListArtifacts(
        [FromQuery] string repo,
        [FromQuery] string workgroup,
        [FromQuery] int? limit = null,
        [FromQuery] int? offset = null)
    {
        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(workgroup))
            return BadRequest("repo and workgroup are required");

        (int normLimit, int normOffset) = NormalizePaging(limit, offset);

        using SqliteConnection conn = db.OpenConnection();
        int total;
        using (SqliteCommand cnt = conn.CreateCommand())
        {
            cnt.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM github_canonical_artifacts WHERE RepoFullName = @r AND WorkGroup = @w)
                  + (SELECT COUNT(*) FROM github_structure_definitions WHERE RepoFullName = @r AND WorkGroup = @w)
                """;
            cnt.Parameters.AddWithValue("@r", repo);
            cnt.Parameters.AddWithValue("@w", workgroup);
            total = Convert.ToInt32(cnt.ExecuteScalar());
        }

        List<WorkGroupArtifactItem> items = [];
        using (SqliteCommand sel = conn.CreateCommand())
        {
            sel.CommandText = """
                SELECT 'canonical' AS Source, RepoFullName, Name, ResourceType, FilePath, Url, WorkGroup, WorkGroupRaw
                FROM github_canonical_artifacts
                WHERE RepoFullName = @r AND WorkGroup = @w
                UNION ALL
                SELECT 'structure-definition' AS Source, RepoFullName, Name, NULL AS ResourceType, FilePath, Url, WorkGroup, WorkGroupRaw
                FROM github_structure_definitions
                WHERE RepoFullName = @r AND WorkGroup = @w
                ORDER BY Name
                LIMIT @lim OFFSET @off
                """;
            sel.Parameters.AddWithValue("@r", repo);
            sel.Parameters.AddWithValue("@w", workgroup);
            sel.Parameters.AddWithValue("@lim", normLimit);
            sel.Parameters.AddWithValue("@off", normOffset);
            using SqliteDataReader r = sel.ExecuteReader();
            while (r.Read())
            {
                items.Add(new WorkGroupArtifactItem(
                    r.GetString(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.GetString(4), r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.IsDBNull(7) ? null : r.GetString(7)));
            }
        }

        return Ok(new WorkGroupArtifactListResponse(items, new PageMetadata(total, normLimit, normOffset)));
    }

    /// <summary>Resolve a (repo, path) pair to its best-matching work-group.</summary>
    [HttpGet("resolve")]
    public IActionResult Resolve([FromQuery] string repo, [FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(path))
            return BadRequest("repo and path are required");

        using SqliteConnection conn = db.OpenConnection();
        string norm = path.Replace('\\', '/').Trim();

        // Stage 1: exact file match in spec_file_map (any MapType).
        (string? wg1, string? raw1) = LookupSpecFileExact(conn, repo, norm);
        if (wg1 is not null || raw1 is not null)
            return Ok(new WorkGroupResolveResponse(repo, path, wg1, raw1, "exact-file"));

        // Stage 2: longest prefix match against MapType='directory' rows.
        (string? wg2, string? raw2, string? prefix) = LookupSpecFileDirectoryPrefix(conn, repo, norm);
        if (prefix is not null)
            return Ok(new WorkGroupResolveResponse(repo, path, wg2, raw2, "directory-prefix"));

        // Stage 3: exact file match against artifact tables.
        (string? wg3, string? raw3) = LookupArtifactByPath(conn, repo, norm);
        if (wg3 is not null || raw3 is not null)
            return Ok(new WorkGroupResolveResponse(repo, path, wg3, raw3, "artifact"));

        // Stage 4: repo default.
        (string? wg4, string? raw4) = LookupRepoDefault(conn, repo);
        if (wg4 is not null || raw4 is not null)
            return Ok(new WorkGroupResolveResponse(repo, path, wg4, raw4, "repo-default"));

        return Ok(new WorkGroupResolveResponse(repo, path, null, null, "none"));
    }

    /// <summary>List distinct unresolved <c>WorkGroupRaw</c> values needing review.</summary>
    [HttpGet("unresolved")]
    public IActionResult Unresolved(
        [FromQuery] string? repo = null,
        [FromQuery] int? limit = null,
        [FromQuery] int? offset = null)
    {
        (int normLimit, int normOffset) = NormalizePaging(limit, offset);

        using SqliteConnection conn = db.OpenConnection();
        // Aggregate distinct WorkGroupRaw values across all four tables.
        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<WorkGroupUnresolvedExample>> examples = new(StringComparer.OrdinalIgnoreCase);

        AggregateUnresolved(conn, "github_canonical_artifacts", "canonical_artifacts", "FilePath", repo, counts, examples);
        AggregateUnresolved(conn, "github_structure_definitions", "structure_definitions", "FilePath", repo, counts, examples);
        AggregateUnresolved(conn, "github_spec_file_map", "spec_file_map", "FilePath", repo, counts, examples);
        AggregateUnresolved(conn, "github_repo_workgroups", "repo_workgroups", "RepoFullName", repo, counts, examples);

        List<WorkGroupUnresolvedItem> all = counts
            .Select(kv => new WorkGroupUnresolvedItem(kv.Key, kv.Value, examples[kv.Key]))
            .OrderByDescending(i => i.OccurrenceCount)
            .ThenBy(i => i.WorkGroupRaw, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int total = all.Count;
        List<WorkGroupUnresolvedItem> page = all.Skip(normOffset).Take(normLimit).ToList();
        return Ok(new WorkGroupUnresolvedListResponse(page, new PageMetadata(total, normLimit, normOffset)));
    }

    // ── helpers ────────────────────────────────────────────────

    private static (int Limit, int Offset) NormalizePaging(int? limit, int? offset)
    {
        int l = limit is null or <= 0 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);
        int o = offset is null or < 0 ? 0 : offset.Value;
        return (l, o);
    }

    private static WorkGroupSummary BuildSummary(string code, string name, bool retired,
        Dictionary<string, (int Files, int Artifacts)>? perRepo)
    {
        List<WorkGroupRepoCount> repos = [];
        int totalFiles = 0;
        int totalArtifacts = 0;
        if (perRepo is not null)
        {
            foreach ((string repoName, (int files, int artifacts)) in perRepo
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                repos.Add(new WorkGroupRepoCount(repoName, files, artifacts));
                totalFiles += files;
                totalArtifacts += artifacts;
            }
        }
        return new WorkGroupSummary(code, name, retired, totalFiles, totalArtifacts, repos);
    }

    private static void AggregateCounts(SqliteConnection conn, string table, bool isFiles,
        Dictionary<string, Dictionary<string, (int Files, int Artifacts)>> counts)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT WorkGroup, RepoFullName, COUNT(*) FROM {table} WHERE WorkGroup IS NOT NULL GROUP BY WorkGroup, RepoFullName";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            string wg = r.GetString(0);
            string repo = r.GetString(1);
            int n = r.GetInt32(2);
            if (!counts.TryGetValue(wg, out Dictionary<string, (int Files, int Artifacts)>? perRepo))
            {
                perRepo = new Dictionary<string, (int Files, int Artifacts)>(StringComparer.OrdinalIgnoreCase);
                counts[wg] = perRepo;
            }
            perRepo.TryGetValue(repo, out (int Files, int Artifacts) cur);
            if (isFiles) cur.Files += n; else cur.Artifacts += n;
            perRepo[repo] = cur;
        }
    }

    private static (string? Wg, string? Raw) LookupSpecFileExact(SqliteConnection conn, string repo, string path)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT WorkGroup, WorkGroupRaw
            FROM github_spec_file_map
            WHERE RepoFullName = @r AND FilePath = @p
            ORDER BY (WorkGroup IS NULL), Id
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@r", repo);
        cmd.Parameters.AddWithValue("@p", path);
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null);
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private static (string? Wg, string? Raw, string? MatchedPrefix) LookupSpecFileDirectoryPrefix(SqliteConnection conn, string repo, string path)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT FilePath, WorkGroup, WorkGroupRaw
            FROM github_spec_file_map
            WHERE RepoFullName = @r AND MapType = 'directory'
              AND (@p = FilePath OR @p LIKE FilePath || '/%')
            ORDER BY length(FilePath) DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@r", repo);
        cmd.Parameters.AddWithValue("@p", path);
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null, null);
        return (
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.GetString(0));
    }

    private static (string? Wg, string? Raw) LookupArtifactByPath(SqliteConnection conn, string repo, string path)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT WorkGroup, WorkGroupRaw FROM github_canonical_artifacts
            WHERE RepoFullName = @r AND FilePath = @p
            UNION ALL
            SELECT WorkGroup, WorkGroupRaw FROM github_structure_definitions
            WHERE RepoFullName = @r AND FilePath = @p
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@r", repo);
        cmd.Parameters.AddWithValue("@p", path);
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null);
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private static (string? Wg, string? Raw) LookupRepoDefault(SqliteConnection conn, string repo)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT WorkGroup, WorkGroupRaw FROM github_repo_workgroups WHERE RepoFullName = @r";
        cmd.Parameters.AddWithValue("@r", repo);
        using SqliteDataReader r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null);
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private static void AggregateUnresolved(
        SqliteConnection conn, string table, string label, string keyColumn,
        string? repoFilter,
        Dictionary<string, int> counts,
        Dictionary<string, List<WorkGroupUnresolvedExample>> examples)
    {
        const int MaxExamplesPerValue = 5;
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT WorkGroupRaw, RepoFullName, {keyColumn} FROM {table} WHERE WorkGroup IS NULL AND WorkGroupRaw IS NOT NULL"
            + (repoFilter is null ? string.Empty : " AND RepoFullName = @r");
        if (repoFilter is not null) cmd.Parameters.AddWithValue("@r", repoFilter);
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            string raw = r.GetString(0);
            string repo = r.GetString(1);
            string key = r.GetString(2);
            counts.TryGetValue(raw, out int cur);
            counts[raw] = cur + 1;
            if (!examples.TryGetValue(raw, out List<WorkGroupUnresolvedExample>? list))
            {
                list = [];
                examples[raw] = list;
            }
            if (list.Count < MaxExamplesPerValue)
                list.Add(new WorkGroupUnresolvedExample(label, repo, key));
        }
    }
}
