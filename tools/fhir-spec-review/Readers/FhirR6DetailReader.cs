using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.FhirSpecReview.Readers;

/// <summary>
/// Reads per-artifact Element / Operation / Search-Parameter inventory from the
/// external (read-only) current-build FHIR R6 vocabulary (<c>cache/fhir-r6.db</c>).
/// Modeled on <see cref="FhirSpecDbReader"/>: opens read-only with pooling
/// disabled, resolves the single R6 package, and maps the relevant columns into
/// the detail DTOs consumed by <c>process</c>.
/// </summary>
internal sealed class FhirR6DetailReader
{
    private readonly string _dbPath;

    public FhirR6DetailReader(string dbPath)
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
    /// Resolves the R6 package row (<c>ShortName='R6'</c> / <c>FhirVersionShort='6.0'</c>
    /// / <c>PackageId='hl7.fhir.r6.core'</c>) to a <c>Packages.Key</c>. Returns null
    /// + an error message when not resolvable.
    /// </summary>
    public int? ResolvePackageKey(out string? error)
    {
        using SqliteConnection conn = OpenReadOnly();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Key FROM Packages
            WHERE ShortName = 'R6' OR FhirVersionShort = '6.0' OR PackageId = 'hl7.fhir.r6.core'
            ORDER BY Key
            LIMIT 1
            """;
        object? result = cmd.ExecuteScalar();
        if (result is not null and not DBNull)
        {
            error = null;
            return Convert.ToInt32(result);
        }

        error = $"R6 package (ShortName='R6' / FhirVersionShort='6.0' / PackageId='hl7.fhir.r6.core') not found in {_dbPath}.";
        return null;
    }

    /// <summary>
    /// Resolves an artifact <paramref name="fhirId"/> (canonical URL last segment,
    /// equal to <c>Structures.Id</c>) to a <c>Structures.Key</c>. Matches by
    /// <c>Id</c> first, then <c>Name</c> — many profile structures have
    /// <c>Id != Name</c> (e.g. <c>bp</c> → <c>Observationbp</c>), so a Name-only
    /// match would silently miss them. Returns null for non-resolvable artifacts.
    /// </summary>
    public int? ResolveStructureKey(int packageKey, string fhirId)
    {
        using SqliteConnection conn = OpenReadOnly();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Key FROM Structures
            WHERE PackageKey = $pk AND (Id = $id OR Name = $id)
            ORDER BY (Id = $id) DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$pk", packageKey);
        cmd.Parameters.AddWithValue("$id", fhirId);
        object? result = cmd.ExecuteScalar();
        if (result is not null and not DBNull) return Convert.ToInt32(result);
        return null;
    }

    /// <summary>Loads the element-review rows for a resolved structure, in field order.</summary>
    public List<ArtifactElementDetail> LoadElements(int packageKey, int structureKey)
    {
        List<ArtifactElementDetail> elements = [];
        using SqliteConnection conn = OpenReadOnly();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Path, MinCardinality, MaxCardinalityString, StandardStatus,
                   FixedValue, PatternValue, ValueSetBindingStrength, BindingValueSet,
                   MeaningWhenMissing, IsModifier, ResourceFieldOrder
            FROM Elements
            WHERE PackageKey = $pk AND StructureKey = $sk
            ORDER BY ResourceFieldOrder
            """;
        cmd.Parameters.AddWithValue("$pk", packageKey);
        cmd.Parameters.AddWithValue("$sk", structureKey);
        using SqliteDataReader reader = cmd.ExecuteReader();
        int order = 0;
        while (reader.Read())
        {
            string path = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            int? minCard = reader.IsDBNull(1) ? null : reader.GetInt32(1);
            string? maxCard = reader.IsDBNull(2) ? null : reader.GetString(2);
            string? standardStatus = reader.IsDBNull(3) ? null : reader.GetString(3);
            bool hasFixed = !reader.IsDBNull(4) && reader.GetString(4).Length > 0;
            bool hasPattern = !reader.IsDBNull(5) && reader.GetString(5).Length > 0;
            string? bindingStrength = reader.IsDBNull(6) ? null : reader.GetString(6);
            string? bindingValueSet = reader.IsDBNull(7) ? null : reader.GetString(7);
            string? meaningWhenMissing = reader.IsDBNull(8) ? null : reader.GetString(8);
            bool isModifier = !reader.IsDBNull(9) && reader.GetInt32(9) != 0;
            int fieldOrder = reader.IsDBNull(10) ? order : reader.GetInt32(10);

            bool isRequired = minCard is >= 1;
            bool isTrialUse = standardStatus is not null
                && standardStatus.Contains("trial", StringComparison.OrdinalIgnoreCase);
            bool requiredBinding = string.Equals(bindingStrength, "required", StringComparison.OrdinalIgnoreCase);
            string? requiredBindingValueSet = requiredBinding ? bindingValueSet : null;
            bool externalRequiredBinding = requiredBinding
                && !string.IsNullOrEmpty(bindingValueSet)
                && !bindingValueSet.Contains("hl7.org/fhir", StringComparison.OrdinalIgnoreCase);

            elements.Add(new ArtifactElementDetail(
                path,
                isRequired,
                maxCard,
                isTrialUse,
                hasFixed,
                hasPattern,
                requiredBinding,
                requiredBindingValueSet,
                externalRequiredBinding,
                meaningWhenMissing,
                isModifier,
                fieldOrder));
            order++;
        }
        return elements;
    }

