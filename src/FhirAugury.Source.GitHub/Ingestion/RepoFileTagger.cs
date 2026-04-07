using FhirAugury.Source.GitHub.Configuration;
using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.GitHub.Ingestion;

/// <summary>
/// Orchestrates file tagging by running discovery patterns against
/// cloned repositories and persisting the results.
/// </summary>
public class RepoFileTagger(
    IEnumerable<IRepoDiscoveryPattern> patterns,
    TagWeightResolver weightResolver,
    GitHubDatabase database,
    ILogger<RepoFileTagger> logger)
{
    /// <summary>
    /// Discovers and applies tags for a repository clone.
    /// Clears existing tags for the repo before applying new ones.
    /// </summary>
    public void ApplyTags(string repoFullName, string clonePath, CancellationToken ct)
    {
        using SqliteConnection connection = database.OpenConnection();

        ClearTags(connection, repoFullName);

        foreach (IRepoDiscoveryPattern pattern in patterns)
        {
            if (!pattern.AppliesTo(repoFullName, clonePath))
                continue;

            logger.LogInformation("Applying {Pattern} pattern to {Repo}",
                pattern.PatternName, repoFullName);

            List<GitHubFileTagRecord> tags = pattern.DiscoverTags(
                repoFullName, clonePath, ct);

            // Apply weights from configuration
            foreach (GitHubFileTagRecord tag in tags)
            {
                tag.Weight = weightResolver.ResolveWeight(
                    tag.TagCategory, tag.TagName, tag.TagModifier);
            }

            if (tags.Count > 0)
            {
                // Insert in batches
                const int batchSize = 1000;
                for (int i = 0; i < tags.Count; i += batchSize)
                {
                    List<GitHubFileTagRecord> batch = tags.GetRange(i, Math.Min(batchSize, tags.Count - i));
                    batch.Insert(connection, ignoreDuplicates: true, insertPrimaryKey: true);
                }

                logger.LogInformation("Applied {Count} tags via {Pattern}",
                    tags.Count, pattern.PatternName);
            }
        }
    }

    private static void ClearTags(SqliteConnection connection, string repoFullName)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM github_file_tags WHERE RepoFullName = @repo";
        cmd.Parameters.AddWithValue("@repo", repoFullName);
        cmd.ExecuteNonQuery();
    }
}
