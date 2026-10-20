namespace FhirAugury.Processing.Contracts;

public sealed class AuthoringSnapshotTableSchema
{
    public AuthoringSnapshotTableSchema(
        string name,
        IEnumerable<string> columns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);

        string[] copiedColumns = columns.ToArray();
        if (copiedColumns.Length == 0 ||
            copiedColumns.Any(string.IsNullOrWhiteSpace) ||
            copiedColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                copiedColumns.Length)
        {
            throw new ArgumentException(
                $"Snapshot table '{name}' must declare a non-empty, unique column catalog.",
                nameof(columns));
        }

        Name = name;
        Columns = Array.AsReadOnly(copiedColumns);
    }

    public string Name { get; }

    public IReadOnlyList<string> Columns { get; }
}

public sealed class AuthoringSnapshotSchemaCatalog
{
    public AuthoringSnapshotSchemaCatalog(
        int version,
        IEnumerable<AuthoringSnapshotTableSchema> tables,
        IEnumerable<string> countedTables)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(countedTables);

        AuthoringSnapshotTableSchema[] copiedTables = tables.ToArray();
        if (copiedTables.Length == 0 ||
            copiedTables.Select(table => table.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != copiedTables.Length)
        {
            throw new ArgumentException(
                "Snapshot schema catalogs must declare a non-empty, unique table set.",
                nameof(tables));
        }

        string[] copiedCountedTables = countedTables.ToArray();
        if (copiedCountedTables.Any(string.IsNullOrWhiteSpace) ||
            copiedCountedTables.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                copiedCountedTables.Length ||
            copiedCountedTables.Any(name =>
                copiedTables.All(table =>
                    !string.Equals(
                        table.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase))))
        {
            throw new ArgumentException(
                "Snapshot counted tables must be unique members of the public table catalog.",
                nameof(countedTables));
        }

        Version = version;
        Tables = Array.AsReadOnly(copiedTables);
        CountedTables = Array.AsReadOnly(copiedCountedTables);
    }

    public int Version { get; }

    public IReadOnlyList<AuthoringSnapshotTableSchema> Tables { get; }

    public IReadOnlyList<string> CountedTables { get; }
}

public static class AuthoringSnapshotSchemaV1
{
    public const int Version = 1;

    public static IReadOnlyList<AuthoringSnapshotTableSchema> CoreTables { get; } =
        Array.AsReadOnly<AuthoringSnapshotTableSchema>(
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
        ]);
}

public static class AuthoringSnapshotSchemaV2
{
    public const int Version = 2;

    public static IReadOnlyList<AuthoringSnapshotTableSchema> CoreTables { get; } =
        Array.AsReadOnly<AuthoringSnapshotTableSchema>(
        [
            .. AuthoringSnapshotSchemaV1.CoreTables,
            new(
                "authoring_run_input_provenance",
                [
                    "RowId",
                    "RunId",
                    "Source",
                    "LatestSuccessfulRefreshAt",
                    "ContentRevision",
                    "CapturedAt",
                ]),
        ]);
}
