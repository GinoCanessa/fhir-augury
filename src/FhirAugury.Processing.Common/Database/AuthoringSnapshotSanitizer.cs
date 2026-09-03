using Microsoft.Data.Sqlite;

namespace FhirAugury.Processing.Common.Database;

public sealed record AuthoringSnapshotTable(
    string Name,
    IReadOnlyCollection<string>? Columns = null);

public sealed class AuthoringSnapshotSanitizer
{
    private readonly IReadOnlyDictionary<string, AuthoringSnapshotTable> _tables;

    public AuthoringSnapshotSanitizer(IEnumerable<AuthoringSnapshotTable> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        _tables = tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);
    }

    public static AuthoringSnapshotSanitizer CreateCore(
        IEnumerable<AuthoringSnapshotTable>? domainTables = null)
    {
        List<AuthoringSnapshotTable> tables =
        [
            new(
                "authoring_runs",
                [
                    "Id",
                    "ProcessorKind",
                    "AuthoringEpoch",
                    "Status",
                    "DatabaseOnly",
                    "TotalItems",
                    "CreatedAt",
                    "StartedAt",
                    "CompletedAt",
                    "SnapshotId",
                ]),
            new(
                "authoring_run_items",
                [
                    "Id",
                    "RunId",
                    "BusinessKey",
                    "ItemKind",
                    "ExpectedSourceRevision",
                    "Status",
                    "AcceptedReceiptId",
                    "AttemptCount",
                    "CreatedAt",
                    "StartedAt",
                    "CompletedAt",
                ]),
            new(
                "authoring_result_receipts",
                [
                    "Id",
                    "OperationId",
                    "RunId",
                    "RunItemId",
                    "BusinessKey",
                    "ContentHash",
                    "ExpectedSourceRevision",
                    "ObservedSourceRevision",
                    "AuthoringEpoch",
                    "PersistedAt",
                ]),
            new(
                "authoring_snapshot_provenance",
                [
                    "SnapshotId",
                    "ProcessorKind",
                    "RunId",
                    "AuthoringEpoch",
                    "Sequence",
                    "SchemaVersion",
                    "ItemCount",
                    "ReceiptCount",
                    "TableCountsJson",
                    "CreatedAt",
                ]),
        ];

        if (domainTables is not null)
        {
            tables.AddRange(domainTables);
        }
        return new AuthoringSnapshotSanitizer(tables);
    }

    public async Task SanitizeAsync(SqliteConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;", ct);
        foreach ((string type, string name) in await ReadSchemaObjectsAsync(connection, ct))
        {
            if (type is "view" or "trigger")
            {
                await ExecuteAsync(connection, $"DROP {type.ToUpperInvariant()} IF EXISTS {Quote(name)};", ct);
            }
        }

        IReadOnlyList<string> existingTables = await ReadTableNamesAsync(connection, ct);
        foreach (string tableName in existingTables)
        {
            if (!_tables.TryGetValue(tableName, out AuthoringSnapshotTable? allowed))
            {
                await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {Quote(tableName)};", ct);
                continue;
            }

            if (allowed.Columns is null)
            {
                continue;
            }

            IReadOnlyList<string> existingColumns = await ReadColumnNamesAsync(connection, tableName, ct);
            string[] selectedColumns = allowed.Columns.ToArray();
            string[] missing = selectedColumns
                .Where(column => !existingColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Snapshot table '{tableName}' is missing required columns: {string.Join(", ", missing)}.");
            }

            if (existingColumns.Count == selectedColumns.Length &&
                existingColumns.All(column => selectedColumns.Contains(column, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            string replacementName = $"__snapshot_{Guid.NewGuid():N}";
            string columnList = string.Join(", ", selectedColumns.Select(Quote));
            await ExecuteAsync(
                connection,
                $"CREATE TABLE {Quote(replacementName)} AS SELECT {columnList} FROM {Quote(tableName)};",
                ct);
            await ExecuteAsync(connection, $"DROP TABLE {Quote(tableName)};", ct);
            await ExecuteAsync(
                connection,
                $"ALTER TABLE {Quote(replacementName)} RENAME TO {Quote(tableName)};",
                ct);
        }

        await ExecuteAsync(connection, "VACUUM;", ct);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", ct);
    }

    private static async Task<IReadOnlyList<(string Type, string Name)>> ReadSchemaObjectsAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT type, name
            FROM sqlite_master
            WHERE type IN ('view', 'trigger')
              AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name
            """;
        List<(string Type, string Name)> values = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            values.Add((reader.GetString(0), reader.GetString(1)));
        }
        return values;
    }

    private static async Task<IReadOnlyList<string>> ReadTableNamesAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;
        List<string> values = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetString(0));
        }
        return values;
    }

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(tableName)});";
        List<string> values = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            values.Add(reader.GetString(1));
        }
        return values;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Quote(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
