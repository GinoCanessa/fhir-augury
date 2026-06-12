using System.Collections.Frozen;
using FhirAugury.Tools.FhirSpecReview.SpecReview;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.FhirSpecReview.Readers;

/// <summary>
/// Reads the published baseline vocabulary from the external (read-only)
/// <c>fhir-spec.db</c>. Selects the package row for a requested release and
/// loads sanitized structures / element paths / search-parameter names — the
/// dimensions the legacy removed-artifact check actively matches.
/// </summary>
internal sealed class FhirSpecDbReader
{
    private readonly string _dbPath;

    // Accept common release aliases for releases whose ShortName differs.
    private static readonly Dictionary<string, string[]> s_releaseAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["R2"] = ["DSTU2", "1.0", "hl7.fhir.r2.core"],
        ["R3"] = ["STU3", "3.0", "hl7.fhir.r3.core"],
        ["R4"] = ["4.0", "hl7.fhir.r4.core"],
        ["R4B"] = ["4.3", "hl7.fhir.r4b.core"],
        ["R5"] = ["5.0", "hl7.fhir.r5.core"],
    };

    public FhirSpecDbReader(string dbPath)
    {
        _dbPath = dbPath;
    }

    private SqliteConnection OpenReadOnly()
    {
        SqliteConnection conn = new(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        conn.Open();
        return conn;
    }

    /// <summary>
    /// Resolves a release token (e.g. <c>R5</c>, <c>5.0</c>, <c>DSTU2</c>) to a
    /// <c>Packages.Key</c>. Matches ShortName / FhirVersionShort / PackageId /
    /// Name and a small alias map. Returns null + a clear error otherwise.
    /// </summary>
    public int? ResolvePackageKey(string release, out string? error)
    {
        List<string> candidates = [release];
        if (s_releaseAliases.TryGetValue(release, out string[]? aliases))
        {
            candidates.AddRange(aliases);
        }

        using SqliteConnection conn = OpenReadOnly();
        foreach (string candidate in candidates)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Key FROM Packages
                WHERE ShortName = $c OR FhirVersionShort = $c OR PackageId = $c OR Name = $c
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$c", candidate);
            object? result = cmd.ExecuteScalar();
            if (result is not null && result is not DBNull)
            {
                error = null;
                return Convert.ToInt32(result);
            }
        }

        error = $"Baseline release '{release}' not found in {_dbPath}. Available releases: {string.Join(", ", AvailableReleases())}.";
        return null;
    }

    /// <summary>Lists the ShortName values present in the DB (for error messages).</summary>
    public List<string> AvailableReleases()
    {
        List<string> releases = [];
        try
        {
            using SqliteConnection conn = OpenReadOnly();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ShortName FROM Packages WHERE ShortName IS NOT NULL ORDER BY Key";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read()) releases.Add(reader.GetString(0));
        }
        catch (SqliteException)
        {
            // best effort
        }
        return releases;
    }

    /// <summary>Loads the sanitized baseline vocabulary for a resolved package key.</summary>
    public SpecVocabulary LoadBaselineVocabulary(int packageKey)
    {
        using SqliteConnection conn = OpenReadOnly();

        Dictionary<string, string> structures = new(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT Name, ArtifactClass FROM Structures WHERE PackageKey = $pk AND Name IS NOT NULL";
            cmd.Parameters.AddWithValue("$pk", packageKey);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                SanitizedKeyword key = KeywordSanitizer.Sanitize(reader.GetString(0));
                if (key.FirstLetter == '\0') continue;
                string artifactClass = reader.IsDBNull(1) ? "Resource" : reader.GetString(1);
                structures[key.Clean] = artifactClass;
            }
        }

        HashSet<string> elementPaths = new(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT Path FROM Elements WHERE PackageKey = $pk AND Path IS NOT NULL";
            cmd.Parameters.AddWithValue("$pk", packageKey);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                SanitizedKeyword key = KeywordSanitizer.Sanitize(reader.GetString(0));
                if (key.Clean.Length > 0) elementPaths.Add(key.Clean);
            }
        }

        HashSet<string> searchParams = new(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT DISTINCT Name FROM SearchParameters WHERE PackageKey = $pk AND Name IS NOT NULL";
            cmd.Parameters.AddWithValue("$pk", packageKey);
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                SanitizedKeyword key = KeywordSanitizer.Sanitize(reader.GetString(0));
                if (key.Clean.Length > 0) searchParams.Add(key.Clean);
            }
        }

        return new SpecVocabulary(
            structures.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            elementPaths.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            searchParams.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
    }
}
