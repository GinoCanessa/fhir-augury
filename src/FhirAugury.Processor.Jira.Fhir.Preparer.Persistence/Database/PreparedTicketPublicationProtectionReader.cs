using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;

public enum PreparedTicketPublicationColumnProtection
{
    Protected,
    PublicationMetadata,
    SelfJiraPublicationMetadata,
    AcceptedZulipPublicationMetadata,
    SnapshotLifecycle,
    Operational,
}

public sealed class PreparedTicketPublicationProtectionException(
    string failureCode,
    string detail) : InvalidOperationException(detail)
{
    public string FailureCode { get; } = failureCode;
}

/// <summary>
/// The explicit preservation graph for publication-enrichment v1. Snapshot
/// reads never create the live inventory's temporary selection table.
/// Current reads use the existing receipt-backed selection on the caller's
/// transaction connection, then add exact-value protection beyond its legacy hash.
/// </summary>
public static class PreparedTicketPublicationProtectionReader
{
    private sealed record TableDefinition(
        string ScopeColumn,
        string[] KeyColumns,
        IReadOnlyDictionary<string, PreparedTicketPublicationColumnProtection> Columns);

    private static readonly IReadOnlyDictionary<string, TableDefinition> Definitions =
        CreateDefinitions();

    private static readonly HashSet<string> TicketTables = new(StringComparer.Ordinal)
    {
        "prepared_tickets", "prepared_ticket_repos", "prepared_ticket_related_jira",
        "prepared_ticket_related_zulip", "prepared_ticket_related_github",
        "prepared_ticket_hydration", "prepared_jira_hydration",
        "prepared_zulip_hydration", "prepared_github_hydration", "prepared_repo_hydration",
        "prepared_ticket_jira_xref", "prepared_ticket_jira_content",
        "prepared_ticket_artifacts", "prepared_ticket_pages",
        "prepared_ticket_in_person_requesters", "prepared_ticket_authoring_state",
    };

    private static readonly HashSet<string> GroupingTables = new(StringComparer.Ordinal)
    {
        "prepared_ticket_topics", "prepared_ticket_topic_groups", "prepared_ticket_topic_members",
    };

    private static readonly string[] PrivateGraphTables =
    [
        "prepared_ticket_authoring_state",
        "prepared_ticket_run_item_partitions",
        "prepared_ticket_publication_refresh_receipts",
        "prepared_ticket_partition_certifications",
    ];

    public static PreparedTicketPublicationColumnProtection ClassifyColumn(
        string table,
        string column)
        => Definitions.TryGetValue(table, out TableDefinition? definition) &&
            definition.Columns.TryGetValue(column, out PreparedTicketPublicationColumnProtection protection)
            ? protection
            : throw Refuse(
                "unclassified-protection-column",
                $"Publication protection has no classification for '{table}.{column}'.");

    public static void ValidateCatalog(AuthoringSnapshotSchemaCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        foreach (AuthoringSnapshotTableSchema table in catalog.Tables)
        {
            foreach (string column in table.Columns)
            {
                _ = ClassifyColumn(table.Name, column);
            }
        }
    }

    public static async Task<PreparedTicketPublicationProtectedInventory> ReadCurrentAsync(
        SqliteConnection connection,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        PreparedTicketPublicationRefreshInventory inventory =
            await PreparerDatabase.GetPublicationRefreshInventoryAsync(connection, ct);
        Dictionary<string, List<DataRow>> tables = await ReadTablesAsync(
            connection, PreparedTicketSnapshotSchemaV3.Catalog, snapshot: false, ct);
        return BuildInventory(
            PreparedTicketSnapshotSchemaV3.Version,
            inventory.Candidates.Select(item => item.ToPublicationCorpusItem()).ToArray(),
            tables,
            snapshot: false);
    }

    public static async Task<PreparedTicketPublicationProtectedInventory> ReadSnapshotAsync(
        SqliteConnection connection,
        int schemaVersion,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        AuthoringSnapshotSchemaCatalog catalog = PreparedTicketSnapshotSchemaResolver.Resolve(schemaVersion);
        Dictionary<string, List<DataRow>> tables =
            await ReadTablesAsync(connection, catalog, snapshot: true, ct);
        return BuildInventory(schemaVersion, ReadSnapshotCorpus(tables), tables, snapshot: true);
    }

