using System.Data;
using System.Text.Json;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Single idempotent post-ingestion pass that normalizes existing
/// <c>WorkGroup</c> values to canonical HL7 codes, preserves unresolved
/// inputs in <c>WorkGroupRaw</c>, and fills missing values along the
/// source-of-truth chain (artifact → controlling artifact → JIRA-Spec key
/// → page/file map → repo default).
/// </summary>
public sealed class WorkGroupResolutionPass
{
    private readonly GitHubDatabase _database;
    private readonly WorkGroupResolver _resolver;
    private readonly RepoDefaultWorkGroupResolver _repoDefaultResolver;
    private readonly ILogger<WorkGroupResolutionPass> _logger;

    public WorkGroupResolutionPass(
        GitHubDatabase database,
        WorkGroupResolver resolver,
        RepoDefaultWorkGroupResolver repoDefaultResolver,
        ILogger<WorkGroupResolutionPass> logger)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(repoDefaultResolver);
        ArgumentNullException.ThrowIfNull(logger);
        _database = database;
        _resolver = resolver;
        _repoDefaultResolver = repoDefaultResolver;
        _logger = logger;
    }

    public Task RunAsync(IReadOnlyList<string> repoFullNames, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repoFullNames);
        foreach (string repo in repoFullNames)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                RunForRepo(repo, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Per-repo failure must not poison subsequent repos.
                _logger.LogWarning(ex, "workgroup resolution failed for {Repo}", repo);
            }
        }
        return Task.CompletedTask;
    }

    private void RunForRepo(string repoFullName, CancellationToken ct)
    {
        using SqliteConnection conn = _database.OpenConnection();
        using SqliteTransaction tx = conn.BeginTransaction(IsolationLevel.Serializable);

        ResolutionCounters counters = new();

        // Step 1: backfill JiraWorkgroupRecord.WorkGroupCode where null.
        BackfillJiraWorkgroupCodes(conn, tx, repoFullName);

        // Step 2: normalize artifact-level WGs on canonical_artifacts +
        // structure_definitions.
        NormalizeArtifactTable(conn, tx, repoFullName, "github_canonical_artifacts", counters);
        NormalizeArtifactTable(conn, tx, repoFullName, "github_structure_definitions", counters);

        // Step 3: SearchParameter inheritance from controlling SD via baseResources.
        PropagateSearchParameterFromBase(conn, tx, repoFullName, counters);

        // Step 4 + 5: spec_file_map: artifact-level lookup, then JIRA-Spec key indirection.
        FillSpecFileMap(conn, tx, repoFullName, counters);

        // Step 6: derive + upsert per-repo default.
        ct.ThrowIfCancellationRequested();
        RepoDefaultResult repoDefault = _repoDefaultResolver.Resolve(conn, repoFullName);
        UpsertRepoDefault(conn, tx, repoFullName, repoDefault);

        // Step 7: fall back to repo default for rows still null.
        if (!string.IsNullOrEmpty(repoDefault.Code))
        {
            counters.FromRepoDefault += FallBackToRepoDefault(conn, tx, repoFullName, "github_canonical_artifacts", repoDefault.Code);
            counters.FromRepoDefault += FallBackToRepoDefault(conn, tx, repoFullName, "github_structure_definitions", repoDefault.Code);
            counters.FromRepoDefault += FallBackToRepoDefault(conn, tx, repoFullName, "github_spec_file_map", repoDefault.Code);
        }

        // Step 8: count rows still unresolved-with-raw and stuck-null.
        counters.UnresolvedKept = CountUnresolvedKept(conn, tx, repoFullName);
        counters.StillUnresolved = CountStillUnresolved(conn, tx, repoFullName);

        tx.Commit();

        _logger.LogInformation(
            "workgroup resolution: repo={Repo} fromExtension={FromExt} fromControllingArtifact={FromCtrl} fromJiraSpecKey={FromJira} fromRepoDefault={FromRepo} unresolvedKept={Unkept} stillUnresolved={StillUnresolved}",
            repoFullName,
            counters.FromExtension,
            counters.FromControllingArtifact,
            counters.FromJiraSpecKey,
            counters.FromRepoDefault,
            counters.UnresolvedKept,
            counters.StillUnresolved);
    }

    private void BackfillJiraWorkgroupCodes(SqliteConnection conn, SqliteTransaction tx, string repo)
    {
        List<(int Id, string Name)> rows = [];
        using (SqliteCommand sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = "SELECT Id, Name FROM jira_workgroups WHERE RepoFullName = @repo AND WorkGroupCode IS NULL";
            sel.Parameters.AddWithValue("@repo", repo);
            using SqliteDataReader r = sel.ExecuteReader();
            while (r.Read())
            {
                rows.Add((r.GetInt32(0), r.GetString(1)));
            }
        }

        foreach ((int id, string name) in rows)
        {
            string? code = _resolver.Resolve(name);
            if (code is null) continue;

            using SqliteCommand upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE jira_workgroups SET WorkGroupCode = @c WHERE Id = @id AND WorkGroupCode IS NULL";
            upd.Parameters.AddWithValue("@c", code);
            upd.Parameters.AddWithValue("@id", id);
            upd.ExecuteNonQuery();
        }
    }

    private void NormalizeArtifactTable(SqliteConnection conn, SqliteTransaction tx, string repo, string table, ResolutionCounters counters)
    {
        List<(int Id, string? Wg, string? Raw)> rows = [];
        using (SqliteCommand sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = $"SELECT Id, WorkGroup, WorkGroupRaw FROM {table} WHERE RepoFullName = @repo AND WorkGroup IS NOT NULL";
            sel.Parameters.AddWithValue("@repo", repo);
            using SqliteDataReader r = sel.ExecuteReader();
            while (r.Read())
            {
                rows.Add((
                    r.GetInt32(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2)));
            }
        }

        foreach ((int id, string? original, string? existingRaw) in rows)
        {
            if (string.IsNullOrEmpty(original)) continue;

            string? canonical = _resolver.Resolve(original);
            string? newWg;
            string? newRaw;
            bool resolved;

            if (canonical is null)
            {
                // Unresolved: move to Raw, blank Wg.
                newWg = null;
                newRaw = original;
                resolved = false;
            }
            else if (string.Equals(canonical, original, StringComparison.OrdinalIgnoreCase))
            {
                // Already canonical: leave Raw as-is (it may carry the
                // original pre-resolution input from a prior pass).
                newWg = canonical;
                newRaw = existingRaw;
                resolved = true;
            }
            else
            {
                // Resolved-but-different: store code, preserve original in Raw.
                newWg = canonical;
                newRaw = original;
                resolved = true;
            }

            if (string.Equals(newWg, original, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(newRaw, existingRaw, StringComparison.OrdinalIgnoreCase))
            {
                if (resolved) counters.FromExtension++;
                continue;
            }

            using SqliteCommand upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = $"UPDATE {table} SET WorkGroup = @wg, WorkGroupRaw = @raw WHERE Id = @id";
            AddNullable(upd, "@wg", newWg);
            AddNullable(upd, "@raw", newRaw);
            upd.Parameters.AddWithValue("@id", id);
            upd.ExecuteNonQuery();

            if (resolved) counters.FromExtension++;
        }
    }

    private static int FallBackToRepoDefault(SqliteConnection conn, SqliteTransaction tx, string repo, string table, string code)
    {
        using SqliteCommand upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = $"UPDATE {table} SET WorkGroup = @wg WHERE RepoFullName = @repo AND WorkGroup IS NULL AND WorkGroupRaw IS NULL";
        upd.Parameters.AddWithValue("@wg", code);
        upd.Parameters.AddWithValue("@repo", repo);
        return upd.ExecuteNonQuery();
    }

    private void PropagateSearchParameterFromBase(SqliteConnection conn, SqliteTransaction tx, string repo, ResolutionCounters counters)
    {
        // SearchParameter rows whose WorkGroup is still null after step 2.
        // Pull TypeSpecificData JSON to extract baseResources.
        List<(int Id, string Json)> rows = [];
        using (SqliteCommand sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = """
                SELECT Id, TypeSpecificData
                FROM github_canonical_artifacts
                WHERE RepoFullName = @repo
                  AND ResourceType = 'SearchParameter'
                  AND WorkGroup IS NULL
                  AND WorkGroupRaw IS NULL
                  AND TypeSpecificData IS NOT NULL
                """;
            sel.Parameters.AddWithValue("@repo", repo);
            using SqliteDataReader r = sel.ExecuteReader();
            while (r.Read())
            {
                if (!r.IsDBNull(1)) rows.Add((r.GetInt32(0), r.GetString(1)));
            }
        }

        foreach ((int id, string json) in rows)
        {
            string? sourceWg = ResolveSearchParameterWg(conn, tx, repo, json);
            if (sourceWg is null) continue;

            using SqliteCommand upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE github_canonical_artifacts
                SET WorkGroup = @wg
                WHERE Id = @id AND WorkGroup IS NULL AND WorkGroupRaw IS NULL
                """;
            upd.Parameters.AddWithValue("@wg", sourceWg);
            upd.Parameters.AddWithValue("@id", id);
            if (upd.ExecuteNonQuery() > 0) counters.FromControllingArtifact++;
        }
    }

    private static string? ResolveSearchParameterWg(SqliteConnection conn, SqliteTransaction tx, string repo, string typeSpecificData)
    {
        List<string> bases = [];
        try
        {
            using JsonDocument doc = JsonDocument.Parse(typeSpecificData);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("baseResources", out JsonElement baseEl))
            {
                if (baseEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement b in baseEl.EnumerateArray())
                    {
                        if (b.ValueKind == JsonValueKind.String)
                        {
                            string? s = b.GetString();
                            if (!string.IsNullOrEmpty(s)) bases.Add(s);
                        }
                    }
                }
                else if (baseEl.ValueKind == JsonValueKind.String)
                {
                    string? s = baseEl.GetString();
                    if (!string.IsNullOrEmpty(s)) bases.Add(s);
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        foreach (string baseName in bases)
        {
            using SqliteCommand sel = conn.CreateCommand();
            sel.Transaction = tx;
            sel.CommandText = """
                SELECT WorkGroup
                FROM github_structure_definitions
                WHERE RepoFullName = @repo AND Name = @name AND WorkGroup IS NOT NULL
                LIMIT 1
                """;
            sel.Parameters.AddWithValue("@repo", repo);
            sel.Parameters.AddWithValue("@name", baseName);
            object? result = sel.ExecuteScalar();
            if (result is string s && !string.IsNullOrEmpty(s)) return s;
        }
        return null;
    }

    private void FillSpecFileMap(SqliteConnection conn, SqliteTransaction tx, string repo, ResolutionCounters counters)
    {
        // Key indirection via JiraSpecArtifactRecord (artifact map rows) and
        // JiraSpecPageRecord (page map rows). We always preserve the original
        // JIRA-Spec free-text in WorkGroupRaw, and fill WorkGroup with the
        // canonical code from jira_workgroups.WorkGroupCode.
        List<(int Id, string ArtifactKey, string MapType, string? Wg, string? Raw)> rows = [];
        using (SqliteCommand sel = conn.CreateCommand())
        {
            sel.Transaction = tx;
            sel.CommandText = """
                SELECT Id, ArtifactKey, MapType, WorkGroup, WorkGroupRaw
                FROM github_spec_file_map
                WHERE RepoFullName = @repo
                """;
            sel.Parameters.AddWithValue("@repo", repo);
            using SqliteDataReader r = sel.ExecuteReader();
            while (r.Read())
            {
                rows.Add((
                    r.GetInt32(0),
                    r.GetString(1),
                    r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4)));
            }
        }

        foreach ((int id, string artifactKey, string mapType, string? curWg, string? curRaw) in rows)
        {
            (string? newWg, string? newRaw, ProvenanceTag prov) = ResolveFileMapRow(conn, tx, repo, artifactKey, mapType);

            if (newWg is null && newRaw is null)
            {
                // Nothing to apply at this stage.
                continue;
            }

            // Idempotent: only update when the tuple actually changes.
            if (string.Equals(newWg, curWg, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(newRaw, curRaw, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using SqliteCommand upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE github_spec_file_map SET WorkGroup = @wg, WorkGroupRaw = @raw WHERE Id = @id";
            AddNullable(upd, "@wg", newWg);
            AddNullable(upd, "@raw", newRaw);
            upd.Parameters.AddWithValue("@id", id);
            upd.ExecuteNonQuery();

            switch (prov)
            {
                case ProvenanceTag.Artifact: counters.FromExtension++; break;
                case ProvenanceTag.JiraSpecKey: counters.FromJiraSpecKey++; break;
            }
        }
    }

    private (string? Wg, string? Raw, ProvenanceTag Prov) ResolveFileMapRow(
        SqliteConnection conn, SqliteTransaction tx, string repo, string artifactKey, string mapType)
    {
        // Preference: JIRA-Spec key indirection (it carries the canonical
        // JIRA-Spec free-text we must preserve in Raw). Falls back to the
        // already-resolved WG on the matching canonical artifact.
        string? jiraRaw;
        string? jiraCode;

        if (string.Equals(mapType, "page", StringComparison.OrdinalIgnoreCase))
        {
            (jiraRaw, jiraCode) = LookupJiraSpecPageWg(conn, tx, repo, artifactKey);
        }
        else
        {
            (jiraRaw, jiraCode) = LookupJiraSpecArtifactWg(conn, tx, repo, artifactKey);
        }

        if (jiraRaw is not null || jiraCode is not null)
        {
            return (jiraCode, jiraRaw, ProvenanceTag.JiraSpecKey);
        }

        // No JIRA-Spec match → try the artifact tables for a code already
        // resolved in step 2/3.
        string? artifactWg = LookupArtifactWg(conn, tx, repo, artifactKey);
        if (!string.IsNullOrEmpty(artifactWg))
        {
            return (artifactWg, null, ProvenanceTag.Artifact);
        }

        return (null, null, ProvenanceTag.None);
    }

    private static (string? Raw, string? Code) LookupJiraSpecArtifactWg(SqliteConnection conn, SqliteTransaction tx, string repo, string artifactKey)
    {
        using SqliteCommand sel = conn.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText = """
            SELECT a.Workgroup, jw.WorkGroupCode
            FROM jira_spec_artifacts a
            LEFT JOIN jira_workgroups jw
                ON jw.RepoFullName = a.RepoFullName AND jw.WorkgroupKey = a.Workgroup
            WHERE a.RepoFullName = @repo AND a.ArtifactKey = @key AND a.Workgroup IS NOT NULL
            LIMIT 1
            """;
        sel.Parameters.AddWithValue("@repo", repo);
        sel.Parameters.AddWithValue("@key", artifactKey);
        using SqliteDataReader r = sel.ExecuteReader();
        if (!r.Read()) return (null, null);
        string? raw = r.IsDBNull(0) ? null : r.GetString(0);
        string? code = r.IsDBNull(1) ? null : r.GetString(1);
        return (raw, code);
    }

    private static (string? Raw, string? Code) LookupJiraSpecPageWg(SqliteConnection conn, SqliteTransaction tx, string repo, string pageKey)
    {
        using SqliteCommand sel = conn.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText = """
            SELECT p.Workgroup, jw.WorkGroupCode
            FROM jira_spec_pages p
            LEFT JOIN jira_workgroups jw
                ON jw.RepoFullName = p.RepoFullName AND jw.WorkgroupKey = p.Workgroup
            WHERE p.RepoFullName = @repo AND p.PageKey = @key AND p.Workgroup IS NOT NULL
            LIMIT 1
            """;
        sel.Parameters.AddWithValue("@repo", repo);
        sel.Parameters.AddWithValue("@key", pageKey);
        using SqliteDataReader r = sel.ExecuteReader();
        if (!r.Read()) return (null, null);
        string? raw = r.IsDBNull(0) ? null : r.GetString(0);
        string? code = r.IsDBNull(1) ? null : r.GetString(1);
        return (raw, code);
    }

    private static string? LookupArtifactWg(SqliteConnection conn, SqliteTransaction tx, string repo, string artifactKey)
    {
        // ArtifactKey is the lowercase artifact identifier from JIRA-Spec; we
        // try matching against canonical artifact Name (case-insensitive) and
        // SD Name. WorkGroup must already be canonical (post step 2).
        using SqliteCommand sel = conn.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText = """
            SELECT WorkGroup FROM github_canonical_artifacts
            WHERE RepoFullName = @repo AND lower(Name) = lower(@key) AND WorkGroup IS NOT NULL
            LIMIT 1
            """;
        sel.Parameters.AddWithValue("@repo", repo);
        sel.Parameters.AddWithValue("@key", artifactKey);
        object? a = sel.ExecuteScalar();
        if (a is string aw && !string.IsNullOrEmpty(aw)) return aw;

        using SqliteCommand sel2 = conn.CreateCommand();
        sel2.Transaction = tx;
        sel2.CommandText = """
            SELECT WorkGroup FROM github_structure_definitions
            WHERE RepoFullName = @repo AND lower(Name) = lower(@key) AND WorkGroup IS NOT NULL
            LIMIT 1
            """;
        sel2.Parameters.AddWithValue("@repo", repo);
        sel2.Parameters.AddWithValue("@key", artifactKey);
        object? s = sel2.ExecuteScalar();
        return s is string sw && !string.IsNullOrEmpty(sw) ? sw : null;
    }

    private static void UpsertRepoDefault(SqliteConnection conn, SqliteTransaction tx, string repo, RepoDefaultResult result)
    {
        // Idempotent: only write when WG/Raw/Source actually differ.
        using SqliteCommand sel = conn.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText = "SELECT Id, WorkGroup, WorkGroupRaw, Source FROM github_repo_workgroups WHERE RepoFullName = @r";
        sel.Parameters.AddWithValue("@r", repo);
        using SqliteDataReader r = sel.ExecuteReader();
        if (r.Read())
        {
            int id = r.GetInt32(0);
            string? curWg = r.IsDBNull(1) ? null : r.GetString(1);
            string? curRaw = r.IsDBNull(2) ? null : r.GetString(2);
            string curSource = r.GetString(3);
            r.Close();

            bool same =
                string.Equals(curWg, result.Code, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(curRaw, result.Raw, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(curSource, result.Source, StringComparison.Ordinal);

            if (same) return;

            using SqliteCommand upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE github_repo_workgroups SET WorkGroup = @wg, WorkGroupRaw = @raw, Source = @src, ResolvedAt = @at WHERE Id = @id";
            AddNullable(upd, "@wg", result.Code);
            AddNullable(upd, "@raw", result.Raw);
            upd.Parameters.AddWithValue("@src", result.Source);
            upd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
            upd.Parameters.AddWithValue("@id", id);
            upd.ExecuteNonQuery();
            return;
        }
        r.Close();

        using SqliteCommand ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = """
            INSERT INTO github_repo_workgroups (RepoFullName, WorkGroup, WorkGroupRaw, Source, ResolvedAt)
            VALUES (@r, @wg, @raw, @src, @at)
            """;
        ins.Parameters.AddWithValue("@r", repo);
        AddNullable(ins, "@wg", result.Code);
        AddNullable(ins, "@raw", result.Raw);
        ins.Parameters.AddWithValue("@src", result.Source);
        ins.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
        ins.ExecuteNonQuery();
    }

    private static int CountUnresolvedKept(SqliteConnection conn, SqliteTransaction tx, string repo)
    {
        int total = 0;
        string[] tables = ["github_canonical_artifacts", "github_structure_definitions", "github_spec_file_map"];
        foreach (string table in tables)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE RepoFullName = @r AND WorkGroup IS NULL AND WorkGroupRaw IS NOT NULL";
            cmd.Parameters.AddWithValue("@r", repo);
            total += Convert.ToInt32(cmd.ExecuteScalar());
        }
        return total;
    }

    private static int CountStillUnresolved(SqliteConnection conn, SqliteTransaction tx, string repo)
    {
        int total = 0;
        string[] tables = ["github_canonical_artifacts", "github_structure_definitions", "github_spec_file_map"];
        foreach (string table in tables)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE RepoFullName = @r AND WorkGroup IS NULL AND WorkGroupRaw IS NULL";
            cmd.Parameters.AddWithValue("@r", repo);
            total += Convert.ToInt32(cmd.ExecuteScalar());
        }
        return total;
    }

    private static void AddNullable(SqliteCommand cmd, string name, string? value)
    {
        cmd.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
    }

    private sealed class ResolutionCounters
    {
        public int FromExtension;
        public int FromControllingArtifact;
        public int FromJiraSpecKey;
        public int FromRepoDefault;
        public int UnresolvedKept;
        public int StillUnresolved;
    }

    private enum ProvenanceTag { None, Artifact, JiraSpecKey }
}
