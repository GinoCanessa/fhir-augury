using FhirAugury.Source.Fhir.Api;
using FhirAugury.Source.Fhir.Database;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Fhir.Readers;

/// <summary>
/// Resolves forgiving release tokens (<c>R5</c>, <c>5.0</c>, <c>DSTU2</c>,
/// <c>hl7.fhir.r5.core</c>, …) to a <c>Packages.Key</c> in the read-only spec
/// database, and owns all reads of the <c>Packages</c> table. The release-alias
/// logic is ported from
/// <c>tools/fhir-spec-review/Readers/FhirSpecDbReader.cs</c> and extended with R6.
/// </summary>
public sealed class FhirReleaseResolver
{
    private readonly FhirSpecDatabase _db;
    private readonly string? _defaultRelease;

    // Accept common release aliases for releases whose ShortName differs.
    private static readonly Dictionary<string, string[]> s_releaseAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["R2"] = ["DSTU2", "1.0", "hl7.fhir.r2.core"],
        ["R3"] = ["STU3", "3.0", "hl7.fhir.r3.core"],
        ["R4"] = ["4.0", "hl7.fhir.r4.core"],
        ["R4B"] = ["4.3", "hl7.fhir.r4b.core"],
        ["R5"] = ["5.0", "hl7.fhir.r5.core"],
        ["R6"] = ["6.0", "6.0.0-ballot4", "hl7.fhir.r6.core"],
    };

    public FhirReleaseResolver(FhirSpecDatabase db, string? defaultRelease = null)
    {
        _db = db;
        _defaultRelease = defaultRelease;
    }

    /// <summary>
    /// Resolves a release token to a <c>Packages.Key</c>. Matches ShortName /
    /// FhirVersionShort / PackageId / Name / PackageVersion plus a small alias map.
    /// Returns null and a clear error otherwise.
    /// </summary>
    public int? ResolvePackageKey(string release, out string? error)
    {
        if (!_db.Exists)
        {
            error = $"Spec database not found at {_db.DatabasePath}.";
            return null;
        }

        List<string> candidates = [release];
        if (s_releaseAliases.TryGetValue(release, out string[]? aliases))
        {
            candidates.AddRange(aliases);
        }

        using SqliteConnection conn = _db.OpenConnection();
        foreach (string candidate in candidates)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Key FROM Packages
                WHERE ShortName = $c OR FhirVersionShort = $c OR PackageId = $c
                   OR Name = $c OR PackageVersion = $c
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$c", candidate);
            object? result = cmd.ExecuteScalar();
            if (result is not null and not DBNull)
            {
                error = null;
                return Convert.ToInt32(result);
            }
        }

        error = $"Release '{release}' not found. Available releases: {string.Join(", ", AvailableReleaseTokens())}.";
        return null;
    }

    /// <summary>
    /// Resolves the default release: the configured <c>DefaultRelease</c> if set,
    /// otherwise the latest stable (newest non-prerelease) package.
    /// </summary>
    public int? ResolveDefaultPackageKey(out string? error)
    {
        if (!string.IsNullOrWhiteSpace(_defaultRelease))
        {
            return ResolvePackageKey(_defaultRelease, out error);
        }

        if (!_db.Exists)
        {
            error = $"Spec database not found at {_db.DatabasePath}.";
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        // Stable = no SemVer prerelease segment (no '-' in PackageVersion).
        // Fall back to the newest package if every release is a prerelease.
        cmd.CommandText = """
            SELECT Key FROM Packages
            ORDER BY (INSTR(PackageVersion, '-') = 0) DESC, Key DESC
            LIMIT 1
            """;
        object? result = cmd.ExecuteScalar();
        if (result is not null and not DBNull)
        {
            error = null;
            return Convert.ToInt32(result);
        }

        error = "No releases found in the spec database.";
        return null;
    }

    /// <summary>
    /// Resolves a (possibly null) release token to a package key and the resolved
    /// release info. A null/blank token — or the literal <c>"default"</c> — resolves
    /// to the default release.
    /// </summary>
    public bool TryResolve(string? release, out int packageKey, out ReleaseInfo? info, out string? error)
    {
        bool useDefault = string.IsNullOrWhiteSpace(release)
            || string.Equals(release, "default", StringComparison.OrdinalIgnoreCase);

        int? key = useDefault
            ? ResolveDefaultPackageKey(out error)
            : ResolvePackageKey(release!, out error);

        if (key is null)
        {
            packageKey = 0;
            info = null;
            return false;
        }

        packageKey = key.Value;
        info = GetReleaseInfo(packageKey);
        if (info is null)
        {
            error = $"Resolved release key {packageKey} could not be loaded.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Loads the release info for a resolved package key.</summary>
    public ReleaseInfo? GetReleaseInfo(int packageKey)
    {
        if (!_db.Exists)
        {
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Key, ShortName, FhirVersionShort, PackageId, PackageVersion, Title
            FROM Packages WHERE Key = $k LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$k", packageKey);
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRelease(reader) : null;
    }

    /// <summary>Lists every release package in the spec database (ordered by key).</summary>
    public List<ReleaseInfo> ListReleaseInfos()
    {
        List<ReleaseInfo> releases = [];
        if (!_db.Exists)
        {
            return releases;
        }

        using SqliteConnection conn = _db.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Key, ShortName, FhirVersionShort, PackageId, PackageVersion, Title
            FROM Packages ORDER BY Key
            """;
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            releases.Add(ReadRelease(reader));
        }
        return releases;
    }

    /// <summary>Lists the ShortName tokens present in the database (for error messages).</summary>
    public List<string> AvailableReleaseTokens()
    {
        List<string> tokens = [];
        if (!_db.Exists)
        {
            return tokens;
        }

        try
        {
            using SqliteConnection conn = _db.OpenConnection();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ShortName FROM Packages WHERE ShortName IS NOT NULL ORDER BY Key";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tokens.Add(reader.GetString(0));
            }
        }
        catch (SqliteException)
        {
            // best effort
        }
        return tokens;
    }

    private static ReleaseInfo ReadRelease(SqliteDataReader r) => new(
        Key: r.GetInt32(0),
        ShortName: r.GetString(1),
        FhirVersion: r.GetString(2),
        PackageId: r.GetString(3),
        PackageVersion: r.GetString(4),
        Title: r.IsDBNull(5) ? null : r.GetString(5));
}