    public static PreparedTicketPublicationPreservationComparison Compare(
        PreparedTicketPublicationBaseline baseline,
        PreparedTicketPublicationProtectedInventory current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        PreparedTicketPublicationProtectedInventory original = baseline.Inventory;
        Dictionary<string, PreparedTicketPublicationCorpusItem> currentCorpus =
            current.Corpus.ToDictionary(item => item.TicketKey, StringComparer.OrdinalIgnoreCase);
        foreach (PreparedTicketPublicationCorpusItem item in original.Corpus)
        {
            if (!currentCorpus.TryGetValue(item.TicketKey, out PreparedTicketPublicationCorpusItem? retained) ||
                retained != item)
            {
                throw Refuse("original-output-changed", $"Original accepted output for '{item.TicketKey}' is missing or replaced.");
            }
        }
        EnsureOriginalRows(original.Rows, current.Rows);
        HashSet<string> originalTickets = original.Corpus.Select(item => item.TicketKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> publicTables = PreparedTicketSnapshotSchemaResolver.Resolve(original.SchemaVersion)
            .Tables.Select(table => table.Name).ToHashSet(StringComparer.Ordinal);
        HashSet<(string Table, string Scope, string Key)> originalCoordinates =
            original.Rows.Select(Coordinate).ToHashSet();
        foreach (PreparedTicketPublicationProtectedRow row in current.Rows)
        {
            if (!publicTables.Contains(row.Table) || originalCoordinates.Contains(Coordinate(row)))
            {
                continue;
            }
            // Public snapshots retain only selected historical receipts/items,
            // not every record of each contributing run. History is a subset
            // comparison here; all current history is frozen for execution.
            bool changesOriginal = TicketTables.Contains(row.Table) &&
                originalTickets.Contains(row.Scope);
            if (changesOriginal && !IsAllowedNewZulipRow(row, original.ZulipReferences, current.ZulipReferences))
            {
                throw Refuse("original-output-changed", $"Original protected output gained a row in '{row.Table}'.");
            }
        }

        Dictionary<string, PreparedTicketPublicationProtectedGrouping> currentGrouping =
            current.Grouping.ToDictionary(partition => partition.PartitionKey, StringComparer.Ordinal);
        foreach (PreparedTicketPublicationProtectedGrouping partition in original.Grouping)
        {
            if (!currentGrouping.TryGetValue(partition.PartitionKey, out PreparedTicketPublicationProtectedGrouping? retained) ||
                partition.Fingerprint != retained.Fingerprint)
            {
                throw Refuse(
                    "original-grouping-changed",
                    $"Original grouping partition '{partition.PartitionKey}' changed its IDs, values, order, or membership.");
            }
        }
        string[] additionalKeys = current.Corpus
            .Select(item => item.TicketKey)
            .Where(key => !originalTickets.Contains(key))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key, StringComparer.Ordinal).ToArray();
        HashSet<string> originalPartitions = original.Grouping
            .Select(partition => partition.PartitionKey).ToHashSet(StringComparer.Ordinal);
        string[] additionalPartitions = current.Grouping
            .Where(partition => !originalPartitions.Contains(partition.PartitionKey))
            .Select(partition => partition.PartitionKey)
            .OrderBy(key => key, StringComparer.Ordinal).ToArray();
        AuthoringRunCorpusComparison comparison = new(
            baseline.Source.SnapshotId, original.Corpus.Count, current.Corpus.Count, additionalKeys.Length);
        comparison.Validate();
        return new(comparison, additionalKeys, additionalPartitions, current);
    }

    public static PreparedTicketPublicationEnrichmentInput CreateRecipeInput(
        PreparedTicketPublicationBaseline baseline,
        PreparedTicketPublicationProtectedInventory current)
    {
        PreparedTicketPublicationPreservationComparison comparison = Compare(baseline, current);
        PreparedTicketPublicationEnrichmentInput input = new(
            PreparedTicketPublicationEnrichmentContract.CurrentVersion,
            baseline.Source,
            baseline.Inventory.CorpusFingerprint,
            current.Corpus,
            current.CorpusFingerprint,
            current.Rows.Select(PreparedTicketPublicationEnrichmentContract.FingerprintRow).ToArray(),
            current.ProtectedContentFingerprint,
            current.Grouping.Select(partition => partition.Fingerprint).ToArray(),
            current.RetainedGroupingFingerprint,
            current.ZulipReferences,
            comparison.AdditionalTicketKeys);
        _ = PreparedTicketPublicationEnrichmentContract.SerializeInput(input);
        return input;
    }

    /// <summary>
    /// Rechecks the frozen current graph before/after apply and on recovery.
    /// Additions are limited to this new run's provenance/receipts and previously
    /// missing hydration for a frozen accepted Zulip association.
    /// </summary>
    public static void ValidateFrozen(
        PreparedTicketPublicationEnrichmentInput input,
        PreparedTicketPublicationProtectedInventory current,
        string? maintenanceRunId = null)
    {
        _ = PreparedTicketPublicationEnrichmentContract.SerializeInput(input);
        if (input.CorpusFingerprint != current.CorpusFingerprint ||
            input.RetainedGroupingFingerprint != current.RetainedGroupingFingerprint)
        {
            throw Refuse("frozen-protection-drift", "The frozen accepted corpus or retained grouping changed.");
        }
        Dictionary<(string Table, string Scope, string Key), PreparedTicketPublicationProtectedRowFingerprint> frozen =
            input.ProtectedRows.ToDictionary(row => (row.Table, row.Scope, row.Key));
        Dictionary<(string Table, string Scope, string Key), PreparedTicketPublicationProtectedRow> currentRows =
            current.Rows.ToDictionary(Coordinate);
        foreach (((string Table, string Scope, string Key) coordinate, PreparedTicketPublicationProtectedRowFingerprint row) in frozen)
        {
            if (!currentRows.TryGetValue(coordinate, out PreparedTicketPublicationProtectedRow? retained) ||
                PreparedTicketPublicationEnrichmentContract.FingerprintRow(retained) != row)
            {
                throw Refuse("frozen-protection-drift", $"Frozen protected output changed in '{row.Table}'.");
            }
        }
        foreach (PreparedTicketPublicationProtectedRow row in current.Rows)
        {
            if (frozen.ContainsKey(Coordinate(row)))
            {
                continue;
            }
            bool newRunRow = !string.IsNullOrWhiteSpace(maintenanceRunId) &&
                row.Scope == maintenanceRunId &&
                row.Table is "authoring_runs" or "authoring_run_items" or
                    "authoring_run_input_provenance" or
                    "prepared_ticket_publication_refresh_receipts" or
                    "prepared_ticket_partition_certifications";
            if (!newRunRow && !IsAllowedNewZulipRow(row, input.ZulipReferences, current.ZulipReferences))
            {
                throw Refuse("frozen-protection-drift", $"Frozen protected output gained a row in '{row.Table}'.");
            }
        }
    }

