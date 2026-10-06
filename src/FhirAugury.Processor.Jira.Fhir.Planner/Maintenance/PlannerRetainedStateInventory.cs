using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FhirAugury.Processing.Common.Database;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Maintenance;

internal static class PlannerRetainedStateInventory
{
    internal static readonly string[] LegacyTables =
    [
        "jira_processing_source_tickets", "planned_tickets", "planned_ticket_repos",
        "planned_ticket_repo_changes", "planned_ticket_repo_impacts", "planned_ticket_change_validations",
        "planned_ticket_testing_considerations", "planned_ticket_open_questions", "planned_ticket_jira_content",
        "planned_ticket_hydration", "planned_jira_hydration", "planned_zulip_hydration",
        "planned_github_hydration", "planned_repo_hydration", "planned_ticket_related_jira",
        "planned_ticket_related_zulip", "planned_ticket_related_github", "planned_ticket_jira_xref",
        "planned_ticket_topics", "planned_ticket_topic_groups", "planned_ticket_topic_members",
        "planned_ticket_topic_repos", "jira_review_workgroups", "planner_schema_migrations",
        "planned_ticket_authoring_state", "planned_ticket_partition_receipts",
        "planned_ticket_run_item_partitions", "planned_ticket_applier_projection_pending",
        "authoring_processor_modes", "authoring_runs", "authoring_run_items", "authoring_run_attempts",
        "authoring_run_stages", "authoring_result_receipts", "authoring_mutation_fences",
        "authoring_run_input_provenance", "authoring_revalidation_lineage", "authoring_review_snapshots",
        "authoring_snapshot_provenance",
    ];

