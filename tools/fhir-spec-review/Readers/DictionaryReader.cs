using System.Collections.Frozen;
using FhirAugury.Tools.FhirSpecReview.SpecReview;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.FhirSpecReview.Readers;

/// <summary>Sanitized dictionary words plus a known-typo → correction map.</summary>
/// <param name="Words">Sanitized known-good words.</param>
/// <param name="Typos">Lower-cased typo → correction (typos are NOT sanitized — sanitizing would fix many).</param>
internal sealed record DictionaryData(
    FrozenSet<string> Words,
    FrozenDictionary<string, string?> Typos);

/// <summary>
/// Reads the external (read-only) <c>dictionary.db</c> spell-check resource:
/// <c>words(Word)</c> into a sanitized set and <c>typos(Typo, Correction)</c>
/// into a lower-cased map.
/// </summary>
internal sealed class DictionaryReader
{
    private readonly string _dbPath;

    public DictionaryReader(string dbPath)
    {
        _dbPath = dbPath;
    }

    public bool Exists => File.Exists(_dbPath);

    public DictionaryData Load()
    {
        SqliteConnection conn = new(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        using (conn)
        {
            conn.Open();

            HashSet<string> words = new(StringComparer.OrdinalIgnoreCase);
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT Word FROM words WHERE Word IS NOT NULL";
                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    SanitizedKeyword key = KeywordSanitizer.Sanitize(reader.GetString(0));
                    if (key.FirstLetter == '\0') continue;
                    words.Add(key.Clean);
                }
            }

            Dictionary<string, string?> typos = new(StringComparer.OrdinalIgnoreCase);
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT Typo, Correction FROM typos WHERE Typo IS NOT NULL";
                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string typo = reader.GetString(0).Trim().ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(typo)) continue;
                    typos[typo] = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            return new DictionaryData(
                words.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                typos.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
        }
    }
}