    private static void EnsureOriginalRows(
        IReadOnlyList<PreparedTicketPublicationProtectedRow> original,
        IReadOnlyList<PreparedTicketPublicationProtectedRow> current)
    {
        Dictionary<(string Table, string Scope, string Key), PreparedTicketPublicationProtectedRow> currentRows =
            current.ToDictionary(Coordinate);
        foreach (PreparedTicketPublicationProtectedRow row in original)
        {
            if (!currentRows.TryGetValue(Coordinate(row), out PreparedTicketPublicationProtectedRow? retained))
            {
                throw Refuse("original-output-changed", $"Original protected output is missing from '{row.Table}'.");
            }
            Dictionary<string, PreparedTicketPublicationProtectedValue> values =
                retained.Values.ToDictionary(value => value.Column, StringComparer.Ordinal);
            if (row.Values.Any(value =>
                    !values.TryGetValue(value.Column, out PreparedTicketPublicationProtectedValue? actual) ||
                    actual != value))
            {
                throw Refuse("original-output-changed", $"Original protected values changed in '{row.Table}'.");
            }
        }
    }

    private static (string Table, string Scope, string Key) Coordinate(PreparedTicketPublicationProtectedRow row)
        => (row.Table, row.Scope, row.Key);

    private static bool IsAllowedNewZulipRow(
        PreparedTicketPublicationProtectedRow row,
        IReadOnlyList<PreparedTicketPublicationZulipReference> frozen,
        IReadOnlyList<PreparedTicketPublicationZulipReference> current)
    {
        if (row.Table != "prepared_zulip_hydration")
        {
            return false;
        }
        string? id = row.Values.Single(value => value.Column == "Id").Value;
        string? ticket = row.Values.Single(value => value.Column == "TicketKey").Value;
        string? reference = row.Values.Single(value => value.Column == "ZulipThreadId").Value;
        PreparedTicketPublicationZulipReference? accepted = frozen.SingleOrDefault(value =>
            string.Equals(value.TicketKey, ticket, StringComparison.OrdinalIgnoreCase) && value.Reference == reference);
        return accepted is { HydrationId: null, HydrationRowId: null } &&
            current.Any(value =>
                value.AssociationId == accepted.AssociationId &&
                string.Equals(value.TicketKey, ticket, StringComparison.OrdinalIgnoreCase) &&
                value.Reference == reference && value.HydrationId == id);
    }

