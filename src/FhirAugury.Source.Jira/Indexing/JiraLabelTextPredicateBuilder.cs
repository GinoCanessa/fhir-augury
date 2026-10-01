using System.Text;
using FhirAugury.Common.Api;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Indexing;

/// <summary>Builds native LIKE predicates over the raw nullable Labels column.</summary>
public static class JiraLabelTextPredicateBuilder
{
    /// <summary>Appends active label-text groups to an existing WHERE clause.</summary>
    public static void AppendFilter(
        StringBuilder sql,
        List<SqliteParameter> parameters,
        JiraLabelTextFilter? filter)
    {
        AppendGroup(sql, parameters, filter?.Includes, exclude: false);
        AppendGroup(sql, parameters, filter?.Excludes, exclude: true);
    }

    private static void AppendGroup(
        StringBuilder sql,
        List<SqliteParameter> parameters,
        IReadOnlyList<string>? values,
        bool exclude)
    {
        if (values is null) return;

        List<string> predicates = [];
        foreach (string value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            string name = $"@labelText{(exclude ? "Exclude" : "Include")}{predicates.Count}";
            predicates.Add($"Labels {(exclude ? "NOT LIKE" : "LIKE")} {name}");
            parameters.Add(new SqliteParameter(name, $"%{value}%"));
        }

        if (predicates.Count == 0) return;

        sql.Append(exclude ? " AND (Labels IS NULL OR (" : " AND (");
        sql.Append(string.Join(exclude ? " AND " : " OR ", predicates));
        sql.Append(exclude ? "))" : ")");
    }
}
