using FhirAugury.Source.Fhir.Api;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Fhir.Readers;

public sealed partial class FhirSpecReader
{
    private const string OperationSummaryColumns =
        "Id, Name, Title, Code, Kind, AffectsState, ResourceTypes, AdditionalResourceTypes, " +
        "InvokeOnSystem, InvokeOnType, InvokeOnInstance, Status, StandardStatus, WorkGroup, " +
        "FhirMaturity, UnversionedUrl, VersionedUrl, Description";

    /// <summary>Lists the operations in a package.</summary>
    public List<OperationSummary> ListOperations(int packageKey)
    {
        List<OperationSummary> results = [];
        if (!_db.Exists)
        {
            return results;
        }

        using SqliteConnection conn = _db.OpenConnection();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {OperationSummaryColumns} FROM Operations WHERE PackageKey = $pk ORDER BY Code, Name";
        cmd.Parameters.AddWithValue("$pk", packageKey);

        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(ReadOperationSummary(reader));
        }
        return results;
    }

    /// <summary>Loads an operation (by id / code / name / url) with its parameter tree.</summary>
    public OperationDetail? GetOperation(int packageKey, string idOrCode)
    {
        if (!_db.Exists)
        {
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();
        long key;
        OperationSummary summary;
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"SELECT {OperationSummaryColumns}, Key FROM Operations " +
                "WHERE PackageKey = $pk AND (Id = $x OR Code = $x OR Name = $x " +
                "OR VersionedUrl = $x OR UnversionedUrl = $x) ORDER BY Key LIMIT 1";
            cmd.Parameters.AddWithValue("$pk", packageKey);
            cmd.Parameters.AddWithValue("$x", idOrCode);
            using SqliteDataReader r = cmd.ExecuteReader();
            if (!r.Read())
            {
                return null;
            }
            summary = ReadOperationSummary(r);
            key = r.GetInt64(18);
        }

        return new OperationDetail(summary, LoadOperationParameters(conn, key));
    }

    private static OperationSummary ReadOperationSummary(SqliteDataReader r)
    {
        List<string> resourceTypes = SplitCsv(GetNullableString(r, 6));
        resourceTypes.AddRange(SplitCsv(GetNullableString(r, 7)));
        return new OperationSummary(
            Id: r.GetString(0),
            Name: r.GetString(1),
            Title: GetNullableString(r, 2),
            Code: GetNullableString(r, 3),
            Kind: r.GetString(4),
            AffectsState: GetNullableBool(r, 5),
            ResourceTypes: resourceTypes,
            System: r.GetInt64(8) != 0,
            Type: r.GetInt64(9) != 0,
            Instance: r.GetInt64(10) != 0,
            Status: GetNullableString(r, 11),
            StandardStatus: GetNullableString(r, 12),
            WorkGroup: GetNullableString(r, 13),
            FhirMaturity: GetNullableInt(r, 14),
            UnversionedUrl: r.GetString(15),
            VersionedUrl: r.GetString(16),
            Description: GetNullableString(r, 17));
    }

    private static List<OperationParameterInfo> LoadOperationParameters(SqliteConnection conn, long operationKey)
    {
        List<ParameterBuild> builds = [];
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Key, ParentParameterKey, Name, Use, Min, Max, Type, Documentation,
                   AllowedTypes, TargetProfileCanonicals, SearchType, BindingStrength,
                   BindingValueSetCanonical
            FROM OperationParameters WHERE OperationKey = $k
            ORDER BY OperationParameterOrder, ParameterPartOrder
            """;
        cmd.Parameters.AddWithValue("$k", operationKey);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            builds.Add(new ParameterBuild
            {
                Key = r.GetInt64(0),
                ParentKey = GetNullableLong(r, 1),
                Name = r.GetString(2),
                Use = r.GetString(3),
                Min = r.GetInt32(4),
                Max = r.GetString(5),
                Type = GetNullableString(r, 6),
                Documentation = GetNullableString(r, 7),
                AllowedTypes = SplitCsv(GetNullableString(r, 8)),
                TargetProfiles = SplitCsv(GetNullableString(r, 9)),
                SearchType = GetNullableString(r, 10),
                BindingStrength = GetNullableString(r, 11),
                BindingValueSet = GetNullableString(r, 12),
            });
        }

        Dictionary<long, ParameterBuild> byKey = builds.ToDictionary(p => p.Key);
        List<ParameterBuild> roots = [];
        foreach (ParameterBuild p in builds)
        {
            if (p.ParentKey is long pk && byKey.TryGetValue(pk, out ParameterBuild? parent))
            {
                parent.Children.Add(p);
            }
            else
            {
                roots.Add(p);
            }
        }
        return roots.Select(p => p.ToInfo()).ToList();
    }

    private sealed class ParameterBuild
    {
        public long Key { get; init; }
        public long? ParentKey { get; init; }
        public string Name { get; init; } = "";
        public string Use { get; init; } = "";
        public int Min { get; init; }
        public string Max { get; init; } = "";
        public string? Type { get; init; }
        public string? Documentation { get; init; }
        public List<string> AllowedTypes { get; init; } = [];
        public List<string> TargetProfiles { get; init; } = [];
        public string? SearchType { get; init; }
        public string? BindingStrength { get; init; }
        public string? BindingValueSet { get; init; }
        public List<ParameterBuild> Children { get; } = [];

        public OperationParameterInfo ToInfo() => new(
            Name, Use, Min, Max, Type, Documentation, AllowedTypes, TargetProfiles,
            SearchType, BindingStrength, BindingValueSet,
            Children.Select(c => c.ToInfo()).ToList());
    }
}