    private static async Task<Dictionary<string, List<DataRow>>> ReadTablesAsync(
        SqliteConnection connection,
        AuthoringSnapshotSchemaCatalog catalog,
        bool snapshot,
        CancellationToken ct)
    {
        ValidateCatalog(catalog);
        Dictionary<string, string[]> actual = new(StringComparer.Ordinal);
        List<string> tableNames = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT type, name FROM sqlite_master
                WHERE type IN ('table', 'view', 'trigger')
                  AND substr(name, 1, 7) <> 'sqlite_'
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string type = reader.GetString(0);
                if (snapshot && type != "table")
                {
                    throw Refuse("invalid-protection-catalog", "Snapshot catalogs cannot contain views or triggers.");
                }
                if (type == "table")
                {
                    tableNames.Add(reader.GetString(1));
                }
            }
        }
        string[] required = catalog.Tables.Select(table => table.Name)
            .Where(table => snapshot || table != "authoring_snapshot_provenance")
            .Concat(snapshot ? [] : PrivateGraphTables).ToArray();
        if (required.Except(tableNames, StringComparer.Ordinal).Any() ||
            snapshot && tableNames.Except(required, StringComparer.Ordinal).Any())
        {
            throw Refuse("invalid-protection-catalog", "The database does not match the supported publication table catalog.");
        }
        foreach (string table in required)
        {
            List<string> columns = [];
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_xinfo({Quote(table)})";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string column = reader.GetString(1);
                _ = ClassifyColumn(table, column);
                columns.Add(column);
            }
            AuthoringSnapshotTableSchema? schema = catalog.Tables.SingleOrDefault(value => value.Name == table);
            if (schema is not null &&
                (schema.Columns.Except(columns, StringComparer.Ordinal).Any() ||
                 snapshot && columns.Except(schema.Columns, StringComparer.Ordinal).Any()))
            {
                throw Refuse("invalid-protection-catalog", $"Table '{table}' does not match its supported snapshot columns.");
            }
            actual.Add(table, columns.ToArray());
        }

        Dictionary<string, List<DataRow>> tables = new(StringComparer.Ordinal);
        foreach ((string table, string[] columns) in actual)
        {
            List<DataRow> rows = [];
            await using SqliteCommand command = connection.CreateCommand();
            string textBytes = string.Join(", ", columns.Select(column =>
                $"CASE WHEN typeof({Quote(column)}) = 'text' THEN CAST({Quote(column)} AS BLOB) END"));
            command.CommandText =
                $"SELECT {string.Join(", ", columns.Select(Quote))}, {textBytes} FROM {Quote(table)}";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                Dictionary<string, PreparedTicketPublicationProtectedValue> values = new(StringComparer.Ordinal);
                for (int index = 0; index < columns.Length; index++)
                {
                    object value = reader.GetValue(index);
                    if (value is string text &&
                        !Encoding.UTF8.GetBytes(text).AsSpan().SequenceEqual(
                            reader.GetFieldValue<byte[]>(index + columns.Length)))
                    {
                        throw Refuse(
                            "invalid-accepted-graph",
                            $"Protected text in '{table}.{columns[index]}' is not losslessly readable as UTF-8.");
                    }
                    values.Add(columns[index], ToValue(columns[index], value));
                }
                rows.Add(new DataRow(table, values));
            }
            tables.Add(table, rows);
        }
        return tables;
    }

    private static IReadOnlyList<PreparedTicketPublicationCorpusItem> ReadSnapshotCorpus(
        Dictionary<string, List<DataRow>> tables)
    {
        List<PreparedTicketPublicationCorpusItem> corpus = [];
        Dictionary<string, DataRow> runs = UniqueBy(tables["authoring_runs"], "Id");
        Dictionary<string, DataRow> items = UniqueBy(tables["authoring_run_items"], "Id");
        _ = UniqueBy(tables["authoring_result_receipts"], "Id");
        HashSet<string> ticketKeys = new(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow ticket in tables["prepared_tickets"])
        {
            string key = ticket.Text("Key");
            if (!ticketKeys.Add(key))
            {
                throw Refuse("invalid-accepted-graph", "The snapshot has duplicate prepared-ticket identities.");
            }
            DataRow[] accepted = tables["authoring_result_receipts"].Where(receipt =>
                string.Equals(receipt.Text("BusinessKey"), key, StringComparison.OrdinalIgnoreCase) &&
                items.TryGetValue(receipt.Text("RunItemId"), out DataRow? item) &&
                item.OptionalText("AcceptedReceiptId") == receipt.Text("Id") &&
                item.Text("RunId") == receipt.Text("RunId") &&
                item.Text("ItemKind").Equals("fhir", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.Text("BusinessKey"), key, StringComparison.OrdinalIgnoreCase) &&
                item.Text("ExpectedSourceRevision") == receipt.Text("ExpectedSourceRevision") &&
                receipt.Text("ObservedSourceRevision") == receipt.Text("ExpectedSourceRevision") &&
                item.Text("Status") is AuthoringStatusValues.Items.Complete or AuthoringStatusValues.Items.Superseded &&
                runs.TryGetValue(receipt.Text("RunId"), out DataRow? run) &&
                run.Text("ProcessorKind") == PreparedTicketSnapshotSchemaV3.ProcessorKind &&
                run.Integer("AuthoringEpoch") == receipt.Integer("AuthoringEpoch")).ToArray();
            if (accepted.Length != 1)
            {
                throw Refuse(
                    "invalid-accepted-graph",
                    $"Snapshot ticket '{key}' has no unique accepted receipt coordinate.");
            }
            DataRow receipt = accepted[0];
            DataRow sourceItem = items[receipt.Text("RunItemId")];
            corpus.Add(new(
                key, receipt.Text("Id"), sourceItem.Text("Id"), receipt.Text("RunId"),
                sourceItem.Text("ItemKind"), sourceItem.Text("ExpectedSourceRevision")));
        }
        _ = PreparedTicketPublicationContract.ComputeCorpusFingerprint(corpus);
        return corpus.AsReadOnly();
    }

    private static PreparedTicketPublicationProtectedInventory BuildInventory(
        int schemaVersion,
        IReadOnlyList<PreparedTicketPublicationCorpusItem> corpus,
        Dictionary<string, List<DataRow>> tables,
        bool snapshot)
    {
        _ = PreparedTicketPublicationContract.ComputeCorpusFingerprint(corpus);
        foreach ((string table, List<DataRow> rows) in tables)
        {
            if (rows.Count == 0)
            {
                continue;
            }
            if (rows[0].Values.ContainsKey("Id"))
            {
                _ = UniqueBy(rows, "Id");
            }
            if (rows[0].Values.ContainsKey("RowId") &&
                rows.Select(row => row.Integer("RowId")).Distinct().Count() != rows.Count)
            {
                throw Refuse("invalid-accepted-graph", $"Table '{table}' contains duplicate row IDs.");
            }
        }
        HashSet<string> tickets = corpus.Select(item => item.TicketKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string table in TicketTables.Where(tables.ContainsKey))
        {
            string scopeColumn = Definitions[table].ScopeColumn;
            if (snapshot && tables[table].Any(row => !tickets.Contains(row.Text(scopeColumn))))
            {
                throw Refuse("invalid-accepted-graph", $"Snapshot table '{table}' contains output outside the accepted corpus.");
            }
            tables[table] = tables[table].Where(row => tickets.Contains(row.Text(scopeColumn))).ToList();
        }

        List<PreparedTicketPublicationZulipReference> references = [];
        HashSet<(string Ticket, string Reference)> acceptedZulip = [];
        foreach (DataRow association in tables["prepared_ticket_related_zulip"])
        {
            string ticket = association.Text("TicketKey");
            string reference = association.Text("ZulipThreadId");
            if (!acceptedZulip.Add((ticket.ToUpperInvariant(), reference)))
            {
                throw Refuse("invalid-accepted-graph", "Accepted Zulip associations are ambiguous.");
            }
            DataRow[] hydration = tables["prepared_zulip_hydration"]
                .Where(row => row.Text("TicketKey").Equals(ticket, StringComparison.OrdinalIgnoreCase) &&
                    row.Text("ZulipThreadId") == reference).ToArray();
            if (hydration.Length > 1)
            {
                throw Refuse("invalid-accepted-graph", "Accepted Zulip hydration coordinates are ambiguous.");
            }
            references.Add(new(
                association.Text("Id"), ticket, reference,
                hydration.SingleOrDefault()?.Text("Id"),
                hydration.SingleOrDefault()?.Integer("RowId")));
        }

        // A maintenance run may update metadata, but cannot manufacture the
        // parent or self-Jira structural rows which authoring/hydration supplied.
        foreach (string ticket in tickets)
        {
            if (tables["prepared_ticket_hydration"].Count(row =>
                    row.Text("TicketKey").Equals(ticket, StringComparison.OrdinalIgnoreCase)) != 1 ||
                tables["prepared_jira_hydration"].Count(row =>
                    row.Text("TicketKey").Equals(ticket, StringComparison.OrdinalIgnoreCase) &&
                    IsSelfJira(row)) != 1)
            {
                throw Refuse("invalid-accepted-graph", $"Ticket '{ticket}' has no unique parent/self Jira hydration.");
            }
        }

        if (!snapshot)
        {
            HashSet<string> historicalRuns = tables["authoring_result_receipts"]
                .Select(row => row.Text("RunId"))
                .Concat(tables["prepared_ticket_partition_receipts"].Select(row => row.Text("RunId")))
                .Concat(tables["authoring_run_input_provenance"].Select(row => row.Text("RunId")))
                .Concat(tables["prepared_ticket_publication_refresh_receipts"].Select(row => row.Text("RunId")))
                .Concat(tables["prepared_ticket_partition_certifications"].Select(row => row.Text("RunId")))
                .ToHashSet(StringComparer.Ordinal);
            tables["authoring_runs"] = tables["authoring_runs"]
                .Where(row => historicalRuns.Contains(row.Text("Id"))).ToList();
            tables["authoring_run_items"] = tables["authoring_run_items"]
                .Where(row => historicalRuns.Contains(row.Text("RunId"))).ToList();
            HashSet<string> itemIds = tables["authoring_run_items"]
                .Select(row => row.Text("Id")).ToHashSet(StringComparer.Ordinal);
            tables["prepared_ticket_run_item_partitions"] = tables["prepared_ticket_run_item_partitions"]
                .Where(row => itemIds.Contains(row.Text("RunItemId"))).ToList();
        }

        List<PreparedTicketPublicationProtectedRow> protectedRows = [];
        foreach ((string table, List<DataRow> rows) in tables)
        {
            if (GroupingTables.Contains(table))
            {
                continue;
            }
            foreach (DataRow row in rows)
            {
                PreparedTicketPublicationProtectedValue[] values = row.Values.Values
                    .Where(value => IsProtected(row, value.Column, acceptedZulip, snapshot)).ToArray();
                if (values.Length != 0)
                {
                    protectedRows.Add(ToProtectedRow(row, values));
                }
            }
        }
        // This also refuses duplicate semantic IDs, even in a snapshot whose
        // sanitizer had to rebuild a table without its original unique indexes.
        _ = PreparedTicketPublicationEnrichmentContract.ComputeProtectedContentFingerprint(protectedRows);
        IReadOnlyList<PreparedTicketPublicationProtectedGrouping> grouping =
            ReadGrouping(corpus, tables);
        return new(schemaVersion, corpus, protectedRows.AsReadOnly(), grouping, references.AsReadOnly());
    }

    private static bool IsProtected(
        DataRow row,
        string column,
        HashSet<(string Ticket, string Reference)> acceptedZulip,
        bool snapshot)
        => ClassifyColumn(row.Table, column) switch
        {
            PreparedTicketPublicationColumnProtection.Protected => true,
            PreparedTicketPublicationColumnProtection.SelfJiraPublicationMetadata => !IsSelfJira(row),
            PreparedTicketPublicationColumnProtection.AcceptedZulipPublicationMetadata =>
                !acceptedZulip.Contains((row.Text("TicketKey").ToUpperInvariant(), row.Text("ZulipThreadId"))),
            PreparedTicketPublicationColumnProtection.SnapshotLifecycle =>
                !snapshot && row.Table == "authoring_runs",
            PreparedTicketPublicationColumnProtection.PublicationMetadata or
                PreparedTicketPublicationColumnProtection.Operational => false,
            _ => throw new InvalidOperationException("Unknown publication column protection classification."),
        };

    private static bool IsSelfJira(DataRow row)
        => row.Text("TicketKey").Equals(row.Text("JiraKey"), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<PreparedTicketPublicationProtectedGrouping> ReadGrouping(
        IReadOnlyList<PreparedTicketPublicationCorpusItem> corpus,
        Dictionary<string, List<DataRow>> tables)
    {
        Dictionary<string, DataRow> selfJira = new(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow row in tables["prepared_jira_hydration"].Where(IsSelfJira))
        {
            if (!selfJira.TryAdd(row.Text("TicketKey"), row))
            {
                throw Refuse("invalid-accepted-graph", "Grouping has ambiguous self-Jira coordinates.");
            }
        }
        Dictionary<long, DataRow> topics = UniqueIntegerRows(tables["prepared_ticket_topics"]);
        Dictionary<long, DataRow> groups = UniqueIntegerRows(tables["prepared_ticket_topic_groups"]);
        _ = UniqueIntegerRows(tables["prepared_ticket_topic_members"]);
        foreach (DataRow group in groups.Values)
        {
            if (!topics.ContainsKey(group.Integer("TopicRowId")))
            {
                throw Refuse("invalid-accepted-graph", "Grouping contains an orphaned linked group.");
            }
        }
        HashSet<string> seenMembers = new(StringComparer.OrdinalIgnoreCase);
        foreach (DataRow member in tables["prepared_ticket_topic_members"])
        {
            if (!topics.TryGetValue(member.Integer("TopicRowId"), out DataRow? topic) ||
                member.Values["TopicGroupRowId"].StorageClass != "null" &&
                (!groups.TryGetValue(member.Integer("TopicGroupRowId"), out DataRow? group) ||
                 group.Integer("TopicRowId") != topic.Integer("RowId")) ||
                !selfJira.TryGetValue(member.Text("TicketKey"), out DataRow? self) ||
                PartitionKey(topic) != PartitionKey(self) ||
                !seenMembers.Add(member.Text("TicketKey")))
            {
                throw Refuse("invalid-accepted-graph", "Grouping contains orphaned, duplicate, or out-of-partition members.");
            }
        }
        string[] partitionKeys = selfJira.Values.Select(PartitionKey)
            .Concat(topics.Values.Select(PartitionKey))
            .Distinct(StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        List<PreparedTicketPublicationProtectedGrouping> result = [];
        foreach (string partitionKey in partitionKeys)
        {
            string[] coordinates = partitionKey.Split('\u001f');
            DataRow[] partitionTopics = topics.Values.Where(row => PartitionKey(row) == partitionKey)
                .OrderBy(row => row.Integer("RowId")).ToArray();
            DataRow[] partitionSelf = selfJira.Values.Where(row => PartitionKey(row) == partitionKey).ToArray();
            string[] displayValues = (partitionTopics.Length == 0
                    ? partitionSelf.Select(row => row.Text("WorkGroup").Trim())
                    : partitionTopics.Select(row => row.Text("WorkGroupDisplay")))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (displayValues.Length != 1 || string.IsNullOrWhiteSpace(displayValues[0]))
            {
                throw Refuse("invalid-accepted-graph", "Grouping has no unique workgroup display value.");
            }
            PreparedTicketGroupingPayload payload = new()
            {
                WorkGroupClean = coordinates[0],
                WorkGroupDisplay = displayValues[0],
                Specification = coordinates[1],
                Type = coordinates[2],
                Topics = [],
            };
            List<PreparedTicketPublicationProtectedRow> rows = [];
            foreach (DataRow topic in partitionTopics)
            {
                rows.Add(ToProtectedRow(topic, scope: partitionKey));
                DataRow[] members = tables["prepared_ticket_topic_members"]
                    .Where(row => row.Integer("TopicRowId") == topic.Integer("RowId")).ToArray();
                if (members.Length < 2)
                {
                    throw Refuse("invalid-accepted-graph", "Grouping contains a topic with fewer than two accepted members.");
                }
                rows.AddRange(members.Select(row => ToProtectedRow(row, scope: partitionKey)));
                PreparedTicketTopicPayload topicPayload = new()
                {
                    ShortDescription = topic.Text("ShortDescription"),
                    LongerDescription = topic.OptionalText("LongerDescription") ?? string.Empty,
                    RenderOrderHint = topic.Values["RenderOrderHint"].StorageClass == "null"
                        ? null : checked((int)topic.Integer("RenderOrderHint")),
                    LinkedTicketGroups = [],
                    RemainingTicketKeys = [],
                };
                DataRow[] topicGroups = groups.Values
                    .Where(row => row.Integer("TopicRowId") == topic.Integer("RowId"))
                    .OrderBy(row => row.Integer("OrderInTopic")).ThenBy(row => row.Integer("RowId")).ToArray();
                for (int index = 0; index < topicGroups.Length; index++)
                {
                    DataRow group = topicGroups[index];
                    if (group.Integer("OrderInTopic") != index)
                    {
                        throw Refuse("invalid-accepted-graph", "Grouping has non-canonical linked-group order.");
                    }
                    rows.Add(ToProtectedRow(group, scope: partitionKey));
                    DataRow[] groupMembers = members.Where(row =>
                        row.Values["TopicGroupRowId"].StorageClass != "null" &&
                        row.Integer("TopicGroupRowId") == group.Integer("RowId")).ToArray();
                    if (groupMembers.Length < 2 ||
                        !groupMembers.Any(row => row.Text("TicketKey") == group.Text("FirstTicketKey")) ||
                        groupMembers.Select(row => row.Integer("OrderInContainer")).Distinct().Count() != groupMembers.Length)
                    {
                        throw Refuse("invalid-accepted-graph", "Grouping has invalid linked-group membership.");
                    }
                    topicPayload.LinkedTicketGroups.Add(new PreparedTicketTopicGroupPayload
                    {
                        FirstTicketKey = group.Text("FirstTicketKey"),
                        Rationale = group.OptionalText("Rationale") ?? string.Empty,
                        Members = groupMembers.Select(row => new PreparedTicketTopicGroupMemberPayload
                        {
                            TicketKey = row.Text("TicketKey"),
                            Order = checked((int)row.Integer("OrderInContainer")),
                        }).ToList(),
                    });
                }
                DataRow[] remaining = members.Where(row => row.Values["TopicGroupRowId"].StorageClass == "null")
                    .OrderBy(row => row.Integer("OrderInContainer"))
                    .ThenBy(row => row.Text("TicketKey"), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(row => row.Text("TicketKey"), StringComparer.Ordinal).ToArray();
                for (int index = 0; index < remaining.Length; index++)
                {
                    if (remaining[index].Integer("OrderInContainer") != index)
                    {
                        throw Refuse("invalid-accepted-graph", "Grouping has non-canonical remaining-member order.");
                    }
                    topicPayload.RemainingTicketKeys.Add(remaining[index].Text("TicketKey"));
                }
                payload.Topics.Add(topicPayload);
            }
            PreparedTicketPublicationCorpusItem[] partitionCorpus = corpus
                .Where(item => PartitionKey(selfJira[item.TicketKey]) == partitionKey).ToArray();
            PreparedTicketPublicationProtectedGroupingFingerprint fingerprint = new(
                partitionKey,
                PreparedTicketPublicationContract.ComputeCorpusFingerprint(partitionCorpus),
                PreparedTicketPublicationContract.ComputeGroupingPartitionFingerprint(payload),
                PreparedTicketPublicationEnrichmentContract.ComputeProtectedContentFingerprint(rows));
            result.Add(new(partitionKey, partitionCorpus, rows.AsReadOnly(), fingerprint));
        }
        return result.AsReadOnly();
    }

    private static Dictionary<long, DataRow> UniqueIntegerRows(IEnumerable<DataRow> rows)
    {
        Dictionary<long, DataRow> unique = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (DataRow row in rows)
        {
            if (!unique.TryAdd(row.Integer("RowId"), row) || !ids.Add(row.Text("Id")))
            {
                throw Refuse("invalid-accepted-graph", $"Table '{row.Table}' has duplicate grouping IDs.");
            }
        }
        return unique;
    }

    private static string PartitionKey(DataRow row)
    {
        string workGroup = row.OptionalText("WorkGroupClean")?.Trim() ?? "unattributed";
        string specification = row.OptionalText("Specification")?.Trim() ?? "Unspecified";
        string type = row.OptionalText("Type")?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(workGroup))
        {
            workGroup = "unattributed";
        }
        if (string.IsNullOrEmpty(specification))
        {
            specification = "Unspecified";
        }
        if (string.IsNullOrWhiteSpace(type) ||
            new[] { workGroup, specification, type }.Any(value => value.Contains('\u001f')))
        {
            throw Refuse("invalid-accepted-graph", "Grouping has invalid partition coordinates.");
        }
        return PreparerDatabase.GetPartitionKey(workGroup, specification, type);
    }

    private static PreparedTicketPublicationProtectedRow ToProtectedRow(
        DataRow row,
        IReadOnlyList<PreparedTicketPublicationProtectedValue>? values = null,
        string? scope = null)
    {
        TableDefinition definition = Definitions[row.Table];
        string key = JsonSerializer.Serialize(definition.KeyColumns.Select(column => row.Values[column]));
        return new(row.Table, scope ?? row.Text(definition.ScopeColumn), key, values ?? row.Values.Values.ToArray());
    }

    private static PreparedTicketPublicationProtectedValue ToValue(string column, object value)
        => value switch
        {
            DBNull => new(column, "null", null),
            string text => new(column, "text", text),
            long integer => new(column, "integer", integer.ToString(CultureInfo.InvariantCulture)),
            double real => new(column, "real", real.ToString("R", CultureInfo.InvariantCulture)),
            byte[] bytes => new(column, "blob", Convert.ToBase64String(bytes)),
            _ => throw Refuse("invalid-accepted-graph", $"Unsupported SQLite storage value in '{column}'."),
        };

    private sealed record DataRow(
        string Table,
        Dictionary<string, PreparedTicketPublicationProtectedValue> Values)
    {
        public string Text(string column)
            => OptionalText(column)
                ?? throw Refuse("invalid-accepted-graph", $"Required coordinate '{Table}.{column}' is null.");

        public string? OptionalText(string column)
        {
            PreparedTicketPublicationProtectedValue value = Values[column];
            return value.StorageClass switch
            {
                "text" => value.Value,
                "null" => null,
                _ => throw Refuse("invalid-accepted-graph", $"Coordinate '{Table}.{column}' is not text."),
            };
        }

        public long Integer(string column)
        {
            PreparedTicketPublicationProtectedValue value = Values[column];
            return value.StorageClass == "integer" &&
                long.TryParse(value.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer)
                ? integer
                : throw Refuse("invalid-accepted-graph", $"Coordinate '{Table}.{column}' is not an integer.");
        }
    }

    private static Dictionary<string, DataRow> UniqueBy(IEnumerable<DataRow> rows, string column)
    {
        Dictionary<string, DataRow> unique = new(StringComparer.Ordinal);
        foreach (DataRow row in rows)
        {
            string coordinate = row.Text(column);
            if (string.IsNullOrWhiteSpace(coordinate) || !unique.TryAdd(coordinate, row))
            {
                throw Refuse("invalid-accepted-graph", $"Table '{row.Table}' contains empty or duplicate '{column}' coordinates.");
            }
        }
        return unique;
    }

    private static string Quote(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static PreparedTicketPublicationProtectionException Refuse(string code, string detail)
        => new(code, detail);

    private static IReadOnlyDictionary<string, TableDefinition> CreateDefinitions()
    {
        Dictionary<string, TableDefinition> tables = new(StringComparer.Ordinal);
        Add("prepared_tickets", "Key", ["Id"],
            """
            RowId Id Key RequestSummary CommentSummary LinkedTicketSummary RelatedTicketSummary
            RelatedZulipSummary RelatedGitHubSummary ExistingProposed ProposalA ProposalAJustification
            ProposalAImpact ProposalB ProposalBJustification ProposalBImpact ProposalC
            ProposalCJustification Recommendation RecommendationJustification SavedAt
            """);
        Add("prepared_ticket_repos", "TicketKey", ["Id"], "RowId Id TicketKey Repo RepoCategory Justification");
        Add("prepared_ticket_related_jira", "TicketKey", ["Id"], "RowId Id TicketKey AssociatedTicketKey LinkType Justification");
        Add("prepared_ticket_related_zulip", "TicketKey", ["Id"], "RowId Id TicketKey ZulipThreadId Justification");
        Add("prepared_ticket_related_github", "TicketKey", ["Id"], "RowId Id TicketKey GitHubItemId Justification");
        Add("prepared_ticket_hydration", "TicketKey", ["Id"],
            """
            RowId Id TicketKey Priority Resolution ResolutionDescriptionPlain Specification RaisedInVersion
            SelectedBallot ChangeCategory Impact Labels CommentCount DescriptionPlain DescriptionHtml
            ResolutionDescriptionHtml CreatedAt RelatedArtifactsRaw RelatedPagesRaw HydrationStatus HydrationReason
            """,
            publication: "Reporter Assignee SourceProject SourceLastSuccessfulRefreshAt SourceContentRevision HydratedAt PublicDisplayNamePolicyVersion");
        Add("prepared_jira_hydration", "TicketKey", ["Id"],
            """
            RowId Id TicketKey JiraKey Title Status Type Priority Resolution ResolutionDescriptionPlain
            WorkGroup WorkGroupClean Specification Url DescriptionHtml ResolutionDescriptionHtml
            CreatedAt RelatedArtifactsRaw RelatedPagesRaw HydratedAt HydrationStatus HydrationReason
            """,
            selfJira: "Reporter Assignee UpdatedAt PublicDisplayNamePolicyVersion");
        Add("prepared_zulip_hydration", "TicketKey", ["Id"], "RowId Id TicketKey ZulipThreadId",
            acceptedZulip: "StreamId StreamName Topic MessageCount FirstMessageAt LastMessageAt FirstMessageExcerpt Url HydratedAt HydrationStatus HydrationReason");
        Add("prepared_github_hydration", "TicketKey", ["Id"],
            "RowId Id TicketKey GitHubItemId Owner Repo Number Path Title State IsPullRequest Labels UpdatedAt Url HydratedAt HydrationStatus HydrationReason");
        Add("prepared_repo_hydration", "TicketKey", ["Id"],
            "RowId Id TicketKey Repo Description WorkGroup Specification CategoryDetail Url HydratedAt HydrationStatus HydrationReason");
        Add("prepared_ticket_jira_xref", "TicketKey", ["Id"], "RowId Id TicketKey JiraKey Source");
        Add("prepared_ticket_jira_content", "TicketKey", ["RowId"], "RowId TicketKey DescriptionHtml ResolutionDescriptionHtml");
        Add("prepared_ticket_artifacts", "TicketKey", ["RowId"], "RowId TicketKey Value");
        Add("prepared_ticket_pages", "TicketKey", ["RowId"], "RowId TicketKey Value");
        Add("prepared_ticket_in_person_requesters", "TicketKey", ["RowId"], "",
            publication: "RowId TicketKey DisplayName PublicDisplayNamePolicyVersion");
        Add("prepared_ticket_topics", "WorkGroupClean", ["Id"],
            "RowId Id WorkGroupClean WorkGroupDisplay Specification Type ShortDescription LongerDescription RenderOrderHint SavedAt");
        Add("prepared_ticket_topic_groups", "FirstTicketKey", ["Id"],
            "RowId Id TopicRowId FirstTicketKey Rationale OrderInTopic SavedAt");
        Add("prepared_ticket_topic_members", "TicketKey", ["Id"],
            "RowId Id TopicRowId TopicGroupRowId TicketKey OrderInContainer");
        Add("prepared_ticket_partition_receipts", "RunId", ["RunId", "PartitionKey"],
            "RunId StageId PartitionKey InputFingerprint OutputFingerprint TopicRows TopicGroupRows MemberRows PersistedAt");
        Add("jira_review_workgroups", "Code", ["Code"], "RowId Code Name NameClean UpdatedAt");

        // Snapshot creation precedes completion of its producing run. These
        // three lifecycle fields necessarily differ from the completed live
        // record. Freeze their current values too once admission is complete.
        Add("authoring_runs", "Id", ["Id"],
            "RowId Id ProcessorKind AuthoringEpoch Purpose SourceRunId DatabaseOnly TotalItems CreatedAt StartedAt RequestJson",
            lifecycle: "Status CompletedAt SnapshotId",
            operational: "Error");
        Add("authoring_run_items", "RunId", ["Id"],
            "RowId Id RunId BusinessKey ItemKind ExpectedSourceRevision Status AcceptedReceiptId CurrentOperationId AttemptCount CreatedAt StartedAt CompletedAt",
            operational: "PostPersistenceLeaseId PostPersistenceLeaseAcquiredAt Error");
        Add("authoring_result_receipts", "RunId", ["Id"],
            "RowId Id OperationId RunId RunItemId BusinessKey ContentHash ExpectedSourceRevision ObservedSourceRevision AuthoringEpoch PersistedAt");
        Add("authoring_run_input_provenance", "RunId", ["RunId", "Source"],
            "RowId RunId Source LatestSuccessfulRefreshAt ContentRevision CapturedAt");
        Add("authoring_snapshot_provenance", "RunId", ["SnapshotId"], "",
            lifecycle: "SnapshotId ProcessorKind RunId AuthoringEpoch Sequence SchemaVersion ItemCount ReceiptCount TableCountsJson CreatedAt");

        // These private graph records never enter a public snapshot. Capture
        // existing values in current state, permitting only new-run additions.
        Add("prepared_ticket_authoring_state", "TicketKey", ["TicketKey"],
            "TicketKey Classification GraphHash ReceiptContentHash RunId RunItemId OperationId UpdatedAt");
        Add("prepared_ticket_run_item_partitions", "RunItemId", ["RunItemId"],
            "RunItemId TicketKey WorkGroupClean WorkGroupDisplay Specification Type CapturedAt");
        Add("prepared_ticket_publication_refresh_receipts", "RunId", ["RunId", "StageId"],
            "RowId RunId StageId InputFingerprint CorpusFingerprint SourceLastSuccessfulRefreshAt SourceContentRevision PublicDisplayNamePolicyVersion AppliedAt");
        Add("prepared_ticket_partition_certifications", "RunId", ["RunId", "PartitionKey"],
            "RowId RunId StageId PartitionKey InputFingerprint SourceRunId SourceStageId SourceInputFingerprint OutputFingerprint CertifiedAt");
        return new ReadOnlyDictionary<string, TableDefinition>(tables);

        void Add(
            string table, string scopeColumn, string[] keyColumns, string protectedColumns,
            string publication = "", string selfJira = "", string acceptedZulip = "",
            string lifecycle = "", string operational = "")
        {
            Dictionary<string, PreparedTicketPublicationColumnProtection> columns = new(StringComparer.Ordinal);
            Classify(protectedColumns, PreparedTicketPublicationColumnProtection.Protected);
            Classify(publication, PreparedTicketPublicationColumnProtection.PublicationMetadata);
            Classify(selfJira, PreparedTicketPublicationColumnProtection.SelfJiraPublicationMetadata);
            Classify(acceptedZulip, PreparedTicketPublicationColumnProtection.AcceptedZulipPublicationMetadata);
            Classify(lifecycle, PreparedTicketPublicationColumnProtection.SnapshotLifecycle);
            Classify(operational, PreparedTicketPublicationColumnProtection.Operational);
            tables.Add(table, new(scopeColumn, keyColumns, new ReadOnlyDictionary<string, PreparedTicketPublicationColumnProtection>(columns)));

            void Classify(string names, PreparedTicketPublicationColumnProtection classification)
            {
                foreach (string column in names.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    columns.Add(column, classification);
                }
            }
        }
    }
}
