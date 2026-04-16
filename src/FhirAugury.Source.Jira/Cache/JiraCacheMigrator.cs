using FhirAugury.Common.Caching;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Source.Jira.Cache;

/// <summary>
/// Migrates legacy flat-layout Jira cache files into the project-scoped layout.
/// Legacy keys: "xml/{filename}" or "json/{filename}"
/// New keys: "{project}/xml/{filename}" or "{project}/json/{filename}"
/// </summary>
public static class JiraCacheMigrator
{
    /// <summary>
    /// Checks for legacy flat-layout cache files and moves them into a
    /// project-scoped subdirectory. Idempotent — no-op if no legacy files exist.
    /// </summary>
    public static void MigrateToProjectLayout(
        IResponseCache cache,
        string defaultProject,
        ILogger logger)
    {
        List<string> oldKeys = cache.EnumerateKeys(JiraCacheLayout.SourceName)
            .Where(IsLegacyKey)
            .ToList();

        if (oldKeys.Count == 0)
        {
            logger.LogDebug("No legacy Jira cache files found — migration not needed");
            return;
        }

        logger.LogInformation(
            "Migrating {Count} legacy cache files to project layout under '{Project}'",
            oldKeys.Count, defaultProject);

        int migrated = 0;
        int failed = 0;

        foreach (string oldKey in oldKeys)
        {
            string newKey = $"{defaultProject}/{oldKey}";

            try
            {
                if (cache.TryGet(JiraCacheLayout.SourceName, oldKey, out Stream? content))
                {
                    using (content)
                    {
                        cache.PutAsync(
                            JiraCacheLayout.SourceName, newKey, content,
                            CancellationToken.None).GetAwaiter().GetResult();
                    }

                    cache.Remove(JiraCacheLayout.SourceName, oldKey);
                    migrated++;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to migrate cache file '{OldKey}'", oldKey);
                failed++;
            }
        }

        logger.LogInformation(
            "Cache migration complete: {Migrated} migrated, {Failed} failed",
            migrated, failed);
    }

    /// <summary>
    /// Determines if a cache key is a legacy flat-layout key.
    /// Legacy keys start with "xml/" or "json/" directly (no project prefix).
    /// </summary>
    internal static bool IsLegacyKey(string key)
    {
        return key.StartsWith($"{JiraCacheLayout.XmlPrefix}/", StringComparison.Ordinal)
            || key.StartsWith($"{JiraCacheLayout.JsonPrefix}/", StringComparison.Ordinal);
    }
}
