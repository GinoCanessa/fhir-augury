using System.Globalization;
using System.Text.RegularExpressions;

namespace FhirAugury.Models.Caching;

/// <summary>
/// Shared file-naming and sorting logic for date-based cache batch files.
/// Used by both Jira (.xml) and Zulip (.json) caches.
/// </summary>
public static partial class CacheFileNaming
{
    /// <summary>Recognized file name patterns.</summary>
    public enum BatchPrefix { WeekOf, DayOf }

    /// <summary>Parsed representation of a cache batch file name.</summary>
    public record ParsedBatchFile(
        string FileName,
        BatchPrefix Prefix,
        DateOnly Date,
        int? SequenceNumber);

    [GeneratedRegex(@"^_WeekOf_(\d{4}-\d{2}-\d{2})(?:-(\d{3}))?\.(\w+)$")]
    private static partial Regex WeekOfPattern();

    [GeneratedRegex(@"^DayOf_(\d{4}-\d{2}-\d{2})(?:-(\d{3}))?\.(\w+)$")]
    private static partial Regex DayOfPattern();

    /// <summary>
    /// Try to parse a file name into its components.
    /// Recognizes:
    ///   _WeekOf_yyyy-MM-dd.ext          (legacy weekly, no sequence)
    ///   DayOf_yyyy-MM-dd.ext            (legacy daily, no sequence)
    ///   _WeekOf_yyyy-MM-dd-###.ext      (weekly with sequence)
    ///   DayOf_yyyy-MM-dd-###.ext        (current daily with sequence)
    /// </summary>
    public static bool TryParse(string fileName, out ParsedBatchFile result)
    {
        result = default!;

        if (string.IsNullOrEmpty(fileName))
            return false;

        // Try _WeekOf_ pattern
        var match = WeekOfPattern().Match(fileName);
        if (match.Success)
        {
            if (!DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return false;

            int? seq = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : null;
            result = new ParsedBatchFile(fileName, BatchPrefix.WeekOf, date, seq);
            return true;
        }

        // Try DayOf_ pattern
        match = DayOfPattern().Match(fileName);
        if (match.Success)
        {
            if (!DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return false;

            int? seq = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : null;
            result = new ParsedBatchFile(fileName, BatchPrefix.DayOf, date, seq);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Generate the next available file name for a daily batch.
    /// Scans existing files in the directory and increments the sequence number.
    /// Returns "DayOf_yyyy-MM-dd-###.{extension}" format.
    /// </summary>
    public static string GenerateDailyFileName(DateOnly date, string extension, IEnumerable<string> existingFiles)
    {
        var dateStr = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var prefix = $"DayOf_{dateStr}-";

        int maxSeq = -1;
        foreach (var file in existingFiles)
        {
            var name = Path.GetFileName(file);
            if (TryParse(name, out var parsed) &&
                parsed.Prefix == BatchPrefix.DayOf &&
                parsed.Date == date &&
                parsed.SequenceNumber.HasValue)
            {
                maxSeq = Math.Max(maxSeq, parsed.SequenceNumber.Value);
            }
        }

        return $"DayOf_{dateStr}-{(maxSeq + 1):D3}.{extension}";
    }

    /// <summary>
    /// Generate the next available file name for a weekly batch.
    /// Returns "_WeekOf_yyyy-MM-dd-###.{extension}" format.
    /// The date is normalized to the Monday of the given week.
    /// </summary>
    public static string GenerateWeeklyFileName(DateOnly date, string extension, IEnumerable<string> existingFiles)
    {
        var monday = NormalizeToMonday(date);
        var dateStr = monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        int maxSeq = -1;
        foreach (var file in existingFiles)
        {
            var name = Path.GetFileName(file);
            if (TryParse(name, out var parsed) &&
                parsed.Prefix == BatchPrefix.WeekOf &&
                parsed.Date == monday &&
                parsed.SequenceNumber.HasValue)
            {
                maxSeq = Math.Max(maxSeq, parsed.SequenceNumber.Value);
            }
        }

        return $"_WeekOf_{dateStr}-{(maxSeq + 1):D3}.{extension}";
    }

    /// <summary>
    /// Sort parsed batch files in the canonical ingestion order:
    /// 1. Ascending by date
    /// 2. Files without sequence numbers before files with sequence numbers
    ///    (for the same date)
    /// 3. _WeekOf_ before DayOf_ (for the same date)
    /// 4. Ascending by sequence number
    /// </summary>
    public static IEnumerable<ParsedBatchFile> SortForIngestion(IEnumerable<ParsedBatchFile> files) =>
        files.OrderBy(f => f.Date)
             .ThenBy(f => f.SequenceNumber.HasValue ? 1 : 0)
             .ThenBy(f => f.Prefix == BatchPrefix.WeekOf ? 0 : 1)
             .ThenBy(f => f.SequenceNumber ?? 0);

    private static DateOnly NormalizeToMonday(DateOnly date)
    {
        var daysFromMonday = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-daysFromMonday);
    }
}
