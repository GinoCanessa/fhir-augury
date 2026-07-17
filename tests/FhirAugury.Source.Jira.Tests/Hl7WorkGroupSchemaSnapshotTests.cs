using FhirAugury.Source.Jira.Database.Records;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Source.Jira.Tests;

/// <summary>
/// Pins the SQL emitted by <see cref="Hl7WorkGroupRecord.CreateTable"/> to a
/// golden snapshot. Phase 1 of the workgroup-mapping refactor only swaps the
/// indexer/acquirer internals; the underlying Jira-side schema must remain
/// byte-identical so existing databases keep loading without migration.
/// </summary>
public sealed class Hl7WorkGroupSchemaSnapshotTests : IDisposable
{
    private readonly SqliteConnection _conn;

    public Hl7WorkGroupSchemaSnapshotTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public void CreateTable_EmitsExpectedTableSql()
    {
        Hl7WorkGroupRecord.CreateTable(_conn);

        string actual = ReadSqliteMaster(_conn, type: "table", name: "hl7_workgroups");
        // Whitespace-normalize for stability across formatter changes; key is
        // column ordering, types, and constraints.
        string normalized = Normalize(actual);

        const string expected =
            "CREATE TABLE hl7_workgroups ( " +
            "Id INTEGER UNIQUE PRIMARY KEY NOT NULL, " +
            "Code TEXT UNIQUE NOT NULL, " +
            "Name TEXT NOT NULL, " +
            "Definition TEXT, " +
            "Retired INTEGER NOT NULL, " +
            "NameClean TEXT NOT NULL )";

        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void CreateTable_EmitsExpectedIndexes()
    {
        Hl7WorkGroupRecord.CreateTable(_conn);

        string codeIdx = ReadSqliteMaster(_conn, type: "index", name: "IDX_hl7_workgroups_Code");
        string nameIdx = ReadSqliteMaster(_conn, type: "index", name: "IDX_hl7_workgroups_NameClean");

        Assert.Contains("hl7_workgroups", codeIdx);
        Assert.Contains("Code", codeIdx);
        Assert.Contains("hl7_workgroups", nameIdx);
        Assert.Contains("NameClean", nameIdx);
    }

    private static string ReadSqliteMaster(SqliteConnection conn, string type, string name)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type=$type AND name=$name";
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$name", name);
        object? result = cmd.ExecuteScalar();
        Assert.NotNull(result);
        return (string)result!;
    }

    private static string Normalize(string sql)
    {
        string collapsed = string.Join(' ',
            sql.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim();
    }
}
