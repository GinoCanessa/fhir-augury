using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;

/// <summary>Jira keys harvested from a single pull request's title + body.</summary>
public sealed record PrTicketHarvest(
    string PrKey,
    IReadOnlyList<string> TicketKeys,
    IReadOnlyList<string> ContributingShas);

/// <summary>
/// Gap-fill resolver: turns a set of window commits that yielded no ticket keys
/// ("gap commits") into per-PR harvested Jira keys by reading the read-only GitHub
/// source DB directly (<c>github_commit_pr_links</c> → <c>github_issues</c>). Keys
/// are harvested from each PR's title+body using the same known-prefix rules as
/// commit messages (<see cref="TicketAttributor.ExtractTicketKeys"/>), once per PR.
/// Best-effort: a missing DB, missing link, or missing PR body is a clean no-op.
/// </summary>
public static class PrTicketResolver
{
    /// <summary>
    /// Resolves <paramref name="gapCommits"/> to the pull request(s) that
    /// introduced them and harvests each PR's Jira ticket keys exactly once,
    /// recording the contributing commit SHAs. Output is ordered by PR key for
    /// deterministic results. Never throws; returns what it has accumulated.
    /// </summary>
    public static IReadOnlyList<PrTicketHarvest> Resolve(
        string githubDbPath,
        IReadOnlyList<WindowCommit> gapCommits,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(githubDbPath) || !File.Exists(githubDbPath) || gapCommits.Count == 0)
        {
            return [];
        }

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = githubDbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString;

        // PR key (ordinal) → contributing SHAs (ordinal-distinct, insertion order).
        Dictionary<string, List<string>> prToShas = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> prShaSeen = new(StringComparer.Ordinal);

        try
        {
            using SqliteConnection connection = new(connectionString);
            connection.Open();

            foreach (WindowCommit commit in gapCommits)
            {
                if (string.IsNullOrWhiteSpace(commit.Sha)) continue;

                using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT PrNumber, RepoFullName FROM github_commit_pr_links WHERE CommitSha = $sha";
                cmd.Parameters.AddWithValue("$sha", commit.Sha);

                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                    int prNumber = reader.GetInt32(0);
                    string repoFullName = reader.GetString(1);
                    string prKey = $"{repoFullName}#{prNumber}";

                    if (!prToShas.TryGetValue(prKey, out List<string>? shas))
                    {
                        shas = [];
                        prToShas[prKey] = shas;
                        prShaSeen[prKey] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                    if (prShaSeen[prKey].Add(commit.Sha))
                    {
                        shas.Add(commit.Sha);
                    }
                }
            }

            List<PrTicketHarvest> results = [];
            foreach (string prKey in prToShas.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText =
                    "SELECT Title, Body FROM github_issues " +
                    "WHERE UniqueKey = $key AND IsPullRequest = 1 LIMIT 1";
                cmd.Parameters.AddWithValue("$key", prKey);

                using SqliteDataReader reader = cmd.ExecuteReader();
                if (!reader.Read()) continue; // no PR row → skip

                string title = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                string body = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);

                IReadOnlyList<string> keys = TicketAttributor.ExtractTicketKeys($"{title}\n{body}");
                if (keys.Count == 0) continue; // PR names no known ticket → skip

                results.Add(new PrTicketHarvest(prKey, keys, prToShas[prKey]));
            }

            return results;
        }
        catch (SqliteException ex)
        {
            logger?.LogDebug(ex, "PR ticket resolution failed against {Db}", githubDbPath);
            return [];
        }
    }
}
