using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using FhirAugury.Common.IO;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Tools.NotesSite.Report;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Tools.NotesSite;

internal sealed record NotesSiteCleanupHooks(
    Func<ImmutableFileSnapshot, ValueTask>? DisposeSnapshotAsync = null);

/// <summary>Orchestrates the <c>report</c> verb: validates inputs, guards the output dir, emits the site.</summary>
internal static class ReportRunner
{
    private const string BallotNotesProcessorKind = "github-fhir-ballot-notes";
    private const int SupportedSnapshotSchemaVersion = 1;

    private static readonly string[] CountTables =
    [
        "notes",
        "note_source_files",
        "note_commits",
        "note_tickets",
        "note_structural_changes",
        "note_extension_refs",
        "notes_hydration_executions",
        "notes_hydration_run_items",
    ];

    private static readonly string[] PublicTables =
    [
        "authoring_runs",
        "authoring_run_items",
        "authoring_result_receipts",
        "authoring_snapshot_provenance",
        .. CountTables,
    ];

    private static readonly string[] ForbiddenColumnFragments =
    [
        "token",
        "verifier",
        "callback",
        "secret",
        "bearer",
        "password",
        "credential",
        "diagnostic",
    ];

    private static readonly string[] SanitizedAuthoringRunColumns =
    [
        "Id", "ProcessorKind", "AuthoringEpoch", "Status", "DatabaseOnly",
        "TotalItems", "CreatedAt", "StartedAt", "CompletedAt", "SnapshotId",
    ];

    private static readonly string[] SanitizedAuthoringRunItemColumns =
    [
        "Id", "RunId", "BusinessKey", "ItemKind", "ExpectedSourceRevision",
        "Status", "AcceptedReceiptId", "AttemptCount", "CreatedAt", "StartedAt",
        "CompletedAt",
    ];

    private static readonly string[] SanitizedReceiptColumns =
    [
        "Id", "OperationId", "RunId", "RunItemId", "BusinessKey", "ContentHash",
        "ExpectedSourceRevision", "ObservedSourceRevision", "AuthoringEpoch",
        "PersistedAt",
    ];

    private static readonly string[] SnapshotProvenanceColumns =
    [
        "SnapshotId", "ProcessorKind", "RunId", "AuthoringEpoch", "Sequence",
        "SchemaVersion", "ItemCount", "ReceiptCount", "TableCountsJson", "CreatedAt",
    ];

    private static readonly string[] SanitizedHydrationExecutionColumns =
    [
        "Id", "RunKey", "RepoOwner", "RepoName", "RepoCategory", "SinceSha",
        "SinceShortSha", "HeadSha", "HeadShortSha", "WindowLabel", "Status",
        "UnitsTotal", "UnitsHydrated", "CommitsInWindow", "TicketsAttributed",
        "IsCutoverBaseline", "StartedAt", "CompletedAt",
    ];

