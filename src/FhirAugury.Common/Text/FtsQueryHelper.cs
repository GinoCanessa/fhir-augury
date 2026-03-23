using System.Text;

namespace FhirAugury.Common.Text;

/// <summary>
/// Shared helper for sanitizing FTS5 (full-text search) queries.
/// Wraps each term in double-quotes and escapes embedded quotes.
/// </summary>
public static class FtsQueryHelper
{
    /// <summary>
    /// Sanitizes a free-text query for use in an SQLite FTS5 MATCH expression.
    /// Each whitespace-delimited term is quoted to prevent FTS syntax injection.
    /// </summary>
    public static string SanitizeFtsQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return string.Empty;

        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sb = new StringBuilder(query.Length + terms.Length * 4);

        for (int i = 0; i < terms.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(terms[i])) continue;

            if (sb.Length > 0) sb.Append(' ');
            sb.Append('"');
            sb.Append(terms[i].Replace("\"", "\"\""));
            sb.Append('"');
        }

        return sb.ToString();
    }
}
