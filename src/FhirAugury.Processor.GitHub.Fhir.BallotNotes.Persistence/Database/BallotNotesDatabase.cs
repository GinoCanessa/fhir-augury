using System.Globalization;
using System.Text.Json;
using FhirAugury.Common.Database;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;

/// <summary>
/// The ballot-notes SQLite database owned by the BallotNotes processor.
/// Greenfield, augury-convention cslightdbgen schema on the
/// <see cref="SourceDatabase"/> base (no Jira processing queue). Splits each
/// unit's lifecycle into an evidence half (written by hydration via
/// <see cref="UpsertUnitEvidence"/>) and a prose half (written back by the
/// drafting skills via <see cref="UpdateNoteProse"/>); re-hydration never
/// clobbers authored prose.
/// </summary>
public sealed class BallotNotesDatabase : SourceDatabase
{
    public const string AuthoringProcessorKind = "github-fhir-ballot-notes";
    private readonly string _databasePath;
    private readonly string _mutationOwnerGeneration;
    private FileStream? _startupOwnerLock;

    public BallotNotesDatabase(
        string dbPath,
        ILogger logger,
        bool readOnly = false,
        string? mutationOwnerGeneration = null)
        : base(dbPath, logger, readOnly)
    {
        _databasePath = Path.GetFullPath(dbPath);
        _mutationOwnerGeneration = mutationOwnerGeneration
            ?? Guid.NewGuid().ToString("N");
    }

    protected override void InitializeSchema(SqliteConnection connection) => EnsureSchema(connection);

