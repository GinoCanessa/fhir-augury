using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Common.Caching;

/// <summary>
/// Rewrites legacy <c>DayOf_</c> / <c>_WeekOf_</c> cache filenames to the
/// current flat <c>YYYYMMDD-YYYYMMDD-NNN.ext</c> format. Runs at most once per
/// process via a caller-managed guard flag; the scan is a no-op once the cache
/// has been migrated.
/// </summary>
public static partial class CacheFileNameMigrator
{
    public record MigrationResult(int Migrated, int AlreadyMigrated, int Failed);

    [GeneratedRegex(@"^DayOf_(\d{4}-\d{2}-\d{2})(?:-(\d{3}))?\.(\w+)$")]
    private static partial Regex LegacyDayOfPattern();

    [GeneratedRegex(@"^_WeekOf_(\d{4}-\d{2}-\d{2})(?:-(\d{3}))?\.(\w+)$")]
    private static partial Regex LegacyWeekOfPattern();

    /// <summary>
    /// Scans every cache key under <paramref name="source"/> and rewrites legacy
    /// filenames. Crash-safe: if a "new" key already exists alongside its legacy
    /// sibling (mid-crash state), the legacy key is removed and the existing new
    /// key kept.
    /// </summary>
    public static async Task<MigrationResult> MigrateAsync(
        IResponseCache cache,
        string source,
        ILogger logger,
        CancellationToken ct = default)
    {
        List<string> allKeys = cache.EnumerateKeys(source).ToList();

        int migrated = 0;
        int alreadyMigrated = 0;
        int failed = 0;

        foreach (string oldKey in allKeys)
        {
            ct.ThrowIfCancellationRequested();

            string fileName = Path.GetFileName(oldKey);
            if (!TryBuildNewFileName(fileName, logger, out string? newFileName))
                continue;

            string prefix = oldKey.Length > fileName.Length
                ? oldKey.Substring(0, oldKey.Length - fileName.Length)
                : string.Empty;
            string newKey = $"{prefix}{newFileName}";

            if (string.Equals(newKey, oldKey, StringComparison.Ordinal))
            {
                // already in new format (shouldn't match the legacy regex, but be defensive)
                continue;
            }

            try
            {
                if (NewKeyExists(cache, source, newKey))
                {
                    logger.LogDebug(
                        "CacheFileNameMigrator: new key '{NewKey}' already exists; removing legacy '{OldKey}'",
                        newKey, oldKey);
                    await cache.RemoveAsync(source, oldKey, ct);
                    alreadyMigrated++;
                    continue;
                }

                Stream? content = await cache.TryGetAsync(source, oldKey, ct);
                if (content is null)
                {
                    logger.LogWarning(
                        "CacheFileNameMigrator: legacy key '{OldKey}' could not be read; skipping",
                        oldKey);
                    failed++;
                    continue;
                }

                await using (content)
                {
                    await cache.PutAsync(source, newKey, content, ct);
                }
                await cache.RemoveAsync(source, oldKey, ct);
                migrated++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "CacheFileNameMigrator: failed to migrate '{OldKey}' to '{NewKey}'",
                    oldKey, newKey);
                failed++;
            }
        }

        logger.LogInformation(
            "CacheFileNameMigrator: source={Source} migrated={Migrated} recovered={Recovered} failed={Failed} (0,0,0 = no-op)",
            source, migrated, alreadyMigrated, failed);

        return new MigrationResult(migrated, alreadyMigrated, failed);
    }

    private static bool TryBuildNewFileName(string fileName, ILogger logger, out string? newFileName)
    {
        newFileName = null;

        Match dayMatch = LegacyDayOfPattern().Match(fileName);
        if (dayMatch.Success)
        {
            if (!DateOnly.TryParseExact(dayMatch.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
                return false;
            int seq = dayMatch.Groups[2].Success
                ? int.Parse(dayMatch.Groups[2].Value, CultureInfo.InvariantCulture)
                : 0;
            string ext = dayMatch.Groups[3].Value;
            newFileName = $"{date:yyyyMMdd}-{date:yyyyMMdd}-{seq:D3}.{ext}";
            return true;
        }

        Match weekMatch = LegacyWeekOfPattern().Match(fileName);
        if (weekMatch.Success)
        {
            if (!DateOnly.TryParseExact(weekMatch.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly monday))
                return false;
            if (monday.DayOfWeek != DayOfWeek.Monday)
            {
                logger.LogWarning(
                    "CacheFileNameMigrator: legacy weekly file '{FileName}' has a non-Monday start ({Day}); migrating with start + 6",
                    fileName, monday.DayOfWeek);
            }
            int seq = weekMatch.Groups[2].Success
                ? int.Parse(weekMatch.Groups[2].Value, CultureInfo.InvariantCulture)
                : 0;
            string ext = weekMatch.Groups[3].Value;
            DateOnly end = monday.AddDays(6);
            newFileName = $"{monday:yyyyMMdd}-{end:yyyyMMdd}-{seq:D3}.{ext}";
            return true;
        }

        return false;
    }

    private static bool NewKeyExists(IResponseCache cache, string source, string newKey)
    {
        if (cache.TryGet(source, newKey, out Stream? s) && s is not null)
        {
            s.Dispose();
            return true;
        }
        return false;
    }
}
