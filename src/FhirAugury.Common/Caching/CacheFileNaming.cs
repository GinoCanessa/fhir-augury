using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FhirAugury.Common.Caching;

/// <summary>
/// Shared file-naming and sorting logic for date-range cache files.
/// Filenames follow the flat convention
/// <c>&lt;YYYYMMDD&gt;-&lt;YYYYMMDD&gt;-&lt;NNN&gt;.&lt;ext&gt;</c>.
/// Single-day files have <c>start == end</c>.
/// </summary>
public static partial class CacheFileNaming
{
    public record ParsedCacheFile(
        string FileName,
        DateOnly StartDate,
        DateOnly EndDate,
        int? SequenceNumber,
        string Extension);

    [GeneratedRegex(@"^(\d{8})-(\d{8})(?:-(\d{3}))?\.(\w+)$")]
    private static partial Regex RangePattern();

    /// <summary>
    /// Attempts to parse <paramref name="fileName"/> into a <see cref="ParsedCacheFile"/>.
    /// Returns <c>false</c> for legacy <c>DayOf_</c>/<c>_WeekOf_</c> formats — those
    /// are handled by <see cref="CacheFileNameMigrator"/>, not here.
    /// </summary>
    public static bool TryParse(string fileName, [NotNullWhen(true)] out ParsedCacheFile? result)
    {
        result = null;

        if (string.IsNullOrEmpty(fileName))
            return false;

        Match match = RangePattern().Match(fileName);
        if (!match.Success)
            return false;

        if (!DateOnly.TryParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly start))
            return false;
        if (!DateOnly.TryParseExact(match.Groups[2].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly end))
            return false;
        if (start > end)
            return false;

        int? seq = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : null;
        string ext = match.Groups[4].Value;

        result = new ParsedCacheFile(fileName, start, end, seq, ext);
        return true;
    }

    /// <summary>
    /// Generates a unique filename for the given <paramref name="start"/>..<paramref name="end"/>
    /// range, incrementing the sequence suffix past any existing files with the same range.
    /// </summary>
    public static string GenerateFileName(DateOnly start, DateOnly end, string extension, IEnumerable<string> existingFiles)
    {
        if (start > end)
            throw new ArgumentException(
                $"StartDate ({start:yyyy-MM-dd}) must be on or before EndDate ({end:yyyy-MM-dd}).",
                nameof(start));

        int maxSeq = -1;
        foreach (string file in existingFiles)
        {
            string name = Path.GetFileName(file);
            if (TryParse(name, out ParsedCacheFile? parsed) &&
                parsed.StartDate == start &&
                parsed.EndDate == end &&
                parsed.SequenceNumber.HasValue)
            {
                maxSeq = Math.Max(maxSeq, parsed.SequenceNumber.Value);
            }
        }

        int nextSeq = maxSeq + 1;
        return $"{start:yyyyMMdd}-{end:yyyyMMdd}-{nextSeq:D3}.{extension}";
    }

    /// <summary>
    /// Single-day convenience overload — equivalent to
    /// <c>GenerateFileName(date, date, extension, existingFiles)</c>.
    /// </summary>
    public static string GenerateFileName(DateOnly date, string extension, IEnumerable<string> existingFiles)
        => GenerateFileName(date, date, extension, existingFiles);

    /// <summary>
    /// Sorts files so that wider (earlier-spanning) ranges appear before narrower
    /// ranges sharing the same start, which mirrors ingestion ordering expectations.
    /// </summary>
    public static IEnumerable<ParsedCacheFile> SortForIngestion(IEnumerable<ParsedCacheFile> files) =>
        files.OrderBy(f => f.StartDate)
             .ThenByDescending(f => f.EndDate)
             .ThenBy(f => f.SequenceNumber ?? 0);
}