    private static readonly string[] SanitizedHydrationItemColumns =
    [
        "Id", "ExecutionId", "NoteId", "Type", "ItemOrder", "Status",
        "EvidenceHash", "EvidenceRevision", "HydratedAt",
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyCollection<string>>>
        SnapshotSchema = new(CreateExpectedSchema);

    public static async Task<int> RunAsync(
        ReportOptions options,
        NotesSiteCleanupHooks? cleanupHooks = null)
    {
        ImmutableFileSnapshot? privateSnapshot = null;
        SiteBuildManifest? successSummary = null;
        try
        {
            string sourcePath = Path.GetFullPath(options.SnapshotDbPath);
            string descriptorPath = Path.GetFullPath(options.SnapshotDescriptorPath);
            string outDir = Path.GetFullPath(options.OutPath);
            StagedDirectoryPublisher.RejectSourcePathsWithinPublication(
                outDir,
                sourcePath,
                descriptorPath);

            ImmutableFileSnapshot snapshot =
                await ImmutableFileSnapshot.CreateAsync(
                    sourcePath,
                    cleanupWarning: message =>
                        Console.Error.WriteLine($"Warning: {message}"))
                    .ConfigureAwait(false);
            privateSnapshot = snapshot;
            SnapshotValidationResult? validation =
                await ValidateSnapshotAsync(snapshot, descriptorPath)
                    .ConfigureAwait(false);
            if (validation is null)
            {
                return 1;
            }

            byte[] snapshotBytes = await File.ReadAllBytesAsync(snapshot.Path)
                .ConfigureAwait(false);
            string embeddedSha256 = ComputeSha256(snapshotBytes);
            DateTimeOffset generatedAt = DateTimeOffset.UtcNow;
            SiteBuildManifest manifest = SiteBuildManifest.Create(
                validation.Descriptor,
                validation.TableCounts,
                options.Title,
                NotesSpaEmitter.RendererAssetsVersion,
                embeddedSha256,
                snapshotBytes.LongLength,
                outDir,
                generatedAt);

            StagedDirectoryPublishResult result =
                await StagedDirectoryPublisher.PublishAsync(
                    outDir,
                    StagedDirectoryVersion.Snapshot(
                        "notes-site",
                        validation.Descriptor.ProcessorKind,
                        validation.Descriptor.Sequence,
                        validation.Descriptor.SnapshotId,
                        manifest.BuildIdentity),
                    (staging, _) =>
                    {
                        NotesSpaEmitter emitter = new(
                            snapshotBytes,
                            validation.Descriptor,
                            options.Title);
                        emitter.Emit(staging);
                        SiteBuildManifest.Write(staging, manifest);
                        return Task.CompletedTask;
                    },
                    (staging, ct) => ValidateStageAsync(staging, manifest, ct),
                    new StagedDirectoryPublishOptions(Force: options.Force),
                    CancellationToken.None).ConfigureAwait(false);

            successSummary = result.Outcome ==
                StagedDirectoryPublishOutcome.Idempotent
                ? SiteBuildManifest.Read(Path.Combine(outDir, SiteBuildManifest.FileName))
                : manifest;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            InvalidOperationException)
        {
            await Console.Error.WriteLineAsync(
                $"Snapshot site publication failed: {ex.Message}")
                .ConfigureAwait(false);
            return 1;
        }
        finally
        {
            if (privateSnapshot is not null)
            {
                await CleanupSnapshotBestEffortAsync(
                    privateSnapshot,
                    cleanupHooks).ConfigureAwait(false);
            }
        }

        Console.WriteLine(SiteBuildManifest.ToSummaryJson(successSummary!));
        return 0;
    }

    private static async ValueTask CleanupSnapshotBestEffortAsync(
        ImmutableFileSnapshot snapshot,
        NotesSiteCleanupHooks? cleanupHooks)
    {
        try
        {
            if (cleanupHooks?.DisposeSnapshotAsync is { } cleanup)
            {
                await cleanup(snapshot).ConfigureAwait(false);
            }
            else
            {
                await snapshot.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            await Console.Error.WriteLineAsync(
                $"Warning: deferred cleanup of private snapshot '{snapshot.Path}': {ex.Message}")
                .ConfigureAwait(false);
        }
    }

    private static async Task<SnapshotValidationResult?> ValidateSnapshotAsync(
        ImmutableFileSnapshot snapshot,
        string descriptorPath)
    {
        try
        {
            AuthoringSnapshotDescriptor descriptor =
                await ReadDescriptorAsync(descriptorPath).ConfigureAwait(false);
            ValidateDescriptorShape(descriptor, snapshot);
            ValidateWholeFile(snapshot, descriptor);

            await using SqliteConnection connection = new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = snapshot.Path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync().ConfigureAwait(false);
            await ValidateSchemaAsync(connection).ConfigureAwait(false);
            await ValidateBrowserQueriesAsync(connection).ConfigureAwait(false);
            await ValidateIntegrityAsync(connection).ConfigureAwait(false);
            await ValidateProvenanceAsync(connection, descriptor)
                .ConfigureAwait(false);

            if (descriptor.TableCounts.Count != CountTables.Length ||
                CountTables.Any(table => !descriptor.TableCounts.ContainsKey(table)))
            {
                throw new InvalidOperationException(
                    "Descriptor table-count catalog does not match the BallotNotes snapshot schema.");
            }

            Dictionary<string, long> counts = new(StringComparer.Ordinal);
            foreach (string table in CountTables)
            {
                long actual = await ScalarInt64Async(
                    connection,
                    $"SELECT COUNT(*) FROM \"{table}\"").ConfigureAwait(false);
                counts[table] = actual;
                if (actual != descriptor.TableCounts[table])
                {
                    throw new InvalidOperationException(
                        $"Snapshot table count mismatch for '{table}': " +
                        $"descriptor={descriptor.TableCounts[table]}, database={actual}.");
                }
            }

            long currentRunItems = await ScalarInt64Async(
                connection,
                """
                SELECT COUNT(*)
                FROM authoring_run_items
                WHERE RunId = @runId AND Status IN ('complete', 'superseded')
                """,
                ("@runId", descriptor.RunId)).ConfigureAwait(false);
            if (currentRunItems != descriptor.ItemCount)
            {
                throw new InvalidOperationException(
                    $"Snapshot item count mismatch: descriptor={descriptor.ItemCount}, " +
                    $"database={currentRunItems}.");
            }
            long receipts = await ScalarInt64Async(
                connection,
                "SELECT COUNT(*) FROM authoring_result_receipts").ConfigureAwait(false);
            if (receipts != descriptor.ReceiptCount)
            {
                throw new InvalidOperationException(
                    $"Snapshot receipt count mismatch: descriptor={descriptor.ReceiptCount}, " +
                    $"database={receipts}.");
            }

            long incomplete = await ScalarInt64Async(
                connection,
                """
                SELECT COUNT(*)
                FROM notes n
                LEFT JOIN notes_hydration_run_items h
                  ON h.ExecutionId = n.CurrentHydrationExecutionId
                 AND h.NoteId = n.NoteId
                 AND h.Type = n.Type
                 AND h.EvidenceRevision = n.CurrentEvidenceRevision
                 AND h.Status = 'completed'
                LEFT JOIN authoring_result_receipts r
                  ON r.OperationId = n.CurrentAuthoringOperationId
                 AND r.BusinessKey = n.NoteId
                 AND r.ExpectedSourceRevision = n.CurrentEvidenceRevision
                WHERE h.Id IS NULL
                   OR r.Id IS NULL
                   OR n.ProseVerificationStatus <> 'receipt-backed'
                   OR n.ProseHydrationExecutionId <> n.CurrentHydrationExecutionId
                   OR n.ProseEvidenceRevision <> n.CurrentEvidenceRevision
                """).ConfigureAwait(false);
            if (incomplete > 0)
            {
                throw new InvalidOperationException(
                    $"Snapshot has {incomplete} note(s) without complete hydration and receipt provenance.");
            }

            return new SnapshotValidationResult(descriptor, counts);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException or
            CryptographicException or SqliteException or InvalidOperationException)
        {
            await Console.Error.WriteLineAsync(
                $"Snapshot validation failed: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    private static async Task<AuthoringSnapshotDescriptor> ReadDescriptorAsync(
        string path)
    {
        if (!File.Exists(path))
        {
            throw new IOException($"Descriptor file not found: {path}");
        }
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<AuthoringSnapshotDescriptor>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Descriptor file '{path}' is empty.");
    }

    private static void ValidateDescriptorShape(
        AuthoringSnapshotDescriptor descriptor,
        ImmutableFileSnapshot snapshot)
    {
        if (!string.Equals(
            descriptor.ProcessorKind,
            BallotNotesProcessorKind,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Descriptor processor kind '{descriptor.ProcessorKind}' is not " +
                $"'{BallotNotesProcessorKind}'.");
        }
        if (string.IsNullOrWhiteSpace(descriptor.RunId) ||
            string.IsNullOrWhiteSpace(descriptor.SnapshotId) ||
            descriptor.AuthoringEpoch < 1 ||
            descriptor.Sequence < 1)
        {
            throw new InvalidOperationException(
                "Descriptor run, snapshot, epoch, and sequence values are invalid.");
        }
        if (descriptor.SchemaVersion != SupportedSnapshotSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported snapshot schema version {descriptor.SchemaVersion}; " +
                $"expected {SupportedSnapshotSchemaVersion}.");
        }
        if (descriptor.ItemCount < 0 || descriptor.ReceiptCount < 0 ||
            descriptor.SizeBytes < 1)
        {
            throw new InvalidOperationException(
                "Descriptor counts and size must be non-negative.");
        }
        if (!string.Equals(
            snapshot.SourceFileName,
            descriptor.FileName,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Descriptor file name does not match the snapshot input.");
        }
        if (descriptor.Sha256.Length != 64 ||
            descriptor.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException(
                "Descriptor SHA-256 must be a 64-character hexadecimal value.");
        }
    }

    private static void ValidateWholeFile(
        ImmutableFileSnapshot snapshot,
        AuthoringSnapshotDescriptor descriptor)
    {
        if (snapshot.SizeBytes != descriptor.SizeBytes)
        {
            throw new InvalidOperationException(
                $"Snapshot size mismatch: descriptor={descriptor.SizeBytes}, file={snapshot.SizeBytes}.");
        }
        if (!string.Equals(
            snapshot.Sha256,
            descriptor.Sha256,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Snapshot SHA-256 mismatch: descriptor={descriptor.Sha256}, file={snapshot.Sha256}.");
        }
    }

    private static async Task ValidateSchemaAsync(SqliteConnection connection)
    {
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> expected =
            SnapshotSchema.Value;
        List<string> tables = [];
        List<string> forbiddenObjects = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT type, name FROM sqlite_master
                WHERE type IN ('table', 'view', 'trigger')
                  AND name NOT LIKE 'sqlite_%'
                ORDER BY type, name
                """;
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                string type = reader.GetString(0);
                string name = reader.GetString(1);
                if (string.Equals(type, "table", StringComparison.Ordinal))
                {
                    tables.Add(name);
                }
                else
                {
                    forbiddenObjects.Add($"{type} {name}");
                }
            }
        }

        if (forbiddenObjects.Count > 0)
        {
            throw new InvalidOperationException(
                $"Snapshot contains forbidden schema objects: {string.Join(", ", forbiddenObjects)}.");
        }

        string[] forbidden = tables.Where(table => !expected.ContainsKey(table)).ToArray();
        if (forbidden.Length > 0)
        {
            throw new InvalidOperationException(
                $"Snapshot contains forbidden or internal tables: {string.Join(", ", forbidden)}.");
        }
        string[] missing = expected.Keys
            .Where(table => !tables.Contains(table, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Snapshot is missing required public tables: {string.Join(", ", missing)}.");
        }

        foreach ((string table, IReadOnlyCollection<string> expectedColumns) in expected)
        {
            IReadOnlyList<string> actualColumns =
                await ReadColumnNamesAsync(connection, table).ConfigureAwait(false);
            string[] missingColumns = expectedColumns
                .Where(column => !actualColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            string[] unexpectedColumns = actualColumns
                .Where(column => !expectedColumns.Contains(column, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (missingColumns.Length > 0 || unexpectedColumns.Length > 0)
            {
                throw new InvalidOperationException(
                    $"Snapshot table '{table}' does not match schema v1. " +
                    $"Missing columns: [{string.Join(", ", missingColumns)}]; " +
                    $"unexpected columns: [{string.Join(", ", unexpectedColumns)}].");
            }
            string? forbiddenColumn = actualColumns.FirstOrDefault(column =>
                ForbiddenColumnFragments.Any(fragment =>
                    column.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
            if (forbiddenColumn is not null)
            {
                throw new InvalidOperationException(
                    $"Snapshot contains forbidden column '{table}.{forbiddenColumn}'.");
            }
        }
    }

    private static async Task ValidateBrowserQueriesAsync(SqliteConnection connection)
    {
        string[] queries =
        [
            "SELECT * FROM notes LIMIT 0",
            "SELECT NoteId, Name, Type, WorkGroup, WorkGroupCode, WorkGroupNames, ListedWorkGroupNames, ListedWorkGroupCodes, IndexWorkGroupNames, IndexWorkGroupCodes, AppliedWorkGroupCodes, RepoOwner, RepoName, CommitsInWindow, TicketsAttributed, NeedsNote FROM notes LIMIT 0",
            "SELECT Path, Role, TouchedInWindow FROM note_source_files ORDER BY FileOrder LIMIT 0",
            "SELECT Sha, ShortSha, AuthorName, AuthorDate, Subject, WebUrl, TicketKeys FROM note_commits ORDER BY CommitOrder LIMIT 0",
            "SELECT TicketKey, Title, Resolution, WorkGroup, Specification, Url, CommitCount, ChangeImpact, ChangeCategory, IssueType, RelatedTicketKeys, TicketOrder FROM note_tickets ORDER BY TicketOrder LIMIT 0",
            "SELECT SourcePath, ElementPath, ChangeKind, Detail, TicketKeys FROM note_structural_changes ORDER BY ChangeOrder LIMIT 0",
            "SELECT ExtensionUrl, ExtensionName, ReplacementCoreElement, Rationale FROM note_extension_refs ORDER BY RefOrder LIMIT 0",
            "SELECT Id, RunKey, RepoOwner, RepoName, Status FROM notes_hydration_executions LIMIT 0",
            "SELECT Id, ExecutionId, NoteId, Type, Status, EvidenceRevision FROM notes_hydration_run_items LIMIT 0",
            "SELECT Id, ProcessorKind FROM authoring_runs LIMIT 0",
            "SELECT Id, RunId, BusinessKey, Status FROM authoring_run_items LIMIT 0",
            "SELECT Id, OperationId, BusinessKey, ExpectedSourceRevision FROM authoring_result_receipts LIMIT 0",
            "SELECT SnapshotId, ProcessorKind, RunId, Sequence FROM authoring_snapshot_provenance LIMIT 0",
        ];
        foreach (string query in queries)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = query;
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync().ConfigureAwait(false);
        }
    }

    private static async Task ValidateIntegrityAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        string result = Convert.ToString(await command.ExecuteScalarAsync()) ?? "unknown";
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Snapshot integrity check failed: {result}.");
        }
    }

    private static async Task ValidateProvenanceAsync(
        SqliteConnection connection,
        AuthoringSnapshotDescriptor descriptor)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT SnapshotId, ProcessorKind, RunId, AuthoringEpoch, Sequence,
                   SchemaVersion, ItemCount, ReceiptCount, TableCountsJson, CreatedAt
            FROM authoring_snapshot_provenance
            """;
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("Snapshot provenance row is missing.");
        }
        if (!string.Equals(reader.GetString(0), descriptor.SnapshotId, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), descriptor.ProcessorKind, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(2), descriptor.RunId, StringComparison.Ordinal) ||
            reader.GetInt64(3) != descriptor.AuthoringEpoch ||
            reader.GetInt64(4) != descriptor.Sequence ||
            reader.GetInt32(5) != descriptor.SchemaVersion ||
            reader.GetInt32(6) != descriptor.ItemCount ||
            reader.GetInt32(7) != descriptor.ReceiptCount ||
            DateTimeOffset.Parse(
                reader.GetString(9),
                System.Globalization.CultureInfo.InvariantCulture) !=
                descriptor.CreatedAt)
        {
            throw new InvalidOperationException(
                "Snapshot provenance does not match the trusted descriptor.");
        }
        Dictionary<string, long> counts =
            JsonSerializer.Deserialize<Dictionary<string, long>>(reader.GetString(8))
            ?? throw new InvalidOperationException(
                "Snapshot provenance table counts are invalid.");
        if (counts.Count != descriptor.TableCounts.Count ||
            counts.Any(pair =>
                !descriptor.TableCounts.TryGetValue(pair.Key, out long value) ||
                pair.Value != value))
        {
            throw new InvalidOperationException(
                "Snapshot provenance table counts do not match the trusted descriptor.");
        }
        if (await reader.ReadAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Snapshot contains more than one provenance row.");
        }
    }

    private static async Task ValidateStageAsync(
        string staging,
        SiteBuildManifest expected,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string[] required =
        [
            "index.html",
            SiteBuildManifest.FileName,
            StagedDirectoryPublisher.VersionFileName,
            Path.Combine("assets", "app.js"),
            Path.Combine("assets", "app.css"),
            Path.Combine("assets", "sql-wasm.js"),
            Path.Combine("assets", "sql-wasm.wasm"),
            Path.Combine("assets", "marked.min.js"),
            Path.Combine("assets", "purify.min.js"),
        ];
        foreach (string relative in required)
        {
            if (!File.Exists(Path.Combine(staging, relative)))
            {
                throw new InvalidOperationException(
                    $"Staged notes site is missing required file '{relative}'.");
            }
        }

        SiteBuildManifest actual = SiteBuildManifest.Read(
            Path.Combine(staging, SiteBuildManifest.FileName));
        if (!ManifestMatches(actual, expected))
        {
            throw new InvalidOperationException(
                "Staged notes site manifest does not match the validated build summary.");
        }
        string html = await File.ReadAllTextAsync(
            Path.Combine(staging, "index.html"),
            ct).ConfigureAwait(false);
        if (!html.Contains("window.__DB__='", StringComparison.Ordinal) ||
            !html.Contains(expected.SnapshotId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Staged notes site is missing its embedded snapshot or provenance.");
        }
        await ValidateEmbeddedDatabaseAsync(
            staging,
            expected.EmbeddedDbSha256,
            expected.EmbeddedDbSizeBytes,
            expected.TableCounts,
            expected.IncludedNoteCount,
            expected.IncludedReceiptCount,
            ct).ConfigureAwait(false);
    }

    private static bool ManifestMatches(
        SiteBuildManifest actual,
        SiteBuildManifest expected)
        =>
        string.Equals(actual.ProcessorKind, expected.ProcessorKind, StringComparison.Ordinal) &&
        string.Equals(actual.RunId, expected.RunId, StringComparison.Ordinal) &&
        string.Equals(actual.SnapshotId, expected.SnapshotId, StringComparison.Ordinal) &&
        actual.AuthoringEpoch == expected.AuthoringEpoch &&
        actual.SnapshotSequence == expected.SnapshotSequence &&
        actual.SnapshotSchemaVersion == expected.SnapshotSchemaVersion &&
        string.Equals(actual.SnapshotSha256, expected.SnapshotSha256, StringComparison.Ordinal) &&
        actual.SnapshotSizeBytes == expected.SnapshotSizeBytes &&
        string.Equals(actual.EmbeddedDbSha256, expected.EmbeddedDbSha256, StringComparison.Ordinal) &&
        actual.EmbeddedDbSizeBytes == expected.EmbeddedDbSizeBytes &&
        actual.IncludedNoteCount == expected.IncludedNoteCount &&
        actual.IncludedReceiptCount == expected.IncludedReceiptCount &&
        string.Equals(actual.SiteKind, expected.SiteKind, StringComparison.Ordinal) &&
        string.Equals(actual.Title, expected.Title, StringComparison.Ordinal) &&
        string.Equals(actual.RendererAssetsVersion, expected.RendererAssetsVersion, StringComparison.Ordinal) &&
        string.Equals(actual.BuildIdentity, expected.BuildIdentity, StringComparison.Ordinal) &&
        string.Equals(actual.OutputPath, expected.OutputPath, StringComparison.Ordinal) &&
        actual.GeneratedAt == expected.GeneratedAt &&
        actual.TableCounts.Count == expected.TableCounts.Count &&
        actual.TableCounts.All(pair =>
            expected.TableCounts.TryGetValue(pair.Key, out long value) &&
            pair.Value == value);

    private static async Task ValidateEmbeddedDatabaseAsync(
        string staging,
        string expectedSha256,
        long expectedSizeBytes,
        IReadOnlyDictionary<string, long> expectedCounts,
        int? expectedNoteCount,
        int? expectedReceiptCount,
        CancellationToken ct)
    {
        string html = await File.ReadAllTextAsync(
            Path.Combine(staging, "index.html"),
            ct).ConfigureAwait(false);
        byte[] bytes = ExtractEmbeddedDatabase(html);
        string actualSha256 = ComputeSha256(bytes);
        if (bytes.LongLength != expectedSizeBytes ||
            !string.Equals(actualSha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Staged notes embedded database checksum or size does not match its manifest.");
        }

        IReadOnlyDictionary<string, long> actualCounts =
            await ReadCountsFromBytesAsync(bytes, staging, ct).ConfigureAwait(false);
        if (actualCounts.Count != expectedCounts.Count ||
            actualCounts.Any(pair =>
                !expectedCounts.TryGetValue(pair.Key, out long expected) ||
                pair.Value != expected))
        {
            throw new InvalidOperationException(
                "Staged notes embedded database counts do not match its manifest.");
        }

        if (expectedNoteCount is not null &&
            actualCounts["notes"] != expectedNoteCount)
        {
            throw new InvalidOperationException(
                $"Staged notes embedded database item count mismatch: " +
                $"manifest={expectedNoteCount}, database={actualCounts["notes"]}.");
        }
        if (expectedReceiptCount is not null)
        {
            long receiptCount = await ReadCountFromBytesAsync(
                bytes,
                "authoring_result_receipts",
                staging,
                ct).ConfigureAwait(false);
            if (receiptCount != expectedReceiptCount)
            {
                throw new InvalidOperationException(
                    $"Staged notes embedded database receipt count mismatch: " +
                    $"manifest={expectedReceiptCount}, database={receiptCount}.");
            }
        }
    }

    private static async Task<long> ScalarInt64Async(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static IReadOnlyDictionary<string, IReadOnlyCollection<string>>
        CreateExpectedSchema()
    {
        using SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        BallotNotesDatabase.EnsureSchema(connection);

        Dictionary<string, IReadOnlyCollection<string>> schema =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string table in PublicTables)
        {
            schema[table] = table switch
            {
                "authoring_runs" => SanitizedAuthoringRunColumns,
                "authoring_run_items" => SanitizedAuthoringRunItemColumns,
                "authoring_result_receipts" => SanitizedReceiptColumns,
                "authoring_snapshot_provenance" => SnapshotProvenanceColumns,
                "notes_hydration_executions" => SanitizedHydrationExecutionColumns,
                "notes_hydration_run_items" => SanitizedHydrationItemColumns,
                _ => ReadColumnNames(connection, table),
            };
        }
        return schema;
    }

    private static IReadOnlyList<string> ReadColumnNames(
        SqliteConnection connection,
        string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> columns = [];
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }
        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Producer schema did not create required public table '{table}'.");
        }
        return columns;
    }

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        SqliteConnection connection,
        string table)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync().ConfigureAwait(false);
        List<string> columns = [];
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static Task<IReadOnlyDictionary<string, long>> ReadCountsFromBytesAsync(
        byte[] bytes)
        => ReadCountsFromBytesAsync(bytes, Path.GetTempPath(), CancellationToken.None);

    private static async Task<IReadOnlyDictionary<string, long>> ReadCountsFromBytesAsync(
        byte[] bytes,
        string directory,
        CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(
            directory,
            $".notes-site-validation-{Guid.NewGuid():N}.db");
        try
        {
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            await using SqliteConnection connection = new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync(ct).ConfigureAwait(false);
            Dictionary<string, long> counts = new(StringComparer.Ordinal);
            foreach (string table in CountTables)
            {
                counts[table] = await ScalarInt64Async(
                    connection,
                    $"SELECT COUNT(*) FROM \"{table}\"").ConfigureAwait(false);
            }
            return counts;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<long> ReadCountFromBytesAsync(
        byte[] bytes,
        string table,
        string directory,
        CancellationToken ct)
    {
        string path = Path.Combine(
            directory,
            $".notes-site-count-{Guid.NewGuid():N}.db");
        try
        {
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            await using SqliteConnection connection = new(
                new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return await ScalarInt64Async(
                connection,
                $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"")
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    private static byte[] ExtractEmbeddedDatabase(string html)
    {
        const string marker = "window.__DB__='";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                "Staged notes site does not contain an embedded database.");
        }
        start += marker.Length;
        int end = html.IndexOf('\'', start);
        if (end < 0)
        {
            throw new InvalidOperationException(
                "Staged notes site has an invalid embedded database.");
        }

        byte[] compressed;
        try
        {
            compressed = Convert.FromBase64String(html[start..end]);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "Staged notes site has an invalid embedded database encoding.",
                ex);
        }

        using MemoryStream input = new(compressed);
        using GZipStream gzip = new(input, CompressionMode.Decompress);
        using MemoryStream output = new();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record SnapshotValidationResult(
        AuthoringSnapshotDescriptor Descriptor,
        IReadOnlyDictionary<string, long> TableCounts);
}
