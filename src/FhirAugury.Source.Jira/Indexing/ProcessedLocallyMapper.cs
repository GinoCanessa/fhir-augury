using System.Text;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Indexing;

/// <summary>
/// Shared mapping between the boolean <c>ProcessedLocally</c> API
/// surface and the stored <c>ProcessedLocallyAt</c> timestamp column.
/// </summary>
public static class ProcessedLocallyMapper
{
    /// <summary>
    /// Translates a caller-supplied boolean value to the SQLite value
    /// to write into <c>ProcessedLocallyAt</c>.
    /// true  -> DateTimeOffset.UtcNow (ISO-8601 string)
    /// false -> DBNull
    /// null  -> DBNull
    /// </summary>
    public static object ToStorageValue(bool? processedLocally) =>
        processedLocally == true
            ? DateTimeOffset.UtcNow.ToString("o")
            : DBNull.Value;

    /// <summary>
    /// Translates a stored timestamp into the boolean surface value
    /// (non-null -> true, null -> false).
    /// </summary>
    public static bool FromStorageValue(DateTimeOffset? storedValue) =>
        storedValue.HasValue;

    /// <summary>
    /// Appends an optional <c>ProcessedLocallyAt</c> predicate to the
    /// given SQL builder. No-op when <paramref name="filter"/> is null.
    /// Assumes the surrounding query is of the form
    /// <c>"... WHERE 1=1"</c> (so a leading <c>AND</c> is always safe).
    /// </summary>
    public static void AppendFilter(
        StringBuilder sql,
        List<SqliteParameter> parameters,
        bool? filter,
        ref int paramIdx)
    {
        if (filter is null) return;

        sql.Append(
            filter.Value
                ? " AND ProcessedLocallyAt IS NOT NULL"
                : " AND ProcessedLocallyAt IS NULL");
    }
}
