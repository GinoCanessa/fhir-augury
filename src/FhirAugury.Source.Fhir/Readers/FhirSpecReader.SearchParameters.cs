using FhirAugury.Source.Fhir.Api;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Fhir.Readers;

public sealed partial class FhirSpecReader
{
    private const string SearchParameterColumns =
        "Id, Code, Name, Title, BaseResources, AdditionalBaseResources, SearchType, Expression, " +
        "ReferenceTargets, Modifiers, Comparators, Status, StandardStatus, WorkGroup, FhirMaturity, " +
        "UnversionedUrl, VersionedUrl, Description";

    /// <summary>Lists search parameters in a package, optionally filtered by base resource and code.</summary>
    public List<SearchParameterInfo> ListSearchParameters(
        int packageKey, string? baseResource = null, string? code = null)
    {
        if (!_db.Exists)
        {
            return [];
        }

        using SqliteConnection conn = _db.OpenConnection();
        List<(SearchParameterInfo Info, long Key, int ComponentCount)> rows = [];

        using (SqliteCommand cmd = conn.CreateCommand())
        {
            List<string> where = ["PackageKey = $pk"];
            cmd.Parameters.AddWithValue("$pk", packageKey);

            if (!string.IsNullOrWhiteSpace(baseResource))
            {
                // BaseResources / AdditionalBaseResources are comma-separated; match a whole token.
                where.Add("((',' || BaseResources || ',') LIKE $bl " +
                          "OR (',' || IFNULL(AdditionalBaseResources, '') || ',') LIKE $bl)");
                cmd.Parameters.AddWithValue("$bl", $"%,{baseResource},%");
            }
            if (!string.IsNullOrWhiteSpace(code))
            {
                where.Add("Code = $code");
                cmd.Parameters.AddWithValue("$code", code);
            }

            cmd.CommandText =
                $"SELECT {SearchParameterColumns}, Key, ComponentCount FROM SearchParameters " +
                $"WHERE {string.Join(" AND ", where)} ORDER BY Code, Name";

            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add((ReadSearchParameter(r), r.GetInt64(18), r.GetInt32(19)));
            }
        }

        Dictionary<long, List<SearchParameterComponentInfo>> components = LoadSearchParameterComponents(
            conn, rows.Where(x => x.ComponentCount > 0).Select(x => x.Key).ToList());

        return rows.Select(x =>
            components.TryGetValue(x.Key, out List<SearchParameterComponentInfo>? comps)
                ? x.Info with { Components = comps }
                : x.Info).ToList();
    }

    /// <summary>Loads a single search parameter by id / code / name / url (with components).</summary>
    public SearchParameterInfo? GetSearchParameter(int packageKey, string idOrCode)
    {
        if (!_db.Exists)
        {
            return null;
        }

        using SqliteConnection conn = _db.OpenConnection();
        SearchParameterInfo info;
        long key;
        int componentCount;
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                $"SELECT {SearchParameterColumns}, Key, ComponentCount FROM SearchParameters " +
                "WHERE PackageKey = $pk AND (Id = $x OR Code = $x OR Name = $x " +
                "OR VersionedUrl = $x OR UnversionedUrl = $x) ORDER BY Key LIMIT 1";
            cmd.Parameters.AddWithValue("$pk", packageKey);
            cmd.Parameters.AddWithValue("$x", idOrCode);
            using SqliteDataReader r = cmd.ExecuteReader();
            if (!r.Read())
            {
                return null;
            }
            info = ReadSearchParameter(r);
            key = r.GetInt64(18);
            componentCount = r.GetInt32(19);
        }

        if (componentCount > 0)
        {
            Dictionary<long, List<SearchParameterComponentInfo>> components =
                LoadSearchParameterComponents(conn, [key]);
            if (components.TryGetValue(key, out List<SearchParameterComponentInfo>? comps))
            {
                info = info with { Components = comps };
            }
        }
        return info;
    }

    private static SearchParameterInfo ReadSearchParameter(SqliteDataReader r)
    {
        List<string> baseResources = SplitCsv(r.GetString(4));
        baseResources.AddRange(SplitCsv(GetNullableString(r, 5)));
        return new SearchParameterInfo(
            Id: r.GetString(0),
            Code: r.GetString(1),
            Name: r.GetString(2),
            Title: GetNullableString(r, 3),
            Base: baseResources,
            Type: GetNullableString(r, 6),
            Expression: GetNullableString(r, 7),
            Targets: SplitCsv(GetNullableString(r, 8)),
            Modifiers: SplitCsv(GetNullableString(r, 9)),
            Comparators: SplitCsv(GetNullableString(r, 10)),
            Components: [],
            Status: GetNullableString(r, 11),
            StandardStatus: GetNullableString(r, 12),
            WorkGroup: GetNullableString(r, 13),
            FhirMaturity: GetNullableInt(r, 14),
            UnversionedUrl: r.GetString(15),
            VersionedUrl: r.GetString(16),
            Description: GetNullableString(r, 17));
    }

    private static Dictionary<long, List<SearchParameterComponentInfo>> LoadSearchParameterComponents(
        SqliteConnection conn, IReadOnlyList<long> searchParameterKeys)
    {
        Dictionary<long, List<SearchParameterComponentInfo>> result = [];
        if (searchParameterKeys.Count == 0)
        {
            return result;
        }

        using SqliteCommand cmd = conn.CreateCommand();
        List<string> placeholders = [];
        for (int i = 0; i < searchParameterKeys.Count; i++)
        {
            string p = $"$k{i}";
            placeholders.Add(p);
            cmd.Parameters.AddWithValue(p, searchParameterKeys[i]);
        }

        cmd.CommandText =
            "SELECT SearchParameterKey, DefinitionCanonical, Expression FROM SearchParameterComponents " +
            $"WHERE SearchParameterKey IN ({string.Join(", ", placeholders)}) ORDER BY SearchParameterKey, Key";

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            long key = r.GetInt64(0);
            if (!result.TryGetValue(key, out List<SearchParameterComponentInfo>? list))
            {
                list = [];
                result[key] = list;
            }
            list.Add(new SearchParameterComponentInfo(r.GetString(1), r.GetString(2)));
        }
        return result;
    }
}
