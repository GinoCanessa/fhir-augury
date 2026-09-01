using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Projects the already-extracted <c>xref_jira</c> rows into first-class
/// PR↔ticket edges in <c>github_pr_ticket_links</c>. No new text extraction:
/// three provenance sources (PR description, PR comment, PR commit) are unioned
/// and de-duplicated to one logical edge per <c>(RepoFullName, PrNumber, JiraKey)</c>
/// with a sorted, comma-joined provenance set.
/// </summary>
public class GitHubPrTicketLinkRebuilder(
    GitHubDatabase database,
    ILogger<GitHubPrTicketLinkRebuilder> logger)
{
    private const string ProvenanceDescription = "description";
    private const string ProvenanceComment = "comment";
    private const string ProvenanceCommit = "commit";

    /// <summary>
    /// Rebuilds PR↔ticket edges for all repos: clears the edge table once, then
    /// projects edges from every repo. Mirrors <see cref="GitHubXRefRebuilder.RebuildAllRepos"/>
    /// so it can run in lock-step after each xref rebuild.
    /// </summary>
    public void RebuildAllRepos(IReadOnlyList<string> repoNames, CancellationToken ct = default)
    {
        using SqliteConnection connection = database.OpenConnection();

        ClearTable(connection);

        int totalEdges = 0;
        foreach (string repo in repoNames)
        {
            totalEdges += RebuildRepo(connection, repo, ct);
        }

        logger.LogInformation("Projected {Count} PR↔ticket edges from {RepoCount} repos", totalEdges, repoNames.Count);
    }

    /// <summary>
    /// Rebuilds PR↔ticket edges for a single repo: clears the edge table, then
    /// projects edges from the specified repo only.
    /// </summary>
    public void RebuildAll(string repoFullName, CancellationToken ct = default)
    {
        RebuildAllRepos([repoFullName], ct);
    }

    private static void ClearTable(SqliteConnection connection)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM github_pr_ticket_links";
        cmd.ExecuteNonQuery();
    }

    private int RebuildRepo(SqliteConnection connection, string repoFullName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // (PrNumber, JiraKey) -> provenance set. Repo is fixed per call.
        Dictionary<(int PrNumber, string JiraKey), SortedSet<string>> edges = [];

        void Add(int prNumber, string jiraKey, string provenance)
        {
            if (string.IsNullOrEmpty(jiraKey)) return;
            (int, string) key = (prNumber, jiraKey);
            if (!edges.TryGetValue(key, out SortedSet<string>? set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                edges[key] = set;
            }
            set.Add(provenance);
        }

        // PR unique-key -> number, for repo PRs only (IsPullRequest=1). Used to
        // resolve description/comment xref rows to a PR number and to exclude
        // non-PR issues.
        Dictionary<string, int> prKeyToNumber = LoadRepoPrKeys(connection, repoFullName);

        // description: xref_jira ContentType='issue' whose SourceId joins a PR row.
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT x.JiraKey, i.Number
                FROM xref_jira x
                JOIN github_issues i ON i.UniqueKey = x.SourceId
                WHERE x.ContentType = 'issue' AND i.IsPullRequest = 1 AND i.RepoFullName = $repo
                """;
            cmd.Parameters.AddWithValue("$repo", repoFullName);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                Add(reader.GetInt32(1), reader.GetString(0), ProvenanceDescription);
            }
        }

        // comment: xref_jira ContentType='comment'; SourceId = "owner/repo#N:commentId".
        // Strip at the LAST ':' to the issue unique-key, then resolve to a PR.
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT x.JiraKey, x.SourceId
                FROM xref_jira x
                WHERE x.ContentType = 'comment' AND x.SourceId LIKE $repoPrefix
                """;
            cmd.Parameters.AddWithValue("$repoPrefix", repoFullName + "#%");
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                string jiraKey = reader.GetString(0);
                string sourceId = reader.GetString(1);
                int colon = sourceId.LastIndexOf(':');
                if (colon <= 0) continue;
                string issueKey = sourceId[..colon];
                if (prKeyToNumber.TryGetValue(issueKey, out int prNumber))
                {
                    Add(prNumber, jiraKey, ProvenanceComment);
                }
            }
        }

        // commit: xref_jira ContentType='commit' (SourceId = sha) joined through
        // github_commit_pr_links (sha -> PrNumber) to the containing PR(s).
        using (SqliteCommand cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT x.JiraKey, l.PrNumber
                FROM xref_jira x
                JOIN github_commit_pr_links l ON l.CommitSha = x.SourceId
                WHERE x.ContentType = 'commit' AND l.RepoFullName = $repo
                """;
            cmd.Parameters.AddWithValue("$repo", repoFullName);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                Add(reader.GetInt32(1), reader.GetString(0), ProvenanceCommit);
            }
        }

        if (edges.Count == 0) return 0;

        List<GitHubPrTicketLinkRecord> records = new(edges.Count);
        foreach (KeyValuePair<(int PrNumber, string JiraKey), SortedSet<string>> edge in edges)
        {
            records.Add(new GitHubPrTicketLinkRecord
            {
                Id = GitHubPrTicketLinkRecord.GetIndex(),
                RepoFullName = repoFullName,
                PrNumber = edge.Key.PrNumber,
                PrUniqueKey = $"{repoFullName}#{edge.Key.PrNumber}",
                JiraKey = edge.Key.JiraKey,
                Provenance = string.Join(",", edge.Value),
            });
        }

        const int batchSize = 1000;
        for (int i = 0; i < records.Count; i += batchSize)
        {
            List<GitHubPrTicketLinkRecord> batch = records.GetRange(i, Math.Min(batchSize, records.Count - i));
            batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
        }

        logger.LogInformation("Projected {Count} PR↔ticket edges from {Repo}", records.Count, repoFullName);
        return records.Count;
    }

    private static Dictionary<string, int> LoadRepoPrKeys(SqliteConnection connection, string repoFullName)
    {
        Dictionary<string, int> map = [];
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT UniqueKey, Number FROM github_issues WHERE RepoFullName = $repo AND IsPullRequest = 1";
        cmd.Parameters.AddWithValue("$repo", repoFullName);
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            map[reader.GetString(0)] = reader.GetInt32(1);
        }
        return map;
    }
}