    /// <summary>
    /// Loads operations whose <c>ResourceTypes</c> / <c>AdditionalResourceTypes</c>
    /// token set contains <paramref name="fhirId"/>.
    /// </summary>
    public List<ArtifactOperationDetail> LoadOperations(int packageKey, string fhirId)
    {
        List<ArtifactOperationDetail> operations = [];
        using SqliteConnection conn = OpenReadOnly();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Code, Name, Kind, Status, StandardStatus, FhirMaturity,
                   IsExperimental, WorkGroup, Description, ResourceTypes, AdditionalResourceTypes
            FROM Operations
            WHERE PackageKey = $pk AND (ResourceTypes LIKE $like OR AdditionalResourceTypes LIKE $like)
            ORDER BY Id
            """;
        cmd.Parameters.AddWithValue("$pk", packageKey);
        cmd.Parameters.AddWithValue("$like", "%" + fhirId + "%");
        using SqliteDataReader reader = cmd.ExecuteReader();
        int order = 0;
        while (reader.Read())
        {
            string? resourceTypes = reader.IsDBNull(10) ? null : reader.GetString(10);
            string? additionalResourceTypes = reader.IsDBNull(11) ? null : reader.GetString(11);
            if (!ContainsToken(resourceTypes, fhirId) && !ContainsToken(additionalResourceTypes, fhirId)) continue;

            operations.Add(new ArtifactOperationDetail(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7) != 0,
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                order));
            order++;
        }
        return operations;
    }

    /// <summary>
    /// Loads search parameters whose <c>BaseResources</c> / <c>AdditionalBaseResources</c>
    /// token set contains <paramref name="fhirId"/>.
    /// </summary>
    public List<ArtifactSearchParameterDetail> LoadSearchParameters(int packageKey, string fhirId)
    {
        List<ArtifactSearchParameterDetail> searchParameters = [];
        using SqliteConnection conn = OpenReadOnly();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, Name, Status, FhirMaturity, StandardStatus, IsExperimental,
                   WorkGroup, SearchType, Description, BaseResources, AdditionalBaseResources
            FROM SearchParameters
            WHERE PackageKey = $pk AND (BaseResources LIKE $like OR AdditionalBaseResources LIKE $like)
            ORDER BY Id
            """;
        cmd.Parameters.AddWithValue("$pk", packageKey);
        cmd.Parameters.AddWithValue("$like", "%" + fhirId + "%");
        using SqliteDataReader reader = cmd.ExecuteReader();
        int order = 0;
        while (reader.Read())
        {
            string? baseResources = reader.IsDBNull(9) ? null : reader.GetString(9);
            string? additionalBaseResources = reader.IsDBNull(10) ? null : reader.GetString(10);
            if (!ContainsToken(baseResources, fhirId) && !ContainsToken(additionalBaseResources, fhirId)) continue;

            searchParameters.Add(new ArtifactSearchParameterDetail(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5) != 0,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                order));
            order++;
        }
        return searchParameters;
    }

    /// <summary>
    /// Token-membership test for the comma-delimited multi-value resource columns
    /// (<c>ResourceTypes</c> / <c>BaseResources</c> and their <c>Additional*</c>
    /// siblings). Splits on <c>,</c> and compares each token case-insensitively to
    /// <paramref name="token"/>, avoiding naive substring over-matching
    /// (<c>Patient</c> must not match <c>PatientXyz</c>).
    /// </summary>
    private static bool ContainsToken(string? csv, string token)
    {
        if (string.IsNullOrEmpty(csv)) return false;
        foreach (string part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(part, token, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
