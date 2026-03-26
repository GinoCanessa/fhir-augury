using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FhirAugury.Common.Caching;

/// <summary>
/// Shared file-naming and sorting logic for date-based cache batch files.
/// </summary>
public static partial class CacheFileNaming
{
    public enum BatchPrefix { WeekOf, DayOf }

    public record ParsedBatchFile(
        string FileName,
        BatchPrefix Prefix,
        DateOnly Date,
        int? SequenceNumber);

    [GeneratedRegex(@"^_WeekOf_(\d{4}-\d{2}-\d{2})(?:-(\d{3}))?\.(\w+)$")]
    private static partial Regex WeekOfPattern();

    [GeneratedRegex(@"^DayOf_(\d{4}-\d{2}-\d{2})(?:-(\d{3}))?\.(\w+)$")]
    private static partial Regex DayOfPattern();

    public static bool TryParse(string fileName, [NotNullWhen(true)] out ParsedBatchFile? result)
    {
        result = null;

        if (string.IsNullOrEmpty(fileName))
            return false;

        Match match = WeekOfPattern().Match(fileName);
        if (match.Success)
        {
            if (!DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
                return false;

            int? seq = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : null;
            result = new ParsedBatchFile(fileName, BatchPrefix.WeekOf, date, seq);
            return true;
        }

        match = DayOfPattern().Match(fileName);
        if (match.Success)
        {
            if (!DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
                return false;

            int? seq = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : null;
            result = new ParsedBatchFile(fileName, BatchPrefix.DayOf, date, seq);
            return true;
        }

        return false;
    }

    public static string GenerateDailyFileName(DateOnly date, string extension, IEnumerable<string> existingFiles)
    {
        string dateStr = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        int maxSeq = -1;
        foreach (string file in existingFiles)
        {
            string name = Path.GetFileName(file);
            if (TryParse(name, out ParsedBatchFile? parsed) &&
                parsed.Prefix == BatchPrefix.DayOf &&
                parsed.Date == date &&
                parsed.SequenceNumber.HasValue)
            {
                maxSeq = Math.Max(maxSeq, parsed.SequenceNumber.Value);
            }
        }

        return $"DayOf_{dateStr}-{(maxSeq + 1):D3}.{extension}";
    }

    public static string GenerateWeeklyFileName(DateOnly date, string extension, IEnumerable<string> existingFiles)
    {
        DateOnly monday = NormalizeToMonday(date);
        string dateStr = monday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        int maxSeq = -1;
        foreach (string file in existingFiles)
        {
            string name = Path.GetFileName(file);
            if (TryParse(name, out ParsedBatchFile? parsed) &&
                parsed.Prefix == BatchPrefix.WeekOf &&
                parsed.Date == monday &&
                parsed.SequenceNumber.HasValue)
            {
                maxSeq = Math.Max(maxSeq, parsed.SequenceNumber.Value);
            }
        }

        return $"_WeekOf_{dateStr}-{(maxSeq + 1):D3}.{extension}";
    }

    public static IEnumerable<ParsedBatchFile> SortForIngestion(IEnumerable<ParsedBatchFile> files) =>
        files.OrderBy(f => f.Date)
             .ThenBy(f => f.SequenceNumber.HasValue ? 1 : 0)
             .ThenBy(f => f.Prefix == BatchPrefix.WeekOf ? 0 : 1)
             .ThenBy(f => f.SequenceNumber ?? 0);

    private static DateOnly NormalizeToMonday(DateOnly date)
    {
        int daysFromMonday = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-daysFromMonday);
    }
}