    /// <summary>
    /// Idempotent. Creates every notes table via the generated
    /// <c>CREATE TABLE IF NOT EXISTS</c> partials. Safe to call against a
    /// connection this instance does not own (the read-only renderer path uses
    /// it), mirroring <c>PreparerDatabase.EnsureSchema</c>.
    /// </summary>
    public static void EnsureSchema(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        NoteRecord.CreateTable(connection);
        NoteSourceFileRecord.CreateTable(connection);
        NoteCommitRecord.CreateTable(connection);
        NoteTicketRecord.CreateTable(connection);
        NotesRunRecord.CreateTable(connection);
        NotesHydrationExecutionRecord.CreateTable(connection);
        NotesHydrationRunItemRecord.CreateTable(connection);
        NoteStructuralChangeRecord.CreateTable(connection);
        NoteExtensionRefRecord.CreateTable(connection);
        AuthoringRunStore.EnsureSchema(connection);

        using (SqliteCommand state = connection.CreateCommand())
        {
            state.CommandText =
                """
                CREATE TABLE IF NOT EXISTS note_authoring_state(
                    NoteId TEXT NOT NULL PRIMARY KEY,
                    Classification TEXT NOT NULL,
                    EvidenceHash TEXT NOT NULL,
                    EvidenceRevision TEXT NOT NULL,
                    ProseHash TEXT NOT NULL,
                    HydrationExecutionId TEXT NOT NULL,
                    RunId TEXT NULL,
                    RunItemId TEXT NULL,
                    OperationId TEXT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_notes_hydration_item_identity
                    ON notes_hydration_run_items(ExecutionId, NoteId COLLATE NOCASE);
                CREATE TABLE IF NOT EXISTS notes_hydration_staged_evidence(
                    ExecutionId TEXT NOT NULL,
                    NoteId TEXT NOT NULL,
                    NoteJson TEXT NOT NULL,
                    SourceFilesJson TEXT NOT NULL,
                    CommitsJson TEXT NOT NULL,
                    TicketsJson TEXT NOT NULL,
                    StructuralChangesJson TEXT NOT NULL,
                    ExtensionRefsJson TEXT NOT NULL,
                    EvidenceHash TEXT NOT NULL,
                    EvidenceRevision TEXT NOT NULL,
                    HydratedAt TEXT NOT NULL,
                    PRIMARY KEY(ExecutionId, NoteId)
                );
                """;
            state.ExecuteNonQuery();
        }

        // Additive migrations for legacy DBs (cslightdbgen emits no ALTER):
        // back-fill columns added after the tables were first created. Must run
        // after the CreateTable calls so the tables exist to be altered.
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "WindowLabel", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NotesRunRecord.DefaultTableName, "WindowLabel", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteTicketRecord.DefaultTableName, "ChangeImpact", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteTicketRecord.DefaultTableName, "ChangeCategory", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteTicketRecord.DefaultTableName, "RelatedTicketKeys", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteTicketRecord.DefaultTableName, "IssueType", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "CurrentNoteIsAuguryGenerated", "INTEGER NOT NULL DEFAULT 0");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "PreservedHandAuthoredHtml", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "WorkGroupNames", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "WorkGroupCodes", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "ListedWorkGroupNames", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "ListedWorkGroupCodes", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "IndexWorkGroupNames", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "IndexWorkGroupCodes", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "AppliedWorkGroupNames", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "AppliedWorkGroupCodes", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "CurrentHydrationExecutionId", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "CurrentEvidenceHash", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "CurrentEvidenceRevision", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "ProseHydrationExecutionId", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "ProseEvidenceRevision", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "CurrentAuthoringOperationId", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NoteRecord.DefaultTableName, "ProseVerificationStatus", "TEXT NOT NULL DEFAULT 'unverified'");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NotesRunRecord.DefaultTableName, "LatestExecutionId", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NotesHydrationExecutionRecord.DefaultTableName, "MutationRunId", "TEXT NOT NULL DEFAULT ''");
        SqliteSchemaHelpers.AddColumnIfMissing(connection, NotesHydrationExecutionRecord.DefaultTableName, "MutationLeaseId", "TEXT NOT NULL DEFAULT ''");
    }

    /// <summary>Returns the number of notes currently stored.</summary>
    public int CountNotes()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{NoteRecord.DefaultTableName}\"";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Idempotently upserts one unit's hydrated evidence plus its children,
    /// keyed on <see cref="NoteRecord.NoteId"/>. Reads any existing prose +
    /// <see cref="NoteRecord.AuthoredAt"/> first and copies it forward, so a
    /// re-hydration of an already-authored unit never clobbers the note. Sets
    /// <see cref="NoteRecord.HydratedAt"/> and <see cref="NoteRecord.SavedAt"/>;
    /// <see cref="NoteRecord.GeneratedAt"/> is preserved when prose exists and
    /// reset to now otherwise (evidence-only units regenerate on every walk).
    /// </summary>
    public void UpsertUnitEvidence(
        NoteRecord evidence,
        IReadOnlyList<NoteSourceFileRecord> files,
        IReadOnlyList<NoteCommitRecord> commits,
        IReadOnlyList<NoteTicketRecord> tickets,
        IReadOnlyList<NoteStructuralChangeRecord>? structuralChanges = null,
        IReadOnlyList<NoteExtensionRefRecord>? extensionRefs = null)
        => UpsertUnitEvidenceCore(
            executionId: null,
            lease: null,
            evidence,
            files,
            commits,
            tickets,
            structuralChanges,
            extensionRefs);

    public string UpsertUnitEvidence(
        string executionId,
        HydrationMutationLease lease,
        NoteRecord evidence,
        IReadOnlyList<NoteSourceFileRecord> files,
        IReadOnlyList<NoteCommitRecord> commits,
        IReadOnlyList<NoteTicketRecord> tickets,
        IReadOnlyList<NoteStructuralChangeRecord>? structuralChanges = null,
        IReadOnlyList<NoteExtensionRefRecord>? extensionRefs = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentNullException.ThrowIfNull(lease);
        return UpsertUnitEvidenceCore(
            executionId,
            lease,
            evidence,
            files,
            commits,
            tickets,
            structuralChanges,
            extensionRefs);
    }

    private string UpsertUnitEvidenceCore(
        string? executionId,
        HydrationMutationLease? lease,
        NoteRecord evidence,
        IReadOnlyList<NoteSourceFileRecord> files,
        IReadOnlyList<NoteCommitRecord> commits,
        IReadOnlyList<NoteTicketRecord> tickets,
        IReadOnlyList<NoteStructuralChangeRecord>? structuralChanges,
        IReadOnlyList<NoteExtensionRefRecord>? extensionRefs)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(tickets);
        IReadOnlyList<NoteStructuralChangeRecord> structural = structuralChanges ?? [];
        IReadOnlyList<NoteExtensionRefRecord> extensions = extensionRefs ?? [];

        if (!string.IsNullOrWhiteSpace(executionId))
        {
            return StageUnitEvidence(
                executionId,
                lease
                    ?? throw new ArgumentNullException(nameof(lease)),
                evidence,
                files,
                commits,
                tickets,
                structural,
                extensions);
        }

        using SqliteConnection connection = OpenConnection();
        ExecuteRaw(connection, "BEGIN IMMEDIATE");
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string evidenceHash = ComputeEvidenceHash(
                evidence,
                files,
                commits,
                tickets,
                structural,
                extensions);
            string effectiveExecutionId = evidence.CurrentHydrationExecutionId;
            string evidenceRevision = string.IsNullOrWhiteSpace(effectiveExecutionId)
                ? evidenceHash
                : AuthoringResultHasher.HashNormalizedUtf8(
                    $"{effectiveExecutionId}\n{evidenceHash}");

            evidence.HydratedAt = now;
            evidence.SavedAt = now;
            evidence.CurrentHydrationExecutionId = effectiveExecutionId;
            evidence.CurrentEvidenceHash = evidenceHash;
            evidence.CurrentEvidenceRevision = evidenceRevision;

            ExistingNoteProse? existing = ReadExistingProse(connection, evidence.NoteId);
            if (existing is { AuthoredAt: not null } prior)
            {
                evidence.ProposedBallotNoteHtml = prior.ProposedBallotNoteHtml;
                evidence.RollupSummaryMarkdown = prior.RollupSummaryMarkdown;
                evidence.NotesForReviewerMarkdown = prior.NotesForReviewerMarkdown;
                evidence.SourceFilesNote = prior.SourceFilesNote;
                evidence.NeedsNote = prior.NeedsNote;
                evidence.AuthoredAt = prior.AuthoredAt;
                evidence.GeneratedAt = prior.GeneratedAt;
                evidence.ProseHydrationExecutionId = prior.ProseHydrationExecutionId;
                evidence.ProseEvidenceRevision = prior.ProseEvidenceRevision;
                evidence.CurrentAuthoringOperationId = prior.CurrentAuthoringOperationId;
                evidence.ProseVerificationStatus = prior.ProseVerificationStatus;
            }
            else
            {
                evidence.GeneratedAt = now;
            }

            DeleteByNoteId(connection, NoteSourceFileRecord.DefaultTableName, evidence.NoteId);
            DeleteByNoteId(connection, NoteCommitRecord.DefaultTableName, evidence.NoteId);
            DeleteByNoteId(connection, NoteTicketRecord.DefaultTableName, evidence.NoteId);
            DeleteByNoteId(connection, NoteStructuralChangeRecord.DefaultTableName, evidence.NoteId);
            DeleteByNoteId(connection, NoteExtensionRefRecord.DefaultTableName, evidence.NoteId);
            DeleteByNoteId(connection, NoteRecord.DefaultTableName, evidence.NoteId);

            InsertRecord(connection, NoteRecord.DefaultTableName, evidence);
            foreach (NoteSourceFileRecord file in files)
            {
                InsertRecord(connection, NoteSourceFileRecord.DefaultTableName, file);
            }
            foreach (NoteCommitRecord commit in commits)
            {
                InsertRecord(connection, NoteCommitRecord.DefaultTableName, commit);
            }
            foreach (NoteTicketRecord ticket in tickets)
            {
                InsertRecord(connection, NoteTicketRecord.DefaultTableName, ticket);
            }
            foreach (NoteStructuralChangeRecord change in structural)
            {
                InsertRecord(connection, NoteStructuralChangeRecord.DefaultTableName, change);
            }
            foreach (NoteExtensionRefRecord extension in extensions)
            {
                InsertRecord(connection, NoteExtensionRefRecord.DefaultTableName, extension);
            }

            ExecuteRaw(connection, "COMMIT");
            return evidenceRevision;
        }
        catch
        {
            ExecuteRaw(connection, "ROLLBACK");
            throw;
        }
    }

    private string StageUnitEvidence(
        string executionId,
        HydrationMutationLease lease,
        NoteRecord evidence,
        IReadOnlyList<NoteSourceFileRecord> files,
        IReadOnlyList<NoteCommitRecord> commits,
        IReadOnlyList<NoteTicketRecord> tickets,
        IReadOnlyList<NoteStructuralChangeRecord> structural,
        IReadOnlyList<NoteExtensionRefRecord> extensions)
    {
        using SqliteConnection connection = OpenConnection();
        ExecuteRaw(connection, "BEGIN IMMEDIATE");
        try
        {
            EnsureHydrationLease(connection, executionId, lease);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string evidenceHash = ComputeEvidenceHash(
                evidence,
                files,
                commits,
                tickets,
                structural,
                extensions);
            string evidenceRevision = AuthoringResultHasher.HashNormalizedUtf8(
                $"{executionId}\n{evidenceHash}");

            evidence.HydratedAt = now;
            evidence.SavedAt = now;
            evidence.CurrentHydrationExecutionId = executionId;
            evidence.CurrentEvidenceHash = evidenceHash;
            evidence.CurrentEvidenceRevision = evidenceRevision;

            using (SqliteCommand staged = connection.CreateCommand())
            {
                staged.CommandText =
                    """
                    INSERT INTO notes_hydration_staged_evidence(
                        ExecutionId, NoteId, NoteJson, SourceFilesJson,
                        CommitsJson, TicketsJson, StructuralChangesJson,
                        ExtensionRefsJson, EvidenceHash, EvidenceRevision,
                        HydratedAt)
                    SELECT
                        $executionId, $noteId, $noteJson, $sourceFilesJson,
                        $commitsJson, $ticketsJson, $structuralChangesJson,
                        $extensionRefsJson, $evidenceHash, $evidenceRevision,
                        $hydratedAt
                    WHERE EXISTS(
                        SELECT 1
                        FROM notes_hydration_run_items
                        WHERE ExecutionId = $executionId
                          AND NoteId = $noteId COLLATE NOCASE
                          AND Type = $type COLLATE NOCASE
                          AND Status = 'pending'
                    )
                    """;
                staged.Parameters.AddWithValue("$executionId", executionId);
                staged.Parameters.AddWithValue("$noteId", evidence.NoteId);
                staged.Parameters.AddWithValue("$type", evidence.Type);
                staged.Parameters.AddWithValue(
                    "$noteJson",
                    JsonSerializer.Serialize(evidence));
                staged.Parameters.AddWithValue(
                    "$sourceFilesJson",
                    JsonSerializer.Serialize(files));
                staged.Parameters.AddWithValue(
                    "$commitsJson",
                    JsonSerializer.Serialize(commits));
                staged.Parameters.AddWithValue(
                    "$ticketsJson",
                    JsonSerializer.Serialize(tickets));
                staged.Parameters.AddWithValue(
                    "$structuralChangesJson",
                    JsonSerializer.Serialize(structural));
                staged.Parameters.AddWithValue(
                    "$extensionRefsJson",
                    JsonSerializer.Serialize(extensions));
                staged.Parameters.AddWithValue("$evidenceHash", evidenceHash);
                staged.Parameters.AddWithValue(
                    "$evidenceRevision",
                    evidenceRevision);
                staged.Parameters.AddWithValue("$hydratedAt", Format(now));
                if (staged.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException(
                        $"Hydration item '{executionId}/{evidence.NoteId}' is not pending.");
                }
            }

            using (SqliteCommand item = connection.CreateCommand())
            {
                item.CommandText =
                    """
                    UPDATE notes_hydration_run_items
                    SET Status = 'completed',
                        EvidenceHash = $evidenceHash,
                        EvidenceRevision = $evidenceRevision,
                        HydratedAt = $hydratedAt,
                        Error = ''
                    WHERE ExecutionId = $executionId
                      AND NoteId = $noteId COLLATE NOCASE
                      AND Status = 'pending'
                    """;
                item.Parameters.AddWithValue("$evidenceHash", evidenceHash);
                item.Parameters.AddWithValue(
                    "$evidenceRevision",
                    evidenceRevision);
                item.Parameters.AddWithValue("$hydratedAt", Format(now));
                item.Parameters.AddWithValue("$executionId", executionId);
                item.Parameters.AddWithValue("$noteId", evidence.NoteId);
                if (item.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException(
                        $"Hydration item '{executionId}/{evidence.NoteId}' changed while staging.");
                }
            }

            ExecuteRaw(connection, "COMMIT");
            return evidenceRevision;
        }
        catch
        {
            ExecuteRaw(connection, "ROLLBACK");
            throw;
        }
    }

    private static void PromoteHydrationExecution(
        SqliteConnection connection,
        string executionId)
    {
                using (SqliteCommand validation = connection.CreateCommand())
                {
                    validation.CommandText =
                        """
                        SELECT
                            e.UnitsTotal,
                            SUM(CASE WHEN i.Status = 'completed' THEN 1 ELSE 0 END),
                            COUNT(s.NoteId)
                        FROM notes_hydration_executions e
                        LEFT JOIN notes_hydration_run_items i
                            ON i.ExecutionId = e.Id
                        LEFT JOIN notes_hydration_staged_evidence s
                            ON s.ExecutionId = i.ExecutionId
                           AND s.NoteId = i.NoteId
                        WHERE e.Id = $executionId
                          AND e.Status = 'running'
                        GROUP BY e.Id, e.UnitsTotal
                        """;
                    validation.Parameters.AddWithValue("$executionId", executionId);
                    using SqliteDataReader reader = validation.ExecuteReader();
                    if (!reader.Read() ||
                        reader.GetInt32(0) != reader.GetInt32(1) ||
                        reader.GetInt32(0) != reader.GetInt32(2))
                    {
                        throw new InvalidOperationException(
                            $"Hydration execution '{executionId}' cannot promote an incomplete staged corpus.");
                    }
                }

                List<StagedHydrationEvidence> stagedRows = [];
                using (SqliteCommand staged = connection.CreateCommand())
                {
                    staged.CommandText =
                        """
                        SELECT NoteJson, SourceFilesJson, CommitsJson, TicketsJson,
                               StructuralChangesJson, ExtensionRefsJson
                        FROM notes_hydration_staged_evidence
                        WHERE ExecutionId = $executionId
                        ORDER BY NoteId
                        """;
                    staged.Parameters.AddWithValue("$executionId", executionId);
                    using SqliteDataReader reader = staged.ExecuteReader();
                    while (reader.Read())
                    {
                        stagedRows.Add(new StagedHydrationEvidence(
                            JsonSerializer.Deserialize<NoteRecord>(reader.GetString(0))
                                ?? throw new InvalidOperationException(
                                    $"Hydration execution '{executionId}' contains invalid staged note evidence."),
                            JsonSerializer.Deserialize<List<NoteSourceFileRecord>>(
                                reader.GetString(1)) ?? [],
                            JsonSerializer.Deserialize<List<NoteCommitRecord>>(
                                reader.GetString(2)) ?? [],
                            JsonSerializer.Deserialize<List<NoteTicketRecord>>(
                                reader.GetString(3)) ?? [],
                            JsonSerializer.Deserialize<List<NoteStructuralChangeRecord>>(
                                reader.GetString(4)) ?? [],
                            JsonSerializer.Deserialize<List<NoteExtensionRefRecord>>(
                                reader.GetString(5)) ?? []));
                    }
                }

                foreach (StagedHydrationEvidence staged in stagedRows)
                {
                    NoteRecord evidence = staged.Note;
                    ExistingNoteProse? existing =
                        ReadExistingProse(connection, evidence.NoteId);
                    if (existing is { AuthoredAt: not null } prior)
                    {
                        evidence.ProposedBallotNoteHtml =
                            prior.ProposedBallotNoteHtml;
                        evidence.RollupSummaryMarkdown =
                            prior.RollupSummaryMarkdown;
                        evidence.NotesForReviewerMarkdown =
                            prior.NotesForReviewerMarkdown;
                        evidence.SourceFilesNote = prior.SourceFilesNote;
                        evidence.NeedsNote = prior.NeedsNote;
                        evidence.AuthoredAt = prior.AuthoredAt;
                        evidence.GeneratedAt = prior.GeneratedAt;
                        evidence.ProseHydrationExecutionId =
                            prior.ProseHydrationExecutionId;
                        evidence.ProseEvidenceRevision =
                            prior.ProseEvidenceRevision;
                        evidence.CurrentAuthoringOperationId =
                            prior.CurrentAuthoringOperationId;
                        evidence.ProseVerificationStatus = "stale";
                    }

                    DeleteByNoteId(
                        connection,
                        NoteSourceFileRecord.DefaultTableName,
                        evidence.NoteId);
                    DeleteByNoteId(
                        connection,
                        NoteCommitRecord.DefaultTableName,
                        evidence.NoteId);
                    DeleteByNoteId(
                        connection,
                        NoteTicketRecord.DefaultTableName,
                        evidence.NoteId);
                    DeleteByNoteId(
                        connection,
                        NoteStructuralChangeRecord.DefaultTableName,
                        evidence.NoteId);
                    DeleteByNoteId(
                        connection,
                        NoteExtensionRefRecord.DefaultTableName,
                        evidence.NoteId);
                    DeleteByNoteId(
                        connection,
                        NoteRecord.DefaultTableName,
                        evidence.NoteId);

                    InsertRecord(connection, NoteRecord.DefaultTableName, evidence);
                    foreach (NoteSourceFileRecord file in staged.SourceFiles)
                    {
                        InsertRecord(
                            connection,
                            NoteSourceFileRecord.DefaultTableName,
                            file);
                    }
                    foreach (NoteCommitRecord commit in staged.Commits)
                    {
                        InsertRecord(
                            connection,
                            NoteCommitRecord.DefaultTableName,
                            commit);
                    }
                    foreach (NoteTicketRecord ticket in staged.Tickets)
                    {
                        InsertRecord(
                            connection,
                            NoteTicketRecord.DefaultTableName,
                            ticket);
                    }
                    foreach (NoteStructuralChangeRecord change in
                             staged.StructuralChanges)
                    {
                        InsertRecord(
                            connection,
                            NoteStructuralChangeRecord.DefaultTableName,
                            change);
                    }
                    foreach (NoteExtensionRefRecord extension in
                             staged.ExtensionRefs)
                    {
                        InsertRecord(
                            connection,
                            NoteExtensionRefRecord.DefaultTableName,
                            extension);
                    }
        }
    }

    /// <summary>
    /// Writes back the authored prose for a unit, setting
    /// <see cref="NoteRecord.AuthoredAt"/>, refreshing
    /// <see cref="NoteRecord.GeneratedAt"/> and <see cref="NoteRecord.SavedAt"/>.
    /// Returns <c>false</c> when the slug was never hydrated (prose cannot attach
    /// to a non-existent unit).
    /// </summary>
    public bool UpdateNoteProse(string noteId, BallotNoteProse prose, DateTimeOffset authoredAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(noteId);
        ArgumentNullException.ThrowIfNull(prose);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            UPDATE "{NoteRecord.DefaultTableName}" SET
                NeedsNote = $needsNote,
                ProposedBallotNoteHtml = $proposed,
                RollupSummaryMarkdown = $rollup,
                NotesForReviewerMarkdown = $notes,
                SourceFilesNote = $srcNote,
                ProseHydrationExecutionId = CurrentHydrationExecutionId,
                ProseEvidenceRevision = CurrentEvidenceRevision,
                CurrentAuthoringOperationId = '',
                ProseVerificationStatus = 'legacy-unverified',
                AuthoredAt = $authoredAt,
                GeneratedAt = $authoredAt,
                SavedAt = $authoredAt
            WHERE NoteId = $id
            """;
        cmd.Parameters.AddWithValue("$needsNote", string.IsNullOrEmpty(prose.NeedsNote) ? "unknown" : prose.NeedsNote);
        cmd.Parameters.AddWithValue("$proposed", prose.ProposedBallotNoteHtml ?? string.Empty);
        cmd.Parameters.AddWithValue("$rollup", prose.RollupSummaryMarkdown ?? string.Empty);
        cmd.Parameters.AddWithValue("$notes", prose.NotesForReviewerMarkdown ?? string.Empty);
        cmd.Parameters.AddWithValue("$srcNote", prose.SourceFilesNote ?? string.Empty);
        cmd.Parameters.AddWithValue("$authoredAt", authoredAt);
        cmd.Parameters.AddWithValue("$id", noteId);
        bool updated = cmd.ExecuteNonQuery() > 0;
        if (updated)
        {
            NoteRecord current = ReadNote(connection, noteId)!;
            UpsertAuthoringState(
                connection,
                noteId,
                "legacy-unverified",
                current.CurrentEvidenceHash,
                current.CurrentEvidenceRevision,
                ComputeProseHash(prose),
                current.CurrentHydrationExecutionId,
                runId: null,
                runItemId: null,
                operationId: null,
                authoredAt);
        }
        return updated;
    }

    /// <summary>
    /// Re-stamps only the four owning-work-group columns
    /// (<see cref="NoteRecord.WorkGroup"/>, <see cref="NoteRecord.WorkGroupCode"/>,
    /// <see cref="NoteRecord.WorkGroupNames"/>, <see cref="NoteRecord.WorkGroupCodes"/>)
    /// on an existing note, leaving every other field — including prose,
    /// <c>NeedsNote</c>, and all timestamps — untouched. Used by the one-off
    /// owning-WG re-stamp maintenance command; deliberately does <em>not</em> set
    /// <c>SavedAt</c>/<c>GeneratedAt</c>/<c>AuthoredAt</c>/<c>HydratedAt</c>.
    /// Returns <c>true</c> when a row was updated.
    /// </summary>
    public bool UpdateNoteWorkGroups(
        string noteId,
        string workGroup,
        string workGroupCode,
        string workGroupNames,
        string workGroupCodes)
    {
        ArgumentException.ThrowIfNullOrEmpty(noteId);
        NoteDetail? detail = GetNote(noteId);
        if (detail is null)
        {
            return false;
        }
        ReallocateWorkGroupsAsync(
            new BallotNotesWorkGroupReallocationRequest(
                [
                    new BallotNoteWorkGroupReallocation(
                        noteId,
                        detail.Note.CurrentEvidenceRevision,
                        workGroup,
                        workGroupCode,
                        workGroupNames,
                        workGroupCodes),
                ])).GetAwaiter().GetResult();
        return true;
    }

    /// <summary>
    /// Enumerates every note id in <see cref="NoteRecord.RowId"/> order, optionally
    /// filtered to a single <paramref name="repo"/> (<c>owner/name</c>). Unlike
    /// <see cref="ListNotes"/> this is unpaged and returns ids only — used by the
    /// owning-WG re-stamp command to iterate the whole DB.
    /// </summary>
    public IReadOnlyList<string> ListNoteIds(string? repo)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();

        string where = string.Empty;
        if (!string.IsNullOrWhiteSpace(repo))
        {
            where = " WHERE (RepoOwner || '/' || RepoName) = $repo";
            cmd.Parameters.AddWithValue("$repo", repo);
        }
        cmd.CommandText = $"SELECT NoteId FROM \"{NoteRecord.DefaultTableName}\"{where} ORDER BY RowId";

        List<string> ids = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }
        return ids;
    }
    /// <see cref="NotesRunRecord.RunKey"/>. Called synchronously by the hydrate
    /// endpoint so a pollable status row exists before <c>202</c> is returned.
    /// </summary>
    public void BeginRun(NotesRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        using SqliteConnection connection = OpenConnection();
        UpsertLogicalRun(connection, run);
    }

    public HydrationMutationLease? TryAcquireHydrationLease(string executionId)
        => TryAcquireMutationLease("hydration", executionId);

    public void ReleaseMutationLease(HydrationMutationLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM authoring_mutation_fences
            WHERE ProcessorKind = $processorKind AND RunId = $runId AND LeaseId = $leaseId
            """;
        command.Parameters.AddWithValue("$processorKind", AuthoringProcessorKind);
        command.Parameters.AddWithValue("$runId", lease.RunId);
        command.Parameters.AddWithValue("$leaseId", lease.LeaseId);
        command.ExecuteNonQuery();
    }

    public void BeginHydrationExecution(
        NotesHydrationExecutionRecord execution,
        HydrationMutationLease lease)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(lease);
        using SqliteConnection connection = OpenConnection();
        ExecuteRaw(connection, "BEGIN IMMEDIATE");
        try
        {
            EnsureMutationLease(connection, lease);
            execution.MutationRunId = lease.RunId;
            execution.MutationLeaseId = lease.LeaseId;
            InsertRecord(
                connection,
                NotesHydrationExecutionRecord.DefaultTableName,
                execution);
            UpsertLogicalRun(
                connection,
                new NotesRunRecord
                {
                    RunKey = execution.RunKey,
                    LatestExecutionId = execution.Id,
                    RepoOwner = execution.RepoOwner,
                    RepoName = execution.RepoName,
                    RepoCategory = execution.RepoCategory,
                    SinceSha = execution.SinceSha,
                    SinceShortSha = execution.SinceShortSha,
                    HeadSha = execution.HeadSha,
                    HeadShortSha = execution.HeadShortSha,
                    WindowLabel = execution.WindowLabel,
                    Status = execution.Status,
                    UnitsTotal = execution.UnitsTotal,
                    UnitsHydrated = execution.UnitsHydrated,
                    CommitsInWindow = execution.CommitsInWindow,
                    TicketsAttributed = execution.TicketsAttributed,
                    StartedAt = execution.StartedAt,
                    CompletedAt = execution.CompletedAt,
                    Error = execution.Error,
                    RunAt = execution.StartedAt,
                });
            ExecuteRaw(connection, "COMMIT");
        }
        catch
        {
            ExecuteRaw(connection, "ROLLBACK");
            throw;
        }
    }

    public void SetHydrationMembership(
        string executionId,
        HydrationMutationLease lease,
        IReadOnlyList<HydrationMembershipDefinition> membership)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(membership);
        using SqliteConnection connection = OpenConnection();
        ExecuteRaw(connection, "BEGIN IMMEDIATE");
        try
        {
            EnsureHydrationLease(connection, executionId, lease);
            int order = 0;
            foreach (HydrationMembershipDefinition definition in membership)
            {
                InsertRecord(
                    connection,
                    NotesHydrationRunItemRecord.DefaultTableName,
                    new NotesHydrationRunItemRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    ExecutionId = executionId,
                    NoteId = definition.NoteId,
                    Type = definition.Type,
                    ItemOrder = order++,
                    Status = "pending",
                });
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE notes_hydration_executions
                SET UnitsTotal = $total
                WHERE Id = $executionId AND Status = 'running'
                """;
            command.Parameters.AddWithValue("$total", membership.Count);
            command.Parameters.AddWithValue("$executionId", executionId);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Hydration execution '{executionId}' is not running.");
            }

            UpdateLogicalRunPlan(connection, executionId, membership.Count);
            ExecuteRaw(connection, "COMMIT");
        }
        catch
        {
            ExecuteRaw(connection, "ROLLBACK");
            throw;
        }
    }

    /// <summary>
    /// Records the unit total and resolved HEAD for a run once the background
    /// grouping walk has completed (the values the synchronous
    /// <see cref="BeginRun"/> could not yet know).
    /// </summary>
    public void UpdateRunPlan(string runKey, int unitsTotal, string headSha, string headShortSha)
    {
        ArgumentException.ThrowIfNullOrEmpty(runKey);
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            UPDATE "{NotesRunRecord.DefaultTableName}" SET
                UnitsTotal = $total,
                HeadSha = $headSha,
                HeadShortSha = $headShortSha,
                RunAt = $now
            WHERE RunKey = $runKey
            """;
        cmd.Parameters.AddWithValue("$total", unitsTotal);
        cmd.Parameters.AddWithValue("$headSha", headSha ?? string.Empty);
        cmd.Parameters.AddWithValue("$headShortSha", headShortSha ?? string.Empty);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow);
        cmd.Parameters.AddWithValue("$runKey", runKey);
        cmd.ExecuteNonQuery();
    }

    public void BumpHydrationProgress(
        string executionId,
        HydrationMutationLease lease,
        int unitsHydrated,
        int commitsInWindow,
        int ticketsAttributed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentNullException.ThrowIfNull(lease);
        using SqliteConnection connection = OpenConnection();
        ExecuteRaw(connection, "BEGIN IMMEDIATE");
        try
        {
            EnsureHydrationLease(connection, executionId, lease);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE notes_hydration_executions
                SET UnitsHydrated = $hydrated,
                    CommitsInWindow = $commits,
                    TicketsAttributed = $tickets
                WHERE Id = $executionId AND Status = 'running'
                """;
            command.Parameters.AddWithValue("$hydrated", unitsHydrated);
            command.Parameters.AddWithValue("$commits", commitsInWindow);
            command.Parameters.AddWithValue("$tickets", ticketsAttributed);
            command.Parameters.AddWithValue("$executionId", executionId);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Hydration execution '{executionId}' is not running.");
            }

            using SqliteCommand summary = connection.CreateCommand();
            summary.CommandText =
                """
                UPDATE notes_runs
                SET UnitsHydrated = $hydrated,
                    CommitsInWindow = $commits,
                    TicketsAttributed = $tickets,
                    RunAt = $now
                WHERE LatestExecutionId = $executionId
                """;
            summary.Parameters.AddWithValue("$hydrated", unitsHydrated);
            summary.Parameters.AddWithValue("$commits", commitsInWindow);
            summary.Parameters.AddWithValue("$tickets", ticketsAttributed);
            summary.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
            summary.Parameters.AddWithValue("$executionId", executionId);
            summary.ExecuteNonQuery();
            ExecuteRaw(connection, "COMMIT");
        }
        catch
        {
            ExecuteRaw(connection, "ROLLBACK");
            throw;
        }
    }

    /// <summary>Updates the hydrated-unit counter and cumulative totals for a running run.</summary>
    public void BumpRunProgress(string runKey, int unitsHydrated, int commitsInWindow, int ticketsAttributed)
    {
        ArgumentException.ThrowIfNullOrEmpty(runKey);
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            UPDATE "{NotesRunRecord.DefaultTableName}" SET
                UnitsHydrated = $hydrated,
                CommitsInWindow = $commits,
                TicketsAttributed = $tickets,
                RunAt = $now
            WHERE RunKey = $runKey
            """;
        cmd.Parameters.AddWithValue("$hydrated", unitsHydrated);
        cmd.Parameters.AddWithValue("$commits", commitsInWindow);
        cmd.Parameters.AddWithValue("$tickets", ticketsAttributed);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow);
        cmd.Parameters.AddWithValue("$runKey", runKey);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Moves a run to a terminal state (<c>completed</c> or <c>failed</c>),
    /// stamping <see cref="NotesRunRecord.CompletedAt"/> and any error detail.
    /// </summary>
    public void FinishRun(string runKey, string status, string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(runKey);
        ArgumentException.ThrowIfNullOrEmpty(status);

        using SqliteConnection connection = OpenConnection();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            $"""
            UPDATE "{NotesRunRecord.DefaultTableName}" SET
                Status = $status,
                CompletedAt = $now,
                Error = $error,
                RunAt = $now
            WHERE RunKey = $runKey
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$error", error ?? string.Empty);
        cmd.Parameters.AddWithValue("$runKey", runKey);
        cmd.ExecuteNonQuery();
    }

    public void FinishHydrationExecution(
        string executionId,
        HydrationMutationLease lease,
        string status,
        string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        using SqliteConnection connection = OpenConnection();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ExecuteRaw(connection, "BEGIN IMMEDIATE");
        try
        {
            EnsureHydrationLease(connection, executionId, lease);
            if (string.Equals(status, "completed", StringComparison.Ordinal))
            {
                PromoteHydrationExecution(connection, executionId);
            }

            using (SqliteCommand execution = connection.CreateCommand())
            {
                execution.CommandText =
                    """
                    UPDATE notes_hydration_executions
                    SET Status = $status, CompletedAt = $completedAt, Error = $error
                    WHERE Id = $executionId AND Status = 'running'
                    """;
                execution.Parameters.AddWithValue("$status", status);
                execution.Parameters.AddWithValue("$completedAt", now);
                execution.Parameters.AddWithValue("$error", error ?? string.Empty);
                execution.Parameters.AddWithValue("$executionId", executionId);
                if (execution.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException(
                        $"Hydration execution '{executionId}' is not running.");
                }
            }

            using (SqliteCommand items = connection.CreateCommand())
            {
                items.CommandText =
                    """
                    UPDATE notes_hydration_run_items
                    SET Status = 'failed',
                        Error = CASE WHEN Error = '' THEN $error ELSE Error END
                    WHERE ExecutionId = $executionId AND Status = 'pending'
                    """;
                items.Parameters.AddWithValue("$error", error ?? "Hydration did not complete.");
                items.Parameters.AddWithValue("$executionId", executionId);
                items.ExecuteNonQuery();
            }

            using (SqliteCommand summary = connection.CreateCommand())
            {
                summary.CommandText =
                    """
                    UPDATE notes_runs
                    SET Status = $status, CompletedAt = $completedAt, Error = $error, RunAt = $completedAt
                    WHERE LatestExecutionId = $executionId
                    """;
                summary.Parameters.AddWithValue("$status", status);
                summary.Parameters.AddWithValue("$completedAt", now);
                summary.Parameters.AddWithValue("$error", error ?? string.Empty);
                summary.Parameters.AddWithValue("$executionId", executionId);
                summary.ExecuteNonQuery();
            }

            ExecuteRaw(connection, "COMMIT");
        }
        catch
        {
            ExecuteRaw(connection, "ROLLBACK");
            throw;
        }
    }

    public void MarkHydrationItemFailed(
        string executionId,
        HydrationMutationLease lease,
        string noteId,
        string error)
    {
        using SqliteConnection connection = OpenConnection();
        ExecuteRaw(connection, "BEGIN IMMEDIATE");
        try
        {
            EnsureHydrationLease(connection, executionId, lease);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE notes_hydration_run_items
                SET Status = 'failed', Error = $error
                WHERE ExecutionId = $executionId
                  AND NoteId = $noteId COLLATE NOCASE
                  AND Status = 'pending'
                """;
            command.Parameters.AddWithValue("$error", error);
            command.Parameters.AddWithValue("$executionId", executionId);
            command.Parameters.AddWithValue("$noteId", noteId);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException(
                    $"Hydration item '{executionId}/{noteId}' is not pending.");
            }
            ExecuteRaw(connection, "COMMIT");
        }
        catch
        {
            ExecuteRaw(connection, "ROLLBACK");
            throw;
        }
    }

    /// <summary>Lists notes matching <paramref name="filter"/>, newest-grouping order.</summary>
    public IReadOnlyList<NoteListRow> ListNotes(NoteQueryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();

        List<string> conditions = [];
        if (!string.IsNullOrWhiteSpace(filter.Repo))
        {
            conditions.Add("(n.RepoOwner || '/' || n.RepoName) = $repo");
            cmd.Parameters.AddWithValue("$repo", filter.Repo);
        }
        if (!string.IsNullOrWhiteSpace(filter.WorkGroupCode))
        {
            conditions.Add("n.WorkGroupCode = $wg COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$wg", filter.WorkGroupCode);
        }
        if (!string.IsNullOrWhiteSpace(filter.Type))
        {
            conditions.Add("n.Type = $type COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$type", filter.Type);
        }
        if (!string.IsNullOrWhiteSpace(filter.NeedsNote))
        {
            conditions.Add("n.NeedsNote = $needsNote COLLATE NOCASE");
            cmd.Parameters.AddWithValue("$needsNote", filter.NeedsNote);
        }
        if (string.Equals(filter.Status, "authored", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add(
                "r.Id IS NOT NULL AND i.Status = 'complete' " +
                "AND n.ProseVerificationStatus = 'receipt-backed' " +
                "AND n.ProseHydrationExecutionId = n.CurrentHydrationExecutionId " +
                "AND n.ProseEvidenceRevision = n.CurrentEvidenceRevision");
        }
        else if (string.Equals(filter.Status, "awaiting-note", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add(
                "n.AuthoredAt IS NULL " +
                "AND n.ProposedBallotNoteHtml = '' " +
                "AND n.RollupSummaryMarkdown = '' " +
                "AND n.NotesForReviewerMarkdown = ''");
        }
        else if (string.Equals(filter.Status, "legacy-unverified", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add(
                "n.ProseVerificationStatus = 'legacy-unverified' " +
                "AND (n.AuthoredAt IS NOT NULL " +
                "OR n.ProposedBallotNoteHtml <> '' " +
                "OR n.RollupSummaryMarkdown <> '' " +
                "OR n.NotesForReviewerMarkdown <> '')");
        }
        else if (string.Equals(filter.Status, "stale", StringComparison.OrdinalIgnoreCase))
        {
            conditions.Add(
                "n.ProseVerificationStatus <> 'legacy-unverified' " +
                "AND (n.AuthoredAt IS NOT NULL " +
                "OR n.ProposedBallotNoteHtml <> '' " +
                "OR n.RollupSummaryMarkdown <> '' " +
                "OR n.NotesForReviewerMarkdown <> '') " +
                "AND NOT (r.Id IS NOT NULL AND i.Status = 'complete' " +
                "AND n.ProseVerificationStatus = 'receipt-backed' " +
                "AND n.ProseHydrationExecutionId = n.CurrentHydrationExecutionId " +
                "AND n.ProseEvidenceRevision = n.CurrentEvidenceRevision)");
        }

        string where = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : string.Empty;
        int limit = filter.Limit <= 0 ? 50 : filter.Limit;
        int offset = filter.Offset < 0 ? 0 : filter.Offset;

        cmd.CommandText =
            "SELECT n.NoteId, n.Type, n.Name, n.RepoOwner, n.RepoName, n.WorkGroup, n.WorkGroupCode, n.NeedsNote, " +
            "n.CommitsInWindow, n.TicketsAttributed, n.HydratedAt, n.AuthoredAt, n.GeneratedAt, " +
            "n.CurrentHydrationExecutionId, n.CurrentEvidenceRevision, n.ProseVerificationStatus, " +
            "n.ProseHydrationExecutionId, n.ProseEvidenceRevision, n.ProposedBallotNoteHtml, " +
            "n.RollupSummaryMarkdown, n.NotesForReviewerMarkdown, " +
            "CASE WHEN r.Id IS NOT NULL AND i.Status = 'complete' THEN 1 ELSE 0 END " +
            $"FROM \"{NoteRecord.DefaultTableName}\" n " +
            "LEFT JOIN authoring_result_receipts r ON r.OperationId = n.CurrentAuthoringOperationId " +
            "LEFT JOIN authoring_run_items i ON i.Id = r.RunItemId AND i.AcceptedReceiptId = r.Id " +
            $"{where} ORDER BY n.WorkGroupCode, n.Type, n.Name LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        List<NoteListRow> rows = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            DateTimeOffset? authoredAt = reader.IsDBNull(11) ? null : new DateTimeOffset(reader.GetDateTime(11));
            rows.Add(new NoteListRow
            {
                NoteId = reader.GetString(0),
                Type = reader.GetString(1),
                Name = reader.GetString(2),
                RepoOwner = reader.GetString(3),
                RepoName = reader.GetString(4),
                WorkGroup = reader.GetString(5),
                WorkGroupCode = reader.GetString(6),
                NeedsNote = reader.GetString(7),
                CommitsInWindow = reader.GetInt32(8),
                TicketsAttributed = reader.GetInt32(9),
                HydratedAt = reader.IsDBNull(10) ? null : new DateTimeOffset(reader.GetDateTime(10)),
                AuthoredAt = authoredAt,
                GeneratedAt = new DateTimeOffset(reader.GetDateTime(12)),
                CurrentHydrationExecutionId = reader.GetString(13),
                CurrentEvidenceRevision = reader.GetString(14),
                ProseVerificationStatus = reader.GetString(15),
                Status = GetNoteStatus(
                    authoredAt,
                    reader.GetString(18),
                    reader.GetString(19),
                    reader.GetString(20),
                    reader.GetString(15),
                    reader.GetString(13),
                    reader.GetString(14),
                    reader.GetString(16),
                    reader.GetString(17),
                    reader.GetBoolean(21)),
            });
        }
        return rows;
    }

    /// <summary>Returns one note with its full hydrated evidence, or <c>null</c> if absent.</summary>
    public NoteDetail? GetNote(string noteId)
    {
        ArgumentException.ThrowIfNullOrEmpty(noteId);

        using SqliteConnection connection = OpenConnection();
        NoteRecord? note = ReadNote(connection, noteId);
        if (note is null) return null;

        return new NoteDetail
        {
            Note = note,
            SourceFiles = ReadSourceFiles(connection, noteId),
            Commits = ReadCommits(connection, noteId),
            Tickets = ReadTickets(connection, noteId),
            StructuralChanges = ReadStructuralChanges(connection, noteId),
            ExtensionRefs = ReadExtensionRefs(connection, noteId),
            IsCurrentProseReceiptBacked = IsCurrentProseReceiptBacked(connection, note),
        };
    }

    /// <summary>Returns the most recent run row, or <c>null</c> if none exists.</summary>
    public NotesRunRecord? GetLatestRun()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT RowId, RunKey, RepoOwner, RepoName, RepoCategory, SinceSha, SinceShortSha, " +
            "HeadSha, HeadShortSha, Status, UnitsTotal, UnitsHydrated, CommitsInWindow, TicketsAttributed, " +
            "StartedAt, CompletedAt, Error, RunAt, WindowLabel, LatestExecutionId " +
            $"FROM \"{NotesRunRecord.DefaultTableName}\" ORDER BY RunAt DESC, RowId DESC LIMIT 1";
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? MapRun(reader) : null;
    }

    /// <summary>Returns a single run by its key, or <c>null</c> if absent.</summary>
    public NotesRunRecord? GetRun(string runKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(runKey);
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT RowId, RunKey, RepoOwner, RepoName, RepoCategory, SinceSha, SinceShortSha, " +
            "HeadSha, HeadShortSha, Status, UnitsTotal, UnitsHydrated, CommitsInWindow, TicketsAttributed, " +
            "StartedAt, CompletedAt, Error, RunAt, WindowLabel, LatestExecutionId " +
            $"FROM \"{NotesRunRecord.DefaultTableName}\" WHERE RunKey = $runKey LIMIT 1";
        cmd.Parameters.AddWithValue("$runKey", runKey);
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? MapRun(reader) : null;
    }

    public NotesHydrationExecutionRecord? GetHydrationExecution(string executionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        using SqliteConnection connection = OpenConnection();
        return ReadHydrationExecution(connection, executionId);
    }

    public NotesHydrationExecutionRecord? GetLatestHydrationExecution()
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, Id, RunKey, RepoOwner, RepoName, RepoCategory,
                   SinceSha, SinceShortSha, HeadSha, HeadShortSha, WindowLabel,
                   Status, MutationRunId, MutationLeaseId,
                   UnitsTotal, UnitsHydrated, CommitsInWindow,
                   TicketsAttributed, IsCutoverBaseline, StartedAt, CompletedAt, Error
            FROM notes_hydration_executions
            ORDER BY StartedAt DESC, RowId DESC
            LIMIT 1
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? MapHydrationExecution(reader) : null;
    }

    public IReadOnlyList<NotesHydrationRunItemRecord> GetHydrationExecutionItems(
        string executionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, Id, ExecutionId, NoteId, Type, ItemOrder, Status,
                   EvidenceHash, EvidenceRevision, HydratedAt, Error
            FROM notes_hydration_run_items
            WHERE ExecutionId = $executionId
            ORDER BY ItemOrder, RowId
            """;
        command.Parameters.AddWithValue("$executionId", executionId);
        List<NotesHydrationRunItemRecord> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(MapHydrationItem(reader));
        }
        return rows;
    }

    public NotesHydrationExecutionRecord ResolveAuthoringExecution(
        string? executionId,
        IReadOnlyList<string>? requestedNoteIds)
    {
        using SqliteConnection connection = OpenConnection();
        if (!string.IsNullOrWhiteSpace(executionId))
        {
            NotesHydrationExecutionRecord execution =
                ReadHydrationExecution(connection, executionId)
                ?? throw new KeyNotFoundException(
                    $"Hydration execution '{executionId}' was not found.");
            if (!string.Equals(execution.Status, "completed", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Hydration execution '{executionId}' is '{execution.Status}', not completed.");
            }
            return execution;
        }

        string[] notes = requestedNoteIds?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (notes.Length != 1)
        {
            throw new ArgumentException(
                "A hydrationExecutionId is required unless exactly one noteId is requested.");
        }

        using SqliteCommand latest = connection.CreateCommand();
        latest.CommandText =
            """
            SELECT e.RowId, e.Id, e.RunKey, e.RepoOwner, e.RepoName, e.RepoCategory,
                   e.SinceSha, e.SinceShortSha, e.HeadSha, e.HeadShortSha, e.WindowLabel,
                   e.Status, e.MutationRunId, e.MutationLeaseId,
                   e.UnitsTotal, e.UnitsHydrated, e.CommitsInWindow,
                   e.TicketsAttributed, e.IsCutoverBaseline, e.StartedAt, e.CompletedAt, e.Error
            FROM notes_hydration_executions e
            INNER JOIN notes_hydration_run_items i ON i.ExecutionId = e.Id
            WHERE i.NoteId = $noteId COLLATE NOCASE
              AND i.Status = 'completed'
              AND e.Status = 'completed'
            ORDER BY e.CompletedAt DESC, e.RowId DESC
            LIMIT 2
            """;
        latest.Parameters.AddWithValue("$noteId", notes[0]);
        List<NotesHydrationExecutionRecord> candidates = [];
        using SqliteDataReader reader = latest.ExecuteReader();
        while (reader.Read())
        {
            candidates.Add(MapHydrationExecution(reader));
        }
        NotesHydrationExecutionRecord resolved = candidates.Count switch
        {
            0 => throw new KeyNotFoundException(
                $"No completed hydration execution contains '{notes[0]}'."),
            1 => candidates[0],
            _ when candidates[0].CompletedAt != candidates[1].CompletedAt => candidates[0],
            _ => throw new InvalidOperationException(
                $"The latest completed hydration execution for '{notes[0]}' is ambiguous."),
        };
        using SqliteCommand running = connection.CreateCommand();
        running.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM notes_hydration_executions
                WHERE RepoOwner = $repoOwner COLLATE NOCASE
                  AND RepoName = $repoName COLLATE NOCASE
                  AND Status = 'running'
                  AND StartedAt > $completedAt
            )
            """;
        running.Parameters.AddWithValue("$repoOwner", resolved.RepoOwner);
        running.Parameters.AddWithValue("$repoName", resolved.RepoName);
        running.Parameters.AddWithValue(
            "$completedAt",
            Format(resolved.CompletedAt!.Value));
        if (Convert.ToInt32(
            running.ExecuteScalar(),
            CultureInfo.InvariantCulture) != 0)
        {
            throw new InvalidOperationException(
                $"A newer hydration execution for '{resolved.RepoOwner}/{resolved.RepoName}' is still running.");
        }
        return resolved;
    }

    public IReadOnlyList<NotesHydrationRunItemRecord> GetCompletedHydrationItems(
        string executionId,
        IReadOnlyList<string>? requestedNoteIds)
    {
        NotesHydrationRunItemRecord[] completed = GetHydrationExecutionItems(executionId)
            .Where(item => string.Equals(item.Status, "completed", StringComparison.Ordinal))
            .ToArray();
        if (requestedNoteIds is null || requestedNoteIds.Count == 0)
        {
            return completed;
        }

        HashSet<string> requested = new(
            requestedNoteIds.Where(value => !string.IsNullOrWhiteSpace(value)),
            StringComparer.OrdinalIgnoreCase);
        NotesHydrationRunItemRecord[] selected = completed
            .Where(item => requested.Contains(item.NoteId))
            .ToArray();
        string[] missing = requested
            .Where(noteId => selected.All(item =>
                !string.Equals(item.NoteId, noteId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new KeyNotFoundException(
                $"Hydration execution '{executionId}' does not contain completed notes: {string.Join(", ", missing)}.");
        }
        return selected;
    }

    public string CreateOrReplayAuthoringRun(
        IReadOnlyCollection<AuthoringRunItemDefinition> definitions,
        bool databaseOnly)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (definitions.Count == 0)
        {
            throw new ArgumentException(
                "An authoring run must contain at least one item.",
                nameof(definitions));
        }

        AuthoringRunItemDefinition[] requested = definitions
            .OrderBy(value => value.BusinessKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.ItemKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.ExpectedSourceRevision, StringComparer.Ordinal)
            .ToArray();
        using SqliteConnection connection = OpenConnection();
        ExecuteRaw(connection, "BEGIN IMMEDIATE");
        try
        {
            long epoch;
            using (SqliteCommand mode = connection.CreateCommand())
            {
                mode.CommandText =
                    """
                    SELECT Mode, Epoch
                    FROM authoring_processor_modes
                    WHERE ProcessorKind = $processorKind
                    """;
                mode.Parameters.AddWithValue(
                    "$processorKind",
                    AuthoringProcessorKind);
                using SqliteDataReader reader = mode.ExecuteReader();
                if (!reader.Read() ||
                    !string.Equals(
                        reader.GetString(0),
                        AuthoringStatusValues.ProcessorModes.RunBacked,
                        StringComparison.Ordinal))
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.AuthoringNotActivated,
                        "BallotNotes run-backed authoring is not active.");
                }
                epoch = reader.GetInt64(1);
            }

            List<(string RunId, bool DatabaseOnly)> overlappingRuns = [];
            using (SqliteCommand overlaps = connection.CreateCommand())
            {
                List<string> predicates = [];
                for (int index = 0; index < requested.Length; index++)
                {
                    predicates.Add(
                        $"(i.BusinessKey = $businessKey{index} COLLATE NOCASE " +
                        $"AND i.ItemKind = $itemKind{index} COLLATE NOCASE " +
                        $"AND i.ExpectedSourceRevision = $revision{index})");
                    overlaps.Parameters.AddWithValue(
                        $"$businessKey{index}",
                        requested[index].BusinessKey);
                    overlaps.Parameters.AddWithValue(
                        $"$itemKind{index}",
                        requested[index].ItemKind);
                    overlaps.Parameters.AddWithValue(
                        $"$revision{index}",
                        requested[index].ExpectedSourceRevision);
                }
                overlaps.CommandText =
                    $"""
                    SELECT DISTINCT r.Id, r.DatabaseOnly, r.RowId
                    FROM authoring_runs r
                    INNER JOIN authoring_run_items i ON i.RunId = r.Id
                    WHERE r.ProcessorKind = $processorKind
                      AND ({string.Join(" OR ", predicates)})
                    ORDER BY r.RowId
                    """;
                overlaps.Parameters.AddWithValue(
                    "$processorKind",
                    AuthoringProcessorKind);
                using SqliteDataReader reader = overlaps.ExecuteReader();
                while (reader.Read())
                {
                    overlappingRuns.Add((
                        reader.GetString(0),
                        reader.GetBoolean(1)));
                }
            }

            foreach ((string runId, bool existingDatabaseOnly) in
                     overlappingRuns)
            {
                AuthoringRunItemDefinition[] existing =
                    ReadAuthoringRunDefinitions(connection, runId);
                if (existingDatabaseOnly == databaseOnly &&
                    DefinitionsEqual(requested, existing))
                {
                    ExecuteRaw(connection, "COMMIT");
                    return runId;
                }
            }

            if (overlappingRuns.Count > 0)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.RevisionAlreadyScheduled,
                    "One or more requested BallotNotes evidence revisions are already scheduled by a different authoring request.");
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            string id = Guid.NewGuid().ToString("N");
            InsertRecord(
                connection,
                AuthoringRunRecord.DefaultTableName,
                new AuthoringRunRecord
                {
                    Id = id,
                    ProcessorKind = AuthoringProcessorKind,
                    AuthoringEpoch = epoch,
                    Status = AuthoringStatusValues.Runs.Queued,
                    DatabaseOnly = databaseOnly,
                    TotalItems = requested.Length,
                    CreatedAt = now,
                });
            foreach (AuthoringRunItemDefinition definition in requested)
            {
                InsertRecord(
                    connection,
                    AuthoringRunItemRecord.DefaultTableName,
                    new AuthoringRunItemRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        RunId = id,
                        BusinessKey = definition.BusinessKey,
                        ItemKind = definition.ItemKind,
                        ExpectedSourceRevision =
                            definition.ExpectedSourceRevision,
                        Status = AuthoringStatusValues.Items.Pending,
                        CreatedAt = now,
                    });
            }

            ExecuteRaw(connection, "COMMIT");
            return id;
        }
        catch
        {
            ExecuteRaw(connection, "ROLLBACK");
            throw;
        }
    }

    public NotesHydrationRunItemRecord? GetHydrationItemForRevision(
        string noteId,
        string type,
        string evidenceRevision)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, Id, ExecutionId, NoteId, Type, ItemOrder, Status,
                   EvidenceHash, EvidenceRevision, HydratedAt, Error
            FROM notes_hydration_run_items
            WHERE NoteId = $noteId COLLATE NOCASE
              AND Type = $type COLLATE NOCASE
              AND EvidenceRevision = $evidenceRevision
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$noteId", noteId);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$evidenceRevision", evidenceRevision);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? MapHydrationItem(reader) : null;
    }

    public async Task SaveNoteProseForAuthoringAsync(
        SqliteConnection connection,
        string runId,
        string runItemId,
        string operationId,
        string noteId,
        string type,
        string observedEvidenceRevision,
        BallotNoteProse prose,
        string proseHash,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(prose);

        await using SqliteCommand read = connection.CreateCommand();
        read.CommandText =
            """
            SELECT n.Type, n.CurrentHydrationExecutionId, n.CurrentEvidenceHash,
                   n.CurrentEvidenceRevision, h.Status
            FROM notes n
            INNER JOIN notes_hydration_run_items h
                ON h.ExecutionId = n.CurrentHydrationExecutionId
               AND h.NoteId = n.NoteId
               AND h.Type = n.Type
               AND h.EvidenceRevision = n.CurrentEvidenceRevision
            WHERE n.NoteId = @noteId COLLATE NOCASE
            LIMIT 1
            """;
        read.Parameters.AddWithValue("@noteId", noteId);
        await using SqliteDataReader reader = await read.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.SourceRevisionMismatch,
                $"Note '{noteId}' no longer has current immutable evidence.");
        }
        string storedType = reader.GetString(0);
        string executionId = reader.GetString(1);
        string evidenceHash = reader.GetString(2);
        string evidenceRevision = reader.GetString(3);
        string hydrationStatus = reader.GetString(4);
        await reader.DisposeAsync();

        if (!string.Equals(storedType, type, StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.SourceRevisionMismatch,
                $"Note '{noteId}' is type '{storedType}', not '{type}'.");
        }
        if (!string.Equals(evidenceRevision, observedEvidenceRevision, StringComparison.Ordinal) ||
            !string.Equals(hydrationStatus, "completed", StringComparison.Ordinal))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.SourceRevisionMismatch,
                $"Note '{noteId}' evidence revision changed before result acceptance.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using SqliteCommand update = connection.CreateCommand();
        update.CommandText =
            """
            UPDATE notes
            SET NeedsNote = @needsNote,
                ProposedBallotNoteHtml = @proposed,
                RollupSummaryMarkdown = @rollup,
                NotesForReviewerMarkdown = @notes,
                SourceFilesNote = @sourceFilesNote,
                ProseHydrationExecutionId = @executionId,
                ProseEvidenceRevision = @evidenceRevision,
                CurrentAuthoringOperationId = @operationId,
                ProseVerificationStatus = 'receipt-backed',
                AuthoredAt = @authoredAt,
                GeneratedAt = @authoredAt,
                SavedAt = @authoredAt
            WHERE NoteId = @noteId COLLATE NOCASE
              AND Type = @type COLLATE NOCASE
              AND CurrentHydrationExecutionId = @executionId
              AND CurrentEvidenceRevision = @evidenceRevision
            """;
        update.Parameters.AddWithValue("@needsNote", NormalizeNeedsNote(prose.NeedsNote));
        update.Parameters.AddWithValue("@proposed", prose.ProposedBallotNoteHtml ?? string.Empty);
        update.Parameters.AddWithValue("@rollup", prose.RollupSummaryMarkdown ?? string.Empty);
        update.Parameters.AddWithValue("@notes", prose.NotesForReviewerMarkdown ?? string.Empty);
        update.Parameters.AddWithValue("@sourceFilesNote", prose.SourceFilesNote ?? string.Empty);
        update.Parameters.AddWithValue("@executionId", executionId);
        update.Parameters.AddWithValue("@evidenceRevision", evidenceRevision);
        update.Parameters.AddWithValue("@operationId", operationId);
        update.Parameters.AddWithValue("@authoredAt", Format(now));
        update.Parameters.AddWithValue("@noteId", noteId);
        update.Parameters.AddWithValue("@type", type);
        if (await update.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.SourceRevisionMismatch,
                $"Note '{noteId}' evidence changed during result acceptance.");
        }

        await UpsertAuthoringStateAsync(
            connection,
            noteId,
            "receipt-backed",
            evidenceHash,
            evidenceRevision,
            proseHash,
            executionId,
            runId,
            runItemId,
            operationId,
            now,
            ct);
    }

    public async Task<int> ClassifyLegacyNotesAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            List<string> noteIds = [];
            await using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT NoteId FROM notes ORDER BY RowId";
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    noteIds.Add(reader.GetString(0));
                }
            }

            int classified = 0;
            foreach (string noteId in noteIds)
            {
                NoteRecord note = ReadNote(connection, noteId)!;
                bool currentReceipt = IsCurrentProseReceiptBacked(connection, note);
                if (currentReceipt)
                {
                    continue;
                }

                string evidenceHash = string.IsNullOrWhiteSpace(note.CurrentEvidenceHash)
                    ? ComputeStoredEvidenceHash(connection, noteId)
                    : note.CurrentEvidenceHash;
                string evidenceRevision = string.IsNullOrWhiteSpace(note.CurrentEvidenceRevision)
                    ? evidenceHash
                    : note.CurrentEvidenceRevision;
                BallotNoteProse prose = ToProse(note);
                await UpsertAuthoringStateAsync(
                    connection,
                    noteId,
                    "legacy-unverified",
                    evidenceHash,
                    evidenceRevision,
                    ComputeProseHash(prose),
                    note.CurrentHydrationExecutionId,
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow,
                    ct);
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE notes
                    SET CurrentEvidenceHash = @evidenceHash,
                        CurrentEvidenceRevision = @evidenceRevision,
                        ProseVerificationStatus = 'legacy-unverified',
                        CurrentAuthoringOperationId = ''
                    WHERE NoteId = @noteId
                    """,
                    ct,
                    ("@evidenceHash", evidenceHash),
                    ("@evidenceRevision", evidenceRevision),
                    ("@noteId", noteId));
                classified++;
            }

            await ExecuteRawAsync(connection, "COMMIT", ct);
            return classified;
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task<int> CountLegacyUnverifiedAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM note_authoring_state WHERE Classification = 'legacy-unverified'";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    public async Task<NotesHydrationExecutionRecord> BuildCutoverBaselineExecutionAsync(
        CancellationToken ct = default)
    {
        string executionId = Guid.NewGuid().ToString("N");
        HydrationMutationLease lease = TryAcquireMutationLease("cutover-baseline", executionId)
            ?? throw new AuthoringConflictException(
                AuthoringConflictCode.MutationFenceUnavailable,
                "A mutating BallotNotes operation is already active.");
        try
        {
            await using SqliteConnection connection = OpenConnection();
            await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
            try
            {
                EnsureMutationLease(connection, lease);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                string runKey = $"cutover-baseline@{now:O}";
                NotesHydrationExecutionRecord execution = new()
                {
                    Id = executionId,
                    RunKey = runKey,
                    RepoOwner = "*",
                    RepoName = "*",
                    SinceSha = "cutover-baseline",
                    SinceShortSha = "baseline",
                    HeadSha = "cutover-baseline",
                    HeadShortSha = "baseline",
                    Status = "completed",
                    MutationRunId = lease.RunId,
                    MutationLeaseId = lease.LeaseId,
                    IsCutoverBaseline = true,
                    StartedAt = now,
                    CompletedAt = now,
                };

                List<string> noteIds = [];
                await using (SqliteCommand list = connection.CreateCommand())
                {
                    list.CommandText = "SELECT NoteId FROM notes ORDER BY RowId";
                    await using SqliteDataReader reader = await list.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        noteIds.Add(reader.GetString(0));
                    }
                }
                execution.UnitsTotal = noteIds.Count;
                execution.UnitsHydrated = noteIds.Count;
                InsertRecord(
                    connection,
                    NotesHydrationExecutionRecord.DefaultTableName,
                    execution);

                int order = 0;
                foreach (string noteId in noteIds)
                {
                    NoteRecord note = ReadNote(connection, noteId)!;
                    string evidenceHash = ComputeStoredEvidenceHash(connection, noteId);
                    string evidenceRevision = AuthoringResultHasher.HashNormalizedUtf8(
                        $"{executionId}\n{evidenceHash}");
                    InsertRecord(
                        connection,
                        NotesHydrationRunItemRecord.DefaultTableName,
                        new NotesHydrationRunItemRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        ExecutionId = executionId,
                        NoteId = note.NoteId,
                        Type = note.Type,
                        ItemOrder = order++,
                        Status = "completed",
                        EvidenceHash = evidenceHash,
                        EvidenceRevision = evidenceRevision,
                        HydratedAt = now,
                    });
                    await ExecuteAsync(
                        connection,
                        """
                        UPDATE notes
                        SET CurrentHydrationExecutionId = @executionId,
                            CurrentEvidenceHash = @evidenceHash,
                            CurrentEvidenceRevision = @evidenceRevision,
                            ProseVerificationStatus = CASE
                                WHEN AuthoredAt IS NULL THEN ProseVerificationStatus
                                ELSE 'legacy-unverified'
                            END
                        WHERE NoteId = @noteId
                        """,
                        ct,
                        ("@executionId", executionId),
                        ("@evidenceHash", evidenceHash),
                        ("@evidenceRevision", evidenceRevision),
                        ("@noteId", noteId));
                }

                await ExecuteRawAsync(connection, "COMMIT", ct);
                return execution;
            }
            catch
            {
                await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
                throw;
            }
        }
        finally
        {
            ReleaseMutationLease(lease);
        }
    }

    public async Task<BallotNotesWorkGroupReallocationResult> ReallocateWorkGroupsAsync(
        BallotNotesWorkGroupReallocationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Changes.Count == 0)
        {
            return new BallotNotesWorkGroupReallocationResult(0);
        }

        string operationId = Guid.NewGuid().ToString("N");
        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            string runId =
                $"maintenance:{_mutationOwnerGeneration}:workgroup-reallocation:{operationId}";
            string leaseId = Guid.NewGuid().ToString("N");
            int acquired = await ExecuteAsync(
                connection,
                """
                INSERT OR IGNORE INTO authoring_mutation_fences(ProcessorKind, RunId, LeaseId, AcquiredAt)
                VALUES(@processorKind, @runId, @leaseId, @acquiredAt)
                """,
                ct,
                ("@processorKind", AuthoringProcessorKind),
                ("@runId", runId),
                ("@leaseId", leaseId),
                ("@acquiredAt", Format(DateTimeOffset.UtcNow)));
            if (acquired != 1)
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.MutationFenceUnavailable,
                    "A mutating BallotNotes operation is already active.");
            }

            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (BallotNoteWorkGroupReallocation change in request.Changes)
            {
                if (!seen.Add(change.NoteId))
                {
                    throw new ArgumentException(
                        $"Workgroup reallocation contains duplicate note '{change.NoteId}'.");
                }
                await using SqliteCommand update = connection.CreateCommand();
                update.CommandText =
                    """
                    UPDATE notes
                    SET WorkGroup = @workGroup,
                        WorkGroupCode = @workGroupCode,
                        WorkGroupNames = @workGroupNames,
                        WorkGroupCodes = @workGroupCodes
                    WHERE NoteId = @noteId COLLATE NOCASE
                      AND CurrentEvidenceRevision = @expectedEvidenceRevision
                    """;
                update.Parameters.AddWithValue("@workGroup", change.WorkGroup ?? string.Empty);
                update.Parameters.AddWithValue("@workGroupCode", change.WorkGroupCode ?? string.Empty);
                update.Parameters.AddWithValue("@workGroupNames", change.WorkGroupNames ?? string.Empty);
                update.Parameters.AddWithValue("@workGroupCodes", change.WorkGroupCodes ?? string.Empty);
                update.Parameters.AddWithValue("@noteId", change.NoteId);
                update.Parameters.AddWithValue(
                    "@expectedEvidenceRevision",
                    change.ExpectedEvidenceRevision);
                if (await update.ExecuteNonQueryAsync(ct) != 1)
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.SourceRevisionMismatch,
                        $"Note '{change.NoteId}' does not match expected evidence revision '{change.ExpectedEvidenceRevision}'.");
                }
            }

            await ExecuteAsync(
                connection,
                """
                DELETE FROM authoring_mutation_fences
                WHERE ProcessorKind = @processorKind AND RunId = @runId AND LeaseId = @leaseId
                """,
                ct,
                ("@processorKind", AuthoringProcessorKind),
                ("@runId", runId),
                ("@leaseId", leaseId));
            await ExecuteRawAsync(connection, "COMMIT", ct);
            return new BallotNotesWorkGroupReallocationResult(request.Changes.Count);
        }
        catch
        {
            await ExecuteRawAsync(connection, "ROLLBACK", CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> ListRunsReadyForFinalizationAsync(
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.Id
            FROM authoring_runs r
            WHERE r.ProcessorKind = @processorKind
              AND r.Status IN (@running, @finalizing, @error)
              AND NOT EXISTS (
                  SELECT 1
                  FROM authoring_run_items i
                  WHERE i.RunId = r.Id
                    AND i.Status NOT IN (@complete, @superseded)
              )
            ORDER BY r.CreatedAt, r.RowId
            """;
        command.Parameters.AddWithValue("@processorKind", AuthoringProcessorKind);
        command.Parameters.AddWithValue("@running", AuthoringStatusValues.Runs.Running);
        command.Parameters.AddWithValue("@finalizing", AuthoringStatusValues.Runs.Finalizing);
        command.Parameters.AddWithValue("@error", AuthoringStatusValues.Runs.Error);
        command.Parameters.AddWithValue("@complete", AuthoringStatusValues.Items.Complete);
        command.Parameters.AddWithValue("@superseded", AuthoringStatusValues.Items.Superseded);
        List<string> runIds = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            runIds.Add(reader.GetString(0));
        }
        return runIds;
    }

    public async Task<string> GetHydrationExecutionIdForRunAsync(
        string runId,
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT DISTINCT h.ExecutionId
            FROM authoring_run_items i
            INNER JOIN notes_hydration_run_items h
                ON h.NoteId = i.BusinessKey
               AND h.Type = i.ItemKind
               AND h.EvidenceRevision = i.ExpectedSourceRevision
            WHERE i.RunId = @runId
            """;
        command.Parameters.AddWithValue("@runId", runId);
        List<string> ids = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetString(0));
        }
        return ids.Count == 1
            ? ids[0]
            : throw new InvalidOperationException(
                $"Authoring run '{runId}' does not resolve to exactly one hydration execution.");
    }

    public async Task<IReadOnlyDictionary<string, long>> GetSnapshotTableCountsAsync(
        CancellationToken ct = default)
    {
        string[] tables =
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
        Dictionary<string, long> counts = new(StringComparer.Ordinal);
        await using SqliteConnection connection = OpenConnection();
        foreach (string table in tables)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            counts[table] = Convert.ToInt64(
                await command.ExecuteScalarAsync(ct),
                CultureInfo.InvariantCulture);
        }
        return counts;
    }

    public async Task<int> CountCurrentReceiptBackedNotesAsync(
        CancellationToken ct = default)
    {
        await using SqliteConnection connection = OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM notes n
            INNER JOIN notes_hydration_run_items h
                ON h.ExecutionId = n.CurrentHydrationExecutionId
               AND h.NoteId = n.NoteId
               AND h.Type = n.Type
               AND h.EvidenceRevision = n.CurrentEvidenceRevision
               AND h.Status = 'completed'
            INNER JOIN authoring_result_receipts r
                ON r.OperationId = n.CurrentAuthoringOperationId
               AND r.BusinessKey = n.NoteId
               AND r.ExpectedSourceRevision = n.CurrentEvidenceRevision
            INNER JOIN authoring_run_items i
                ON i.Id = r.RunItemId
               AND i.AcceptedReceiptId = r.Id
               AND i.Status = 'complete'
            WHERE n.ProseVerificationStatus = 'receipt-backed'
              AND n.ProseHydrationExecutionId = n.CurrentHydrationExecutionId
              AND n.ProseEvidenceRevision = n.CurrentEvidenceRevision
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct),
            CultureInfo.InvariantCulture);
    }

    private static NoteRecord? ReadNote(SqliteConnection connection, string noteId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT NoteId, Type, Name, RepoOwner, RepoName, RepoCategory, WorkGroup, WorkGroupCode, " +
            "SinceSha, SinceShortSha, HeadSha, HeadShortSha, CommitsInWindow, TicketsAttributed, NeedsNote, " +
            "CurrentBallotNoteHtml, ProposedBallotNoteHtml, RollupSummaryMarkdown, NotesForReviewerMarkdown, " +
            "SourceFilesNote, HydratedAt, AuthoredAt, GeneratedAt, SavedAt, WindowLabel, " +
            "CurrentNoteIsAuguryGenerated, PreservedHandAuthoredHtml, WorkGroupNames, WorkGroupCodes, " +
            "ListedWorkGroupNames, ListedWorkGroupCodes, IndexWorkGroupNames, IndexWorkGroupCodes, " +
            "AppliedWorkGroupNames, AppliedWorkGroupCodes, CurrentHydrationExecutionId, CurrentEvidenceHash, " +
            "CurrentEvidenceRevision, ProseHydrationExecutionId, ProseEvidenceRevision, " +
            "CurrentAuthoringOperationId, ProseVerificationStatus " +
            $"FROM \"{NoteRecord.DefaultTableName}\" WHERE NoteId = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", noteId);
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new NoteRecord
        {
            NoteId = reader.GetString(0),
            Type = reader.GetString(1),
            Name = reader.GetString(2),
            RepoOwner = reader.GetString(3),
            RepoName = reader.GetString(4),
            RepoCategory = reader.GetString(5),
            WorkGroup = reader.GetString(6),
            WorkGroupCode = reader.GetString(7),
            SinceSha = reader.GetString(8),
            SinceShortSha = reader.GetString(9),
            HeadSha = reader.GetString(10),
            HeadShortSha = reader.GetString(11),
            CommitsInWindow = reader.GetInt32(12),
            TicketsAttributed = reader.GetInt32(13),
            NeedsNote = reader.GetString(14),
            CurrentBallotNoteHtml = reader.GetString(15),
            ProposedBallotNoteHtml = reader.GetString(16),
            RollupSummaryMarkdown = reader.GetString(17),
            NotesForReviewerMarkdown = reader.GetString(18),
            SourceFilesNote = reader.GetString(19),
            HydratedAt = reader.IsDBNull(20) ? null : new DateTimeOffset(reader.GetDateTime(20)),
            AuthoredAt = reader.IsDBNull(21) ? null : new DateTimeOffset(reader.GetDateTime(21)),
            GeneratedAt = new DateTimeOffset(reader.GetDateTime(22)),
            SavedAt = new DateTimeOffset(reader.GetDateTime(23)),
            WindowLabel = reader.GetString(24),
            CurrentNoteIsAuguryGenerated = reader.GetBoolean(25),
            PreservedHandAuthoredHtml = reader.GetString(26),
            WorkGroupNames = reader.GetString(27),
            WorkGroupCodes = reader.GetString(28),
            ListedWorkGroupNames = reader.GetString(29),
            ListedWorkGroupCodes = reader.GetString(30),
            IndexWorkGroupNames = reader.GetString(31),
            IndexWorkGroupCodes = reader.GetString(32),
            AppliedWorkGroupNames = reader.GetString(33),
            AppliedWorkGroupCodes = reader.GetString(34),
            CurrentHydrationExecutionId = reader.GetString(35),
            CurrentEvidenceHash = reader.GetString(36),
            CurrentEvidenceRevision = reader.GetString(37),
            ProseHydrationExecutionId = reader.GetString(38),
            ProseEvidenceRevision = reader.GetString(39),
            CurrentAuthoringOperationId = reader.GetString(40),
            ProseVerificationStatus = reader.GetString(41),
        };
    }

    private static List<NoteSourceFileRecord> ReadSourceFiles(SqliteConnection connection, string noteId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, NoteId, Path, Role, TouchedInWindow, FileOrder " +
            $"FROM \"{NoteSourceFileRecord.DefaultTableName}\" WHERE NoteId = $id ORDER BY FileOrder, RowId";
        cmd.Parameters.AddWithValue("$id", noteId);
        List<NoteSourceFileRecord> rows = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new NoteSourceFileRecord
            {
                Id = reader.GetString(0),
                NoteId = reader.GetString(1),
                Path = reader.GetString(2),
                Role = reader.GetString(3),
                TouchedInWindow = reader.GetBoolean(4),
                FileOrder = reader.GetInt32(5),
            });
        }
        return rows;
    }

    private static List<NoteCommitRecord> ReadCommits(SqliteConnection connection, string noteId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, NoteId, Sha, ShortSha, AuthorName, AuthorDate, Subject, WebUrl, TicketKeys, CommitOrder " +
            $"FROM \"{NoteCommitRecord.DefaultTableName}\" WHERE NoteId = $id ORDER BY CommitOrder, RowId";
        cmd.Parameters.AddWithValue("$id", noteId);
        List<NoteCommitRecord> rows = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new NoteCommitRecord
            {
                Id = reader.GetString(0),
                NoteId = reader.GetString(1),
                Sha = reader.GetString(2),
                ShortSha = reader.GetString(3),
                AuthorName = reader.GetString(4),
                AuthorDate = reader.GetString(5),
                Subject = reader.GetString(6),
                WebUrl = reader.GetString(7),
                TicketKeys = reader.GetString(8),
                CommitOrder = reader.GetInt32(9),
            });
        }
        return rows;
    }

    private static List<NoteTicketRecord> ReadTickets(SqliteConnection connection, string noteId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, NoteId, TicketKey, Title, Resolution, WorkGroup, Specification, Url, CommitCount, TicketOrder, " +
            "ChangeImpact, ChangeCategory, RelatedTicketKeys, IssueType " +
            $"FROM \"{NoteTicketRecord.DefaultTableName}\" WHERE NoteId = $id ORDER BY TicketOrder, RowId";
        cmd.Parameters.AddWithValue("$id", noteId);
        List<NoteTicketRecord> rows = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new NoteTicketRecord
            {
                Id = reader.GetString(0),
                NoteId = reader.GetString(1),
                TicketKey = reader.GetString(2),
                Title = reader.GetString(3),
                Resolution = reader.GetString(4),
                WorkGroup = reader.GetString(5),
                Specification = reader.GetString(6),
                Url = reader.GetString(7),
                CommitCount = reader.GetInt32(8),
                TicketOrder = reader.GetInt32(9),
                ChangeImpact = reader.GetString(10),
                ChangeCategory = reader.GetString(11),
                RelatedTicketKeys = reader.GetString(12),
                IssueType = reader.GetString(13),
            });
        }
        return rows;
    }

    private static List<NoteStructuralChangeRecord> ReadStructuralChanges(SqliteConnection connection, string noteId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, NoteId, SourcePath, ElementPath, ChangeKind, Detail, TicketKeys, ChangeOrder " +
            $"FROM \"{NoteStructuralChangeRecord.DefaultTableName}\" WHERE NoteId = $id ORDER BY ChangeOrder, RowId";
        cmd.Parameters.AddWithValue("$id", noteId);
        List<NoteStructuralChangeRecord> rows = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new NoteStructuralChangeRecord
            {
                Id = reader.GetString(0),
                NoteId = reader.GetString(1),
                SourcePath = reader.GetString(2),
                ElementPath = reader.GetString(3),
                ChangeKind = reader.GetString(4),
                Detail = reader.GetString(5),
                TicketKeys = reader.GetString(6),
                ChangeOrder = reader.GetInt32(7),
            });
        }
        return rows;
    }

    private static List<NoteExtensionRefRecord> ReadExtensionRefs(SqliteConnection connection, string noteId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, NoteId, ExtensionUrl, ExtensionName, ReplacementCoreElement, Rationale, RefOrder " +
            $"FROM \"{NoteExtensionRefRecord.DefaultTableName}\" WHERE NoteId = $id ORDER BY RefOrder, RowId";
        cmd.Parameters.AddWithValue("$id", noteId);
        List<NoteExtensionRefRecord> rows = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new NoteExtensionRefRecord
            {
                Id = reader.GetString(0),
                NoteId = reader.GetString(1),
                ExtensionUrl = reader.GetString(2),
                ExtensionName = reader.GetString(3),
                ReplacementCoreElement = reader.GetString(4),
                Rationale = reader.GetString(5),
                RefOrder = reader.GetInt32(6),
            });
        }
        return rows;
    }

    private static NotesRunRecord MapRun(SqliteDataReader reader) => new()
    {
        RunKey = reader.GetString(1),
        RepoOwner = reader.GetString(2),
        RepoName = reader.GetString(3),
        RepoCategory = reader.GetString(4),
        SinceSha = reader.GetString(5),
        SinceShortSha = reader.GetString(6),
        HeadSha = reader.GetString(7),
        HeadShortSha = reader.GetString(8),
        Status = reader.GetString(9),
        UnitsTotal = reader.GetInt32(10),
        UnitsHydrated = reader.GetInt32(11),
        CommitsInWindow = reader.GetInt32(12),
        TicketsAttributed = reader.GetInt32(13),
        StartedAt = reader.IsDBNull(14) ? null : new DateTimeOffset(reader.GetDateTime(14)),
        CompletedAt = reader.IsDBNull(15) ? null : new DateTimeOffset(reader.GetDateTime(15)),
        Error = reader.GetString(16),
        RunAt = new DateTimeOffset(reader.GetDateTime(17)),
        WindowLabel = reader.GetString(18),
        LatestExecutionId = reader.GetString(19),
    };

    private static ExistingNoteProse? ReadExistingProse(SqliteConnection connection, string noteId)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT ProposedBallotNoteHtml, RollupSummaryMarkdown, NotesForReviewerMarkdown, " +
            "SourceFilesNote, NeedsNote, AuthoredAt, GeneratedAt, ProseHydrationExecutionId, " +
            "ProseEvidenceRevision, CurrentAuthoringOperationId, ProseVerificationStatus " +
            $"FROM \"{NoteRecord.DefaultTableName}\" WHERE NoteId = $id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", noteId);
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new ExistingNoteProse(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : new DateTimeOffset(reader.GetDateTime(5)),
            new DateTimeOffset(reader.GetDateTime(6)),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10));
    }

    private HydrationMutationLease? TryAcquireMutationLease(
        string operation,
        string operationId)
    {
        string runId =
            $"{operation}:{_mutationOwnerGeneration}:{operationId}";
        string leaseId = Guid.NewGuid().ToString("N");
        using SqliteConnection connection = OpenConnection();
        ExecuteRaw(connection, "BEGIN IMMEDIATE");
        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR IGNORE INTO authoring_mutation_fences(ProcessorKind, RunId, LeaseId, AcquiredAt)
                VALUES($processorKind, $runId, $leaseId, $acquiredAt)
                """;
            command.Parameters.AddWithValue("$processorKind", AuthoringProcessorKind);
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue("$leaseId", leaseId);
            command.Parameters.AddWithValue("$acquiredAt", Format(DateTimeOffset.UtcNow));
            bool acquired = command.ExecuteNonQuery() == 1;
            ExecuteRaw(connection, "COMMIT");
            return acquired ? new HydrationMutationLease(runId, leaseId) : null;
        }
        catch
        {
            ExecuteRaw(connection, "ROLLBACK");
            throw;
        }
    }

    public void AcquireStartupOwnership()
    {
        if (_startupOwnerLock is not null)
        {
            return;
        }

        string ownerPath = _databasePath + ".owner";
        Directory.CreateDirectory(
            Path.GetDirectoryName(ownerPath)
                ?? throw new InvalidOperationException(
                    $"Database path '{_databasePath}' has no parent directory."));
        try
        {
            _startupOwnerLock = new FileStream(
                ownerPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Another BallotNotes service generation owns database '{_databasePath}'.",
                ex);
        }
    }

    public async Task<int> RecoverInterruptedHydrationAsync(
        CancellationToken ct = default)
    {
        if (_startupOwnerLock is null)
        {
            throw new InvalidOperationException(
                "BallotNotes startup ownership must be acquired before recovering interrupted hydration.");
        }

        await using SqliteConnection connection = OpenConnection();
        await ExecuteRawAsync(connection, "BEGIN IMMEDIATE", ct);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            List<(string ExecutionId, string RunId, string LeaseId)> abandoned = [];
            await using (SqliteCommand list = connection.CreateCommand())
            {
                list.CommandText =
                    """
                    SELECT Id, MutationRunId, MutationLeaseId
                    FROM notes_hydration_executions
                    WHERE Status = 'running'
                      AND MutationRunId LIKE 'hydration:%'
                      AND MutationRunId NOT LIKE @ownerPrefix
                    ORDER BY RowId
                    """;
                list.Parameters.AddWithValue(
                    "@ownerPrefix",
                    $"hydration:{_mutationOwnerGeneration}:%");
                await using SqliteDataReader reader =
                    await list.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    abandoned.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetString(2)));
                }
            }

            foreach ((string executionId, string runId, string leaseId) in
                     abandoned)
            {
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE notes_hydration_executions
                    SET Status = 'failed',
                        CompletedAt = @completedAt,
                        Error = 'Hydration execution was abandoned by a prior service generation.'
                    WHERE Id = @executionId AND Status = 'running'
                    """,
                    ct,
                    ("@completedAt", Format(now)),
                    ("@executionId", executionId));
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE notes_hydration_run_items
                    SET Status = 'failed',
                        Error = CASE
                            WHEN Error = '' THEN 'Hydration execution was abandoned by a prior service generation.'
                            ELSE Error
                        END
                    WHERE ExecutionId = @executionId AND Status = 'pending'
                    """,
                    ct,
                    ("@executionId", executionId));
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE notes_runs
                    SET Status = 'failed',
                        CompletedAt = @completedAt,
                        Error = 'Hydration execution was abandoned by a prior service generation.',
                        RunAt = @completedAt
                    WHERE LatestExecutionId = @executionId
                    """,
                    ct,
                    ("@completedAt", Format(now)),
                    ("@executionId", executionId));
                await ExecuteAsync(
                    connection,
                    """
                    DELETE FROM authoring_mutation_fences
                    WHERE ProcessorKind = @processorKind
                      AND RunId = @runId
                      AND LeaseId = @leaseId
                    """,
                    ct,
                    ("@processorKind", AuthoringProcessorKind),
                    ("@runId", runId),
                    ("@leaseId", leaseId));
            }

            int clearedOperationalFences = await ExecuteAsync(
                connection,
                """
                DELETE FROM authoring_mutation_fences
                WHERE ProcessorKind = @processorKind
                  AND (
                      RunId LIKE 'hydration:%'
                      OR
                      RunId LIKE 'maintenance:%'
                      OR RunId LIKE 'cutover-baseline:%'
                  )
                  AND RunId NOT LIKE @hydrationOwner
                  AND RunId NOT LIKE @maintenanceOwner
                  AND RunId NOT LIKE @baselineOwner
                """,
                ct,
                ("@processorKind", AuthoringProcessorKind),
                ("@hydrationOwner",
                    $"hydration:{_mutationOwnerGeneration}:%"),
                ("@maintenanceOwner",
                    $"maintenance:{_mutationOwnerGeneration}:%"),
                ("@baselineOwner",
                    $"cutover-baseline:{_mutationOwnerGeneration}:%"));

            await ExecuteRawAsync(connection, "COMMIT", ct);
            return abandoned.Count + clearedOperationalFences;
        }
        catch
        {
            await ExecuteRawAsync(
                connection,
                "ROLLBACK",
                CancellationToken.None);
            throw;
        }
    }

    private static void EnsureMutationLease(
        SqliteConnection connection,
        HydrationMutationLease lease)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM authoring_mutation_fences
            WHERE ProcessorKind = $processorKind AND RunId = $runId AND LeaseId = $leaseId
            """;
        command.Parameters.AddWithValue("$processorKind", AuthoringProcessorKind);
        command.Parameters.AddWithValue("$runId", lease.RunId);
        command.Parameters.AddWithValue("$leaseId", lease.LeaseId);
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.MutationFenceUnavailable,
                "The BallotNotes mutation lease was lost.");
        }
    }

    private static void EnsureHydrationLease(
        SqliteConnection connection,
        string executionId,
        HydrationMutationLease lease)
    {
        EnsureMutationLease(connection, lease);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM notes_hydration_executions
            WHERE Id = $executionId
              AND Status = 'running'
              AND MutationRunId = $runId
              AND MutationLeaseId = $leaseId
            """;
        command.Parameters.AddWithValue("$executionId", executionId);
        command.Parameters.AddWithValue("$runId", lease.RunId);
        command.Parameters.AddWithValue("$leaseId", lease.LeaseId);
        if (Convert.ToInt32(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture) != 1)
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.MutationFenceUnavailable,
                $"Hydration execution '{executionId}' does not own the durable mutation lease.");
        }
    }

    private static void UpsertLogicalRun(
        SqliteConnection connection,
        NotesRunRecord run)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO notes_runs(
                RunKey, LatestExecutionId, RepoOwner, RepoName, RepoCategory,
                SinceSha, SinceShortSha, HeadSha, HeadShortSha, WindowLabel,
                Status, UnitsTotal, UnitsHydrated, CommitsInWindow,
                TicketsAttributed, StartedAt, CompletedAt, Error, RunAt)
            VALUES(
                $runKey, $latestExecutionId, $repoOwner, $repoName, $repoCategory,
                $sinceSha, $sinceShortSha, $headSha, $headShortSha, $windowLabel,
                $status, $unitsTotal, $unitsHydrated, $commitsInWindow,
                $ticketsAttributed, $startedAt, $completedAt, $error, $runAt)
            ON CONFLICT(RunKey) DO UPDATE SET
                LatestExecutionId = excluded.LatestExecutionId,
                RepoOwner = excluded.RepoOwner,
                RepoName = excluded.RepoName,
                RepoCategory = excluded.RepoCategory,
                SinceSha = excluded.SinceSha,
                SinceShortSha = excluded.SinceShortSha,
                HeadSha = excluded.HeadSha,
                HeadShortSha = excluded.HeadShortSha,
                WindowLabel = excluded.WindowLabel,
                Status = excluded.Status,
                UnitsTotal = excluded.UnitsTotal,
                UnitsHydrated = excluded.UnitsHydrated,
                CommitsInWindow = excluded.CommitsInWindow,
                TicketsAttributed = excluded.TicketsAttributed,
                StartedAt = excluded.StartedAt,
                CompletedAt = excluded.CompletedAt,
                Error = excluded.Error,
                RunAt = excluded.RunAt
            """;
        command.Parameters.AddWithValue("$runKey", run.RunKey);
        command.Parameters.AddWithValue("$latestExecutionId", run.LatestExecutionId);
        command.Parameters.AddWithValue("$repoOwner", run.RepoOwner);
        command.Parameters.AddWithValue("$repoName", run.RepoName);
        command.Parameters.AddWithValue("$repoCategory", run.RepoCategory);
        command.Parameters.AddWithValue("$sinceSha", run.SinceSha);
        command.Parameters.AddWithValue("$sinceShortSha", run.SinceShortSha);
        command.Parameters.AddWithValue("$headSha", run.HeadSha);
        command.Parameters.AddWithValue("$headShortSha", run.HeadShortSha);
        command.Parameters.AddWithValue("$windowLabel", run.WindowLabel);
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$unitsTotal", run.UnitsTotal);
        command.Parameters.AddWithValue("$unitsHydrated", run.UnitsHydrated);
        command.Parameters.AddWithValue("$commitsInWindow", run.CommitsInWindow);
        command.Parameters.AddWithValue("$ticketsAttributed", run.TicketsAttributed);
        command.Parameters.AddWithValue(
            "$startedAt",
            run.StartedAt is null ? DBNull.Value : Format(run.StartedAt.Value));
        command.Parameters.AddWithValue(
            "$completedAt",
            run.CompletedAt is null ? DBNull.Value : Format(run.CompletedAt.Value));
        command.Parameters.AddWithValue("$error", run.Error);
        command.Parameters.AddWithValue("$runAt", Format(run.RunAt));
        command.ExecuteNonQuery();
    }

    private static void UpdateLogicalRunPlan(
        SqliteConnection connection,
        string executionId,
        int unitsTotal)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE notes_runs
            SET UnitsTotal = $unitsTotal, RunAt = $runAt
            WHERE LatestExecutionId = $executionId
            """;
        command.Parameters.AddWithValue("$unitsTotal", unitsTotal);
        command.Parameters.AddWithValue("$runAt", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$executionId", executionId);
        command.ExecuteNonQuery();
    }

    private static NotesHydrationExecutionRecord? ReadHydrationExecution(
        SqliteConnection connection,
        string executionId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT RowId, Id, RunKey, RepoOwner, RepoName, RepoCategory,
                   SinceSha, SinceShortSha, HeadSha, HeadShortSha, WindowLabel,
                   Status, MutationRunId, MutationLeaseId,
                   UnitsTotal, UnitsHydrated, CommitsInWindow,
                   TicketsAttributed, IsCutoverBaseline, StartedAt, CompletedAt, Error
            FROM notes_hydration_executions
            WHERE Id = $executionId
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$executionId", executionId);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? MapHydrationExecution(reader) : null;
    }

    private static NotesHydrationExecutionRecord MapHydrationExecution(
        SqliteDataReader reader)
        => new()
        {
            Id = reader.GetString(1),
            RunKey = reader.GetString(2),
            RepoOwner = reader.GetString(3),
            RepoName = reader.GetString(4),
            RepoCategory = reader.GetString(5),
            SinceSha = reader.GetString(6),
            SinceShortSha = reader.GetString(7),
            HeadSha = reader.GetString(8),
            HeadShortSha = reader.GetString(9),
            WindowLabel = reader.GetString(10),
            Status = reader.GetString(11),
            MutationRunId = reader.GetString(12),
            MutationLeaseId = reader.GetString(13),
            UnitsTotal = reader.GetInt32(14),
            UnitsHydrated = reader.GetInt32(15),
            CommitsInWindow = reader.GetInt32(16),
            TicketsAttributed = reader.GetInt32(17),
            IsCutoverBaseline = reader.GetBoolean(18),
            StartedAt = new DateTimeOffset(reader.GetDateTime(19)),
            CompletedAt = reader.IsDBNull(20)
                ? null
                : new DateTimeOffset(reader.GetDateTime(20)),
            Error = reader.GetString(21),
        };

    private static NotesHydrationRunItemRecord MapHydrationItem(
        SqliteDataReader reader)
        => new()
        {
            Id = reader.GetString(1),
            ExecutionId = reader.GetString(2),
            NoteId = reader.GetString(3),
            Type = reader.GetString(4),
            ItemOrder = reader.GetInt32(5),
            Status = reader.GetString(6),
            EvidenceHash = reader.GetString(7),
            EvidenceRevision = reader.GetString(8),
            HydratedAt = reader.IsDBNull(9)
                ? null
                : new DateTimeOffset(reader.GetDateTime(9)),
            Error = reader.GetString(10),
        };

    private static AuthoringRunItemDefinition[] ReadAuthoringRunDefinitions(
        SqliteConnection connection,
        string runId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT BusinessKey, ItemKind, ExpectedSourceRevision
            FROM authoring_run_items
            WHERE RunId = $runId
            ORDER BY BusinessKey COLLATE NOCASE,
                     ItemKind COLLATE NOCASE,
                     ExpectedSourceRevision
            """;
        command.Parameters.AddWithValue("$runId", runId);
        List<AuthoringRunItemDefinition> definitions = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            definitions.Add(new AuthoringRunItemDefinition(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2)));
        }
        return [.. definitions];
    }

    private static bool DefinitionsEqual(
        IReadOnlyList<AuthoringRunItemDefinition> left,
        IReadOnlyList<AuthoringRunItemDefinition> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            if (!string.Equals(
                    left[index].BusinessKey,
                    right[index].BusinessKey,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    left[index].ItemKind,
                    right[index].ItemKind,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    left[index].ExpectedSourceRevision,
                    right[index].ExpectedSourceRevision,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsCurrentProseReceiptBacked(
        SqliteConnection connection,
        NoteRecord note)
    {
        if (string.IsNullOrWhiteSpace(note.CurrentAuthoringOperationId))
        {
            return false;
        }
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM authoring_result_receipts r
                INNER JOIN authoring_run_items i
                    ON i.Id = r.RunItemId AND i.AcceptedReceiptId = r.Id
                WHERE r.OperationId = $operationId
                  AND r.BusinessKey = $noteId COLLATE NOCASE
                  AND r.ExpectedSourceRevision = $evidenceRevision
                  AND i.Status = 'complete'
            )
            """;
        command.Parameters.AddWithValue(
            "$operationId",
            note.CurrentAuthoringOperationId);
        command.Parameters.AddWithValue("$noteId", note.NoteId);
        command.Parameters.AddWithValue(
            "$evidenceRevision",
            note.CurrentEvidenceRevision);
        return Convert.ToInt32(
            command.ExecuteScalar(),
            CultureInfo.InvariantCulture) != 0;
    }

    private static string GetNoteStatus(
        DateTimeOffset? authoredAt,
        string proposed,
        string rollup,
        string reviewerNotes,
        string verificationStatus,
        string currentExecutionId,
        string currentEvidenceRevision,
        string proseExecutionId,
        string proseEvidenceRevision,
        bool hasReceipt)
    {
        if (hasReceipt &&
            string.Equals(
                verificationStatus,
                "receipt-backed",
                StringComparison.Ordinal) &&
            string.Equals(
                currentExecutionId,
                proseExecutionId,
                StringComparison.Ordinal) &&
            string.Equals(
                currentEvidenceRevision,
                proseEvidenceRevision,
                StringComparison.Ordinal))
        {
            return "authored";
        }
        bool hasProse = authoredAt is not null ||
            !string.IsNullOrWhiteSpace(proposed) ||
            !string.IsNullOrWhiteSpace(rollup) ||
            !string.IsNullOrWhiteSpace(reviewerNotes);
        if (!hasProse)
        {
            return "awaiting-note";
        }
        return string.Equals(
            verificationStatus,
            "legacy-unverified",
            StringComparison.Ordinal)
            ? "legacy-unverified"
            : "stale";
    }

    private static string ComputeEvidenceHash(
        NoteRecord note,
        IReadOnlyList<NoteSourceFileRecord> files,
        IReadOnlyList<NoteCommitRecord> commits,
        IReadOnlyList<NoteTicketRecord> tickets,
        IReadOnlyList<NoteStructuralChangeRecord> structural,
        IReadOnlyList<NoteExtensionRefRecord> extensions)
    {
        string payload = JsonSerializer.Serialize(new
        {
            note.NoteId,
            note.Type,
            note.Name,
            note.RepoOwner,
            note.RepoName,
            note.RepoCategory,
            note.ListedWorkGroupNames,
            note.ListedWorkGroupCodes,
            note.IndexWorkGroupNames,
            note.IndexWorkGroupCodes,
            note.AppliedWorkGroupNames,
            note.AppliedWorkGroupCodes,
            note.SinceSha,
            note.HeadSha,
            note.WindowLabel,
            note.CommitsInWindow,
            note.TicketsAttributed,
            note.CurrentBallotNoteHtml,
            note.CurrentNoteIsAuguryGenerated,
            note.PreservedHandAuthoredHtml,
            Files = files
                .OrderBy(value => value.FileOrder)
                .Select(value => new
                {
                    value.Path,
                    value.Role,
                    value.TouchedInWindow,
                    value.FileOrder,
                }),
            Commits = commits
                .OrderBy(value => value.CommitOrder)
                .Select(value => new
                {
                    value.Sha,
                    value.ShortSha,
                    value.AuthorName,
                    value.AuthorDate,
                    value.Subject,
                    value.WebUrl,
                    value.TicketKeys,
                    value.CommitOrder,
                }),
            Tickets = tickets
                .OrderBy(value => value.TicketOrder)
                .Select(value => new
                {
                    value.TicketKey,
                    value.Title,
                    value.Resolution,
                    value.WorkGroup,
                    value.Specification,
                    value.Url,
                    value.CommitCount,
                    value.ChangeImpact,
                    value.ChangeCategory,
                    value.RelatedTicketKeys,
                    value.IssueType,
                    value.TicketOrder,
                }),
            Structural = structural
                .OrderBy(value => value.ChangeOrder)
                .Select(value => new
                {
                    value.SourcePath,
                    value.ElementPath,
                    value.ChangeKind,
                    value.Detail,
                    value.TicketKeys,
                    value.ChangeOrder,
                }),
            Extensions = extensions
                .OrderBy(value => value.RefOrder)
                .Select(value => new
                {
                    value.ExtensionUrl,
                    value.ExtensionName,
                    value.ReplacementCoreElement,
                    value.Rationale,
                    value.RefOrder,
                }),
        });
        return AuthoringResultHasher.HashNormalizedUtf8(payload);
    }

    private static string ComputeStoredEvidenceHash(
        SqliteConnection connection,
        string noteId)
    {
        NoteRecord note = ReadNote(connection, noteId)
            ?? throw new KeyNotFoundException($"Note '{noteId}' was not found.");
        return ComputeEvidenceHash(
            note,
            ReadSourceFiles(connection, noteId),
            ReadCommits(connection, noteId),
            ReadTickets(connection, noteId),
            ReadStructuralChanges(connection, noteId),
            ReadExtensionRefs(connection, noteId));
    }

    public static string ComputeProseHash(BallotNoteProse prose)
    {
        ArgumentNullException.ThrowIfNull(prose);
        return AuthoringResultHasher.HashNormalizedUtf8(
            JsonSerializer.Serialize(new
            {
                NeedsNote = NormalizeNeedsNote(prose.NeedsNote),
                ProposedBallotNoteHtml =
                    prose.ProposedBallotNoteHtml ?? string.Empty,
                RollupSummaryMarkdown =
                    prose.RollupSummaryMarkdown ?? string.Empty,
                NotesForReviewerMarkdown =
                    prose.NotesForReviewerMarkdown ?? string.Empty,
                SourceFilesNote = prose.SourceFilesNote ?? string.Empty,
            }));
    }

    private static BallotNoteProse ToProse(NoteRecord note)
        => new()
        {
            NeedsNote = note.NeedsNote,
            ProposedBallotNoteHtml = note.ProposedBallotNoteHtml,
            RollupSummaryMarkdown = note.RollupSummaryMarkdown,
            NotesForReviewerMarkdown = note.NotesForReviewerMarkdown,
            SourceFilesNote = note.SourceFilesNote,
        };

    private static string NormalizeNeedsNote(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "yes" or "true" => "yes",
            "no" or "false" => "no",
            _ => "unknown",
        };

    private static void UpsertAuthoringState(
        SqliteConnection connection,
        string noteId,
        string classification,
        string evidenceHash,
        string evidenceRevision,
        string proseHash,
        string hydrationExecutionId,
        string? runId,
        string? runItemId,
        string? operationId,
        DateTimeOffset updatedAt)
    {
        using SqliteCommand command = connection.CreateCommand();
        ConfigureAuthoringStateCommand(
            command,
            noteId,
            classification,
            evidenceHash,
            evidenceRevision,
            proseHash,
            hydrationExecutionId,
            runId,
            runItemId,
            operationId,
            updatedAt);
        command.ExecuteNonQuery();
    }

    private static async Task UpsertAuthoringStateAsync(
        SqliteConnection connection,
        string noteId,
        string classification,
        string evidenceHash,
        string evidenceRevision,
        string proseHash,
        string hydrationExecutionId,
        string? runId,
        string? runItemId,
        string? operationId,
        DateTimeOffset updatedAt,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        ConfigureAuthoringStateCommand(
            command,
            noteId,
            classification,
            evidenceHash,
            evidenceRevision,
            proseHash,
            hydrationExecutionId,
            runId,
            runItemId,
            operationId,
            updatedAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void ConfigureAuthoringStateCommand(
        SqliteCommand command,
        string noteId,
        string classification,
        string evidenceHash,
        string evidenceRevision,
        string proseHash,
        string hydrationExecutionId,
        string? runId,
        string? runItemId,
        string? operationId,
        DateTimeOffset updatedAt)
    {
        command.CommandText =
            """
            INSERT INTO note_authoring_state(
                NoteId, Classification, EvidenceHash, EvidenceRevision, ProseHash,
                HydrationExecutionId, RunId, RunItemId, OperationId, UpdatedAt)
            VALUES(
                @noteId, @classification, @evidenceHash, @evidenceRevision, @proseHash,
                @hydrationExecutionId, @runId, @runItemId, @operationId, @updatedAt)
            ON CONFLICT(NoteId) DO UPDATE SET
                Classification = excluded.Classification,
                EvidenceHash = excluded.EvidenceHash,
                EvidenceRevision = excluded.EvidenceRevision,
                ProseHash = excluded.ProseHash,
                HydrationExecutionId = excluded.HydrationExecutionId,
                RunId = excluded.RunId,
                RunItemId = excluded.RunItemId,
                OperationId = excluded.OperationId,
                UpdatedAt = excluded.UpdatedAt
            """;
        command.Parameters.AddWithValue("@noteId", noteId);
        command.Parameters.AddWithValue("@classification", classification);
        command.Parameters.AddWithValue("@evidenceHash", evidenceHash);
        command.Parameters.AddWithValue("@evidenceRevision", evidenceRevision);
        command.Parameters.AddWithValue("@proseHash", proseHash);
        command.Parameters.AddWithValue(
            "@hydrationExecutionId",
            hydrationExecutionId);
        command.Parameters.AddWithValue("@runId", (object?)runId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@runItemId",
            (object?)runItemId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@operationId",
            (object?)operationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@updatedAt", Format(updatedAt));
    }

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static void InsertRecord(
        SqliteConnection connection,
        string table,
        object value)
    {
        System.Reflection.PropertyInfo[] properties = value.GetType()
            .GetProperties()
            .Where(property =>
                property.CanRead &&
                property.GetMethod is { IsStatic: false } &&
                !string.Equals(
                    property.Name,
                    "RowId",
                    StringComparison.Ordinal))
            .ToArray();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"INSERT INTO \"{table}\" (" +
            string.Join(", ", properties.Select(property =>
                $"\"{property.Name}\"")) +
            ") VALUES (" +
            string.Join(", ", properties.Select((_, index) => $"$p{index}")) +
            ")";
        for (int index = 0; index < properties.Length; index++)
        {
            object? propertyValue = properties[index].GetValue(value);
            object databaseValue = propertyValue switch
            {
                null => DBNull.Value,
                DateTimeOffset timestamp => Format(timestamp),
                _ => propertyValue,
            };
            command.Parameters.AddWithValue($"$p{index}", databaseValue);
        }
        command.ExecuteNonQuery();
    }

    private static void ExecuteRaw(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task ExecuteRawAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Format(DateTimeOffset value)
        => value.ToString("O", CultureInfo.InvariantCulture);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _startupOwnerLock?.Dispose();
            _startupOwnerLock = null;
        }
        base.Dispose(disposing);
    }

    private static void DeleteByNoteId(SqliteConnection connection, string table, string noteId)
        => DeleteByColumn(connection, table, "NoteId", noteId);

    private static void DeleteByColumn(SqliteConnection connection, string table, string column, string value)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM \"{table}\" WHERE \"{column}\" = $v";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private sealed record ExistingNoteProse(
        string ProposedBallotNoteHtml,
        string RollupSummaryMarkdown,
        string NotesForReviewerMarkdown,
        string SourceFilesNote,
        string NeedsNote,
        DateTimeOffset? AuthoredAt,
        DateTimeOffset GeneratedAt,
        string ProseHydrationExecutionId,
        string ProseEvidenceRevision,
        string CurrentAuthoringOperationId,
        string ProseVerificationStatus);

    private sealed record StagedHydrationEvidence(
        NoteRecord Note,
        IReadOnlyList<NoteSourceFileRecord> SourceFiles,
        IReadOnlyList<NoteCommitRecord> Commits,
        IReadOnlyList<NoteTicketRecord> Tickets,
        IReadOnlyList<NoteStructuralChangeRecord> StructuralChanges,
        IReadOnlyList<NoteExtensionRefRecord> ExtensionRefs);
}

public sealed record HydrationMutationLease(
    string RunId,
    string LeaseId);

public sealed record HydrationMembershipDefinition(
    string NoteId,
    string Type);