    internal static async Task<PlannerRetainedDatabaseInventory> WriteAsync(
        PlannerRetainedStatePaths paths, string artifactId, string safetyRelativePath,
        string inventoryDirectory, bool verifiedSnapshotProjection, CancellationToken ct)
    {
        string safetyPath = paths.InBundle(safetyRelativePath);
        foreach (string suffix in PlannerRetainedStatePaths.SqliteSuffixes)
        {
            if (PlannerRetainedStatePaths.TryAttributes(safetyPath + suffix, out _))
            {
                throw Refuse("safety-copy-not-standalone");
            }
        }
        using FileStream safety = PlannerRetainedStatePaths.OpenFrozenFile(safetyPath);
        string safetyHash = PlannerRetainedStateEvidence.Hash(safety, ct);
        await using SqliteConnection connection = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(safetyPath, ct);
        Execute(connection, "BEGIN;");
        try
        {
            PlannerRetainedRows schema = Rows("schema", """
                SELECT type, name, tbl_name, rootpage, sql
                FROM sqlite_schema ORDER BY type COLLATE BINARY, name COLLATE BINARY
                """);
            PlannerRetainedRows tableList = Rows("tables", """
                SELECT schema, name, type, ncol, wr, strict FROM pragma_table_list
                WHERE schema = 'main' ORDER BY name COLLATE BINARY
                """);
            PlannerRetainedRows databaseMetadata = Rows("database", """
                SELECT 'encoding' AS name, encoding AS value FROM pragma_encoding
                UNION ALL SELECT 'user_version', user_version FROM pragma_user_version
                UNION ALL SELECT 'application_id', application_id FROM pragma_application_id
                UNION ALL SELECT 'schema_version', schema_version FROM pragma_schema_version
                UNION ALL SELECT 'page_size', page_size FROM pragma_page_size
                UNION ALL SELECT 'auto_vacuum', auto_vacuum FROM pragma_auto_vacuum
                """);
            PlannerRetainedRows integrity = Rows("integrity", "PRAGMA integrity_check;");
            PlannerRetainedRows foreignKeys = Rows("foreign-keys", "PRAGMA foreign_key_check;");
            List<PlannerRetainedTable> tables = [];
            List<(string Name, string Kind, bool WithoutRowId, bool Strict)> names = [];
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT name, type, wr, strict FROM pragma_table_list
                    WHERE schema = 'main' AND type <> 'view' ORDER BY name COLLATE BINARY
                    """;
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    names.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2) != 0, reader.GetInt64(3) != 0));
                }
            }
            foreach ((string name, string kind, bool withoutRowId, bool strict) in names)
            {
                ct.ThrowIfCancellationRequested();
                string token = $"t{tables.Count:D6}";
                List<PlannerRetainedColumn> columns = ReadColumns(connection, name);
                List<PlannerRetainedRows> metadata =
                [
                    Rows(token + "-columns", $"PRAGMA table_xinfo({Quote(name)});"),
                    Rows(token + "-indexes", $"PRAGMA index_list({Quote(name)});"),
                    Rows(token + "-foreign-keys", $"PRAGMA foreign_key_list({Quote(name)});"),
                ];
                List<string> indexes = [];
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = $"PRAGMA index_list({Quote(name)});";
                    using SqliteDataReader reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        indexes.Add(reader.GetString(1));
                    }
                }
                foreach (string index in indexes.Order(StringComparer.Ordinal))
                {
                    metadata.Add(Rows(token + $"-index{metadata.Count:D6}", $"PRAGMA index_xinfo({Quote(index)});"));
                }
                if (kind == "virtual")
                {
                    using SqliteCommand command = connection.CreateCommand();
                    command.CommandText = "SELECT sql FROM sqlite_schema WHERE name = @name";
                    command.Parameters.AddWithValue("@name", name);
                    string sql = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
                    if (!Regex.IsMatch(sql, @"\bUSING\s+(fts5|rtree|rtree_i32)\s*\(",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    {
                        throw Refuse("unsupported-table-reader");
                    }
                }
                if (columns.Count == 0 || kind is not ("table" or "shadow" or "virtual"))
                {
                    throw Refuse("unsupported-table-reader");
                }

                // Hidden=1 virtual command/rank columns are not persistent values.
                // Generated columns (hidden=2/3) are values and are included.
                List<string> expressions = columns.Where(column => column.Hidden != 1)
                    .Select(column => Quote(column.Name)).ToList();
                string? rowId = null;
                string order;
                if (withoutRowId)
                {
                    string[] key = columns.Where(column => column.PrimaryKeyOrder != 0)
                        .OrderBy(column => column.PrimaryKeyOrder).Select(column => Quote(column.Name)).ToArray();
                    if (key.Length == 0)
                    {
                        throw Refuse("unsupported-table-reader");
                    }
                    // Use the table's actual primary-key ordering, so SQLite scans
                    // the b-tree rather than materializing/sorting database rows.
                    order = string.Join(", ", key);
                }
                else
                {
                    rowId = new[] { "_rowid_", "rowid", "oid" }.FirstOrDefault(candidate =>
                        !columns.Any(column => column.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)));
                    if (rowId is null)
                    {
                        throw Refuse("inaccessible-row-identity");
                    }
                    expressions.Insert(0, Quote(rowId));
                    order = Quote(rowId);
                }
                PlannerRetainedRows rows = Rows(token, $"SELECT {string.Join(", ", expressions)} FROM {Quote(name)} ORDER BY {order}");
                tables.Add(new(name, kind, withoutRowId, strict, rowId, columns, rows, metadata));
            }

            List<PlannerRetainedCheck> checks = ValidateRelationships(connection, tables, verifiedSnapshotProjection, ct);
            bool integrityOk;
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";
                using SqliteDataReader reader = command.ExecuteReader();
                integrityOk = reader.Read() && reader.GetString(0) == "ok" && !reader.Read();
            }
            checks.Add(new("integrity-check", integrityOk ? "passed" : "failed", integrityOk ? 0 : integrity.RowCount));
            checks.Add(new("foreign-key-check", foreignKeys.RowCount == 0 ? "passed" : "failed", foreignKeys.RowCount));
            PlannerRetainedDatabaseInventory inventory = new(
                artifactId, safetyRelativePath, safetyHash, safety.Length, schema, tableList, databaseMetadata,
                integrity, foreignKeys, tables,
                LegacyTables.Where(name => !tables.Any(table => Identifier(table.Name) == Identifier(name))).ToArray(), checks);
            PlannerRetainedStateEvidence.WriteJson(paths.Output(inventoryDirectory + "/inventory.json"), inventory);
            if (checks.Any(check => check.Status == "failed"))
            {
                throw Refuse(checks.Any(check => check.Name.StartsWith("processor-kind:", StringComparison.Ordinal) &&
                        check.Status == "failed")
                    ? "wrong-processor-kind" : "broken-required-relationship");
            }
            if (PlannerRetainedStateEvidence.Hash(safety, ct) != safetyHash)
            {
                throw new PlannerRetainedStateException("inventory", "safety-preservation-mismatch", 1);
            }
            return inventory;

            PlannerRetainedRows Rows(string name, string sql) =>
                WriteRows(connection, paths, inventoryDirectory + "/" + name + ".rows", sql, ct);
        }
        catch (SqliteException ex)
        {
            throw new PlannerRetainedStateException("inventory", "unsupported-table-reader", inner: ex);
        }
        finally
        {
            Execute(connection, "ROLLBACK;");
        }
    }

    // The native column spans belong to the current SQLite row. They are written
    // before sqlite3_step, in bounded chunks: neither a table nor a large BLOB is
    // copied into a managed collection. Every value has a type tag and byte length.
    internal static PlannerRetainedRows WriteRows(
        SqliteConnection connection, PlannerRetainedStatePaths paths, string relativePath, string sql, CancellationToken ct)
    {
        int result = raw.sqlite3_prepare_v2(connection.Handle ?? throw Refuse("closed-inventory-connection"),
            sql, out sqlite3_stmt statement);
        if (result != raw.SQLITE_OK)
        {
            statement?.Dispose();
            throw Refuse("unsupported-table-reader");
        }
        using (statement)
        {
            List<string> columns = [];
            int count = raw.sqlite3_column_count(statement);
            for (int column = 0; column < count; column++)
            {
                columns.Add(raw.sqlite3_column_name(statement, column).utf8_to_string());
            }
            long rows = 0;
            string path = paths.Output(relativePath);
            using (FileStream file = PlannerRetainedStatePaths.CreateFile(path))
            using (BinaryWriter writer = new(file, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("FAROWS1\0"u8);
                writer.Write(count);
                foreach (string column in columns)
                {
                    byte[] name = Encoding.UTF8.GetBytes(column);
                    writer.Write((long)name.Length);
                    writer.Write(name);
                }
                while ((result = raw.sqlite3_step(statement)) == raw.SQLITE_ROW)
                {
                    ct.ThrowIfCancellationRequested();
                    writer.Write((byte)1);
                    for (int column = 0; column < count; column++)
                    {
                        int type = raw.sqlite3_column_type(statement, column);
                        writer.Write((byte)type);
                        switch (type)
                        {
                            case raw.SQLITE_NULL:
                                writer.Write(0L);
                                break;
                            case raw.SQLITE_INTEGER:
                                writer.Write(8L);
                                writer.Write(raw.sqlite3_column_int64(statement, column));
                                break;
                            case raw.SQLITE_FLOAT:
                                writer.Write(8L);
                                writer.Write(BitConverter.DoubleToInt64Bits(raw.sqlite3_column_double(statement, column)));
                                break;
                            case raw.SQLITE_TEXT:
                            case raw.SQLITE_BLOB:
                                if (type == raw.SQLITE_TEXT)
                                {
                                    // Request UTF-8 without decoding/replacing invalid bytes or losing embedded NUL.
                                    _ = raw.sqlite3_column_text(statement, column);
                                }
                                ReadOnlySpan<byte> bytes = raw.sqlite3_column_blob(statement, column);
                                writer.Write((long)bytes.Length);
                                while (!bytes.IsEmpty)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    int length = Math.Min(bytes.Length, 65536);
                                    writer.Write(bytes[..length]);
                                    bytes = bytes[length..];
                                }
                                break;
                            default:
                                throw Refuse("unsupported-storage-class");
                        }
                    }
                    rows = checked(rows + 1);
                }
                if (result != raw.SQLITE_DONE)
                {
                    throw Refuse("unsupported-table-reader");
                }
                writer.Write((byte)0);
                writer.Write(rows);
                writer.Flush();
                file.Flush(flushToDisk: true);
            }
            return new(relativePath, rows, PlannerRetainedStateEvidence.Fingerprint(paths.Bundle, path, ct).Sha256, columns);
        }
    }

    internal static List<PlannerRetainedColumn> ReadColumns(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_xinfo({Quote(table)});";
        List<PlannerRetainedColumn> columns = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt64(3) != 0, reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5), reader.GetInt32(6)));
        }
        return columns;
    }

    private static List<PlannerRetainedCheck> ValidateRelationships(
        SqliteConnection connection, IReadOnlyList<PlannerRetainedTable> tables, bool snapshot, CancellationToken ct)
    {
        Dictionary<string, PlannerRetainedTable> known = tables.ToDictionary(table => Identifier(table.Name), StringComparer.Ordinal);
        List<PlannerRetainedCheck> checks = [];
        string[] kindTables =
        [
            "authoring_processor_modes", "authoring_runs", "authoring_result_receipts",
            "authoring_review_snapshots", "authoring_snapshot_provenance", "authoring_mutation_fences",
        ];
        foreach (string table in kindTables)
        {
            if (HasRows(table))
            {
                if (HasColumns(table, "ProcessorKind"))
                {
                    Check("processor-kind:" + table, table, ["ProcessorKind"], [],
                        "ProcessorKind IS NOT NULL AND (typeof(ProcessorKind) <> 'text' OR " +
                        "(ProcessorKind <> '' AND ProcessorKind COLLATE BINARY <> 'jira-fhir'))");
                }
                else if (table != "authoring_result_receipts")
                {
                    checks.Add(new("processor-kind:" + table, "failed", 1));
                }
            }
        }

        string[] issueChildren =
        [
            "planned_ticket_repos", "planned_ticket_repo_changes", "planned_ticket_repo_impacts",
            "planned_ticket_change_validations", "planned_ticket_testing_considerations",
            "planned_ticket_open_questions", "planned_ticket_hydration", "planned_jira_hydration",
            "planned_zulip_hydration", "planned_github_hydration", "planned_repo_hydration",
            "planned_ticket_related_jira", "planned_ticket_related_zulip", "planned_ticket_related_github",
            "planned_ticket_jira_xref",
        ];
        foreach (string table in issueChildren)
        {
            Parent(table, "planned_tickets", [("IssueKey", "Key")]);
        }
        Parent("planned_ticket_jira_content", "planned_tickets", [("TicketKey", "Key")]);
        Parent("planned_ticket_authoring_state", "planned_tickets", [("TicketKey", "Key")]);
        string[] repoChildren =
        [
            "planned_ticket_repo_changes", "planned_ticket_repo_impacts", "planned_ticket_change_validations",
            "planned_ticket_testing_considerations", "planned_ticket_open_questions",
        ];
        foreach (string table in repoChildren)
        {
            Parent(table, "planned_ticket_repos", [("TicketRepoId", "Id"), ("IssueKey", "IssueKey"), ("RepoKey", "RepoKey")]);
        }
        Parent("planned_ticket_repo_impacts", "planned_ticket_repo_changes",
            [("TicketRepoChangeId", "Id"), ("TicketRepoId", "TicketRepoId"), ("IssueKey", "IssueKey"), ("RepoKey", "RepoKey")],
            "c.TicketRepoChangeId IS NOT NULL");
        Parent("planned_ticket_topic_members", "planned_ticket_topics", [("TopicRowId", "RowId")]);
        Parent("planned_ticket_topic_groups", "planned_ticket_topics", [("TopicRowId", "RowId")]);
        Parent("planned_ticket_topic_repos", "planned_ticket_topics", [("TopicRowId", "RowId")]);
        Parent("planned_ticket_topic_members", "planned_ticket_topic_groups",
            [("TopicGroupRowId", "RowId"), ("TopicRowId", "TopicRowId")], "c.TopicGroupRowId IS NOT NULL");
        Parent("planned_ticket_topic_members", "planned_tickets", [("TicketKey", "Key")]);
        Parent("planned_ticket_topic_groups", "planned_ticket_topic_members",
            [("RowId", "TopicGroupRowId"), ("TopicRowId", "TopicRowId"), ("FirstTicketKey", "TicketKey")]);

        foreach (string table in new[]
        {
            "authoring_run_items", "authoring_run_attempts", "authoring_run_stages",
            "authoring_result_receipts", "authoring_run_input_provenance",
            "authoring_review_snapshots", "planned_ticket_partition_receipts",
        })
        {
            Parent(table, "authoring_runs", [("RunId", "Id")]);
        }
        Parent("authoring_revalidation_lineage", "authoring_runs", [("RunId", "Id")]);
        Parent("authoring_revalidation_lineage", "authoring_runs", [("PreviousRunId", "Id")]);
        if (HasColumns("authoring_runs", "SourceRunId"))
        {
            Parent("authoring_runs", "authoring_runs", [("SourceRunId", "Id")], "c.SourceRunId IS NOT NULL");
        }
        Parent("authoring_processor_modes", "authoring_runs",
            [("RevalidationRunId", "Id"), ("ProcessorKind", "ProcessorKind")], "c.RevalidationRunId IS NOT NULL");
        Parent("authoring_mutation_fences", "authoring_runs", [("RunId", "Id"), ("ProcessorKind", "ProcessorKind")],
            "c.RunId NOT LIKE 'maintenance:%'");
        Parent("authoring_run_attempts", "authoring_run_items", [("RunItemId", "Id"), ("RunId", "RunId")]);
        Parent("authoring_result_receipts", "authoring_runs", [("RunId", "Id"), ("AuthoringEpoch", "AuthoringEpoch")]);
        Parent("authoring_result_receipts", "authoring_run_items",
            [("RunItemId", "Id"), ("RunId", "RunId"), ("BusinessKey", "BusinessKey"),
             ("ExpectedSourceRevision", "ExpectedSourceRevision"), ("Id", "AcceptedReceiptId")]);
        Check("receipt-source-revision", "authoring_result_receipts", ["ExpectedSourceRevision", "ObservedSourceRevision"],
            [], "c.ExpectedSourceRevision IS NOT c.ObservedSourceRevision COLLATE BINARY");
        Parent("authoring_run_items", "authoring_result_receipts",
            [("AcceptedReceiptId", "Id"), ("Id", "RunItemId"), ("RunId", "RunId"), ("BusinessKey", "BusinessKey")],
            "c.AcceptedReceiptId IS NOT NULL");
        Parent("planned_ticket_run_item_partitions", "authoring_run_items",
            [("RunItemId", "Id"), ("TicketKey", "BusinessKey")]);
        Parent("planned_ticket_authoring_state", "authoring_result_receipts",
            [("OperationId", "OperationId"), ("RunId", "RunId"), ("RunItemId", "RunItemId"), ("TicketKey", "BusinessKey")],
            "c.Classification = 'receipt-backed'");
        Check("current-accepted-state", "planned_ticket_authoring_state", ["Classification", "RunItemId", "OperationId"],
            [("authoring_run_items", ["Id", "AcceptedReceiptId"]), ("authoring_result_receipts", ["OperationId", "Id"])],
            """
            c.Classification = 'receipt-backed' AND NOT EXISTS(
                SELECT 1 FROM authoring_run_items i JOIN authoring_result_receipts r ON r.Id = i.AcceptedReceiptId
                WHERE i.Id = c.RunItemId AND r.OperationId = c.OperationId)
            """);
        if (HasColumns("planned_ticket_authoring_state", "ReceiptContentHash"))
        {
            Check("current-receipt-hash", "planned_ticket_authoring_state", ["Classification", "ReceiptContentHash", "OperationId"],
                [("authoring_result_receipts", ["OperationId", "ContentHash"])],
                """
                c.Classification = 'receipt-backed' AND c.ReceiptContentHash IS NOT NULL AND NOT EXISTS(
                    SELECT 1 FROM authoring_result_receipts r
                    WHERE r.OperationId = c.OperationId AND r.ContentHash = c.ReceiptContentHash)
                """);
        }
        if (!snapshot)
        {
            Parent("authoring_result_receipts", "authoring_run_attempts",
                [("OperationId", "OperationId"), ("RunId", "RunId"), ("RunItemId", "RunItemId"),
                 ("ContentHash", "ContentHash"), ("ObservedSourceRevision", "ObservedSourceRevision")]);
            Check("receipt-accepted-attempt", "authoring_result_receipts", ["OperationId"],
                [("authoring_run_attempts", ["OperationId", "Status"])],
                """
                NOT EXISTS(SELECT 1 FROM authoring_run_attempts a
                    WHERE a.OperationId = c.OperationId AND a.Status = 'accepted')
                """);
            Parent("authoring_run_items", "authoring_run_attempts",
                [("CurrentOperationId", "OperationId"), ("Id", "RunItemId"), ("RunId", "RunId")],
                "c.CurrentOperationId IS NOT NULL");
            Check("accepted-operation-coordinate", "authoring_run_items", ["AcceptedReceiptId", "CurrentOperationId"],
                [("authoring_result_receipts", ["Id", "OperationId"])],
                """
                c.AcceptedReceiptId IS NOT NULL AND NOT EXISTS(
                    SELECT 1 FROM authoring_result_receipts r
                    WHERE r.Id = c.AcceptedReceiptId AND r.OperationId = c.CurrentOperationId)
                """);
            Parent("planned_ticket_partition_receipts", "authoring_run_stages",
                [("StageId", "Id"), ("RunId", "RunId"), ("PartitionKey", "PartitionKey"), ("InputFingerprint", "InputFingerprint")]);
            Parent("authoring_runs", "authoring_review_snapshots", [("SnapshotId", "Id"), ("Id", "RunId")],
                "c.SnapshotId IS NOT NULL");
            Check("run-item-counts", "authoring_runs", ["Id", "TotalItems"],
                [("authoring_run_items", ["RunId"])],
                "c.TotalItems < 0 OR c.TotalItems <> (SELECT COUNT(*) FROM authoring_run_items i WHERE i.RunId = c.Id)");
        }
        else
        {
            // The committed Planner snapshot projection intentionally omits attempts,
            // stages and review-snapshot rows, and retains only selected historic
            // items. Capture sets this flag only after external record/descriptor,
            // checksum, provenance, schema and table-count validation.
            checks.Add(new("verified-snapshot-projection", "passed", 0));
            Check("projected-run-item-counts", "authoring_runs", ["Id", "TotalItems"],
                [("authoring_run_items", ["RunId"])],
                "c.TotalItems < (SELECT COUNT(*) FROM authoring_run_items i WHERE i.RunId = c.Id)");
        }
        Parent("authoring_review_snapshots", "authoring_runs", [("RunId", "Id"), ("AuthoringEpoch", "AuthoringEpoch")]);
        Check("run-epochs", "authoring_runs", ["AuthoringEpoch"], [], "c.AuthoringEpoch < 0");
        Check("mode-epochs", "authoring_processor_modes", ["Epoch"], [], "c.Epoch < 0");
        Check("partition-counts", "planned_ticket_partition_receipts", ["TopicRows", "TopicGroupRows", "MemberRows"],
            [], "c.TopicRows < 0 OR c.TopicGroupRows < 0 OR c.MemberRows < 0");
        foreach ((string table, string lease, string acquired) in new[]
        {
            ("authoring_run_items", "PostPersistenceLeaseId", "PostPersistenceLeaseAcquiredAt"),
            ("authoring_run_stages", "LeaseId", "LeaseAcquiredAt"),
        })
        {
            if (HasColumns(table, lease, acquired))
            {
                Check("lease-coordinates:" + table, table, [lease, acquired], [],
                    $"(c.{Quote(lease)} IS NULL) <> (c.{Quote(acquired)} IS NULL)");
            }
        }
        return checks;

        bool HasRows(string table) => known.TryGetValue(Identifier(table), out PlannerRetainedTable? value) && value.Rows.RowCount != 0;
        bool HasColumns(string table, params string[] columns) => known.TryGetValue(Identifier(table), out PlannerRetainedTable? value) &&
            columns.All(column => value.Columns.Any(actual => Identifier(actual.Name) == Identifier(column)));

        void Parent(string child, string parent, (string Child, string Parent)[] keys, string where = "1")
        {
            string equals = string.Join(" AND ", keys.Select(key => $"p.{Quote(key.Parent)} = c.{Quote(key.Child)} COLLATE BINARY"));
            Check(child + "->" + parent + ":" + string.Join(",", keys.Select(key => key.Child)),
                child, keys.Select(key => key.Child).ToArray(),
                [(parent, keys.Select(key => key.Parent).ToArray())],
                $"({where}) AND (SELECT COUNT(*) FROM {Quote(parent)} p WHERE {equals}) <> 1");
        }

        void Check(string name, string child, string[] columns,
            (string Table, string[] Columns)[] parents, string violation)
        {
            ct.ThrowIfCancellationRequested();
            if (!HasRows(child))
            {
                checks.Add(new(name, known.ContainsKey(Identifier(child)) ? "empty" : "absent", 0));
                return;
            }
            if (!HasColumns(child, columns) || parents.Any(parent => !HasColumns(parent.Table, parent.Columns)))
            {
                // An optional relationship with no populated coordinate does not
                // require a missing legacy parent. SQL must still be readable.
                if (HasColumns(child, columns) && parents.Any(parent => !known.ContainsKey(Identifier(parent.Table))))
                {
                    string? conditional = violation.StartsWith('(')
                        ? violation[..(violation.IndexOf(") AND ", StringComparison.Ordinal) + 1)]
                        : null;
                    if (conditional is not null && Scalar(connection,
                            $"SELECT COUNT(*) FROM {Quote(child)} c WHERE {conditional}") == 0)
                    {
                        checks.Add(new(name, "no-required-coordinate", 0));
                        return;
                    }
                }
                checks.Add(new(name, "failed", 1));
                return;
            }
            long failures = Scalar(connection, $"SELECT COUNT(*) FROM {Quote(child)} c WHERE {violation}");
            checks.Add(new(name, failures == 0 ? "passed" : "failed", failures));
        }
    }

    internal static bool TableExists(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = @name COLLATE NOCASE";
        command.Parameters.AddWithValue("@name", table);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    internal static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // SQLite folds ASCII identifier case, not Unicode. Distinct unknown tables
    // must not collapse into one managed dictionary entry or one domain check.
    private static string Identifier(string value) => string.Concat(value.Select(character =>
        character is >= 'A' and <= 'Z' ? (char)(character + ('a' - 'A')) : character));

    internal static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static PlannerRetainedStateException Refuse(string category) => new("inventory", category);
}
