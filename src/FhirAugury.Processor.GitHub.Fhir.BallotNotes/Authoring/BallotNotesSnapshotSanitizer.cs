using FhirAugury.Processing.Common.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Authoring;

public sealed class BallotNotesSnapshotSanitizer(string runId)
    : AuthoringSnapshotSanitizer(CreateTables())
{
    public override async Task SanitizeAsync(
        SqliteConnection connection,
        CancellationToken ct = default)
    {
        BallotNoteReceiptValidation.RegisterFunctions(connection);
        await ExecuteAsync(
            connection,
            $"""
            CREATE TEMP TABLE current_snapshot_notes AS
            SELECT n.NoteId, r.Id AS ReceiptId, r.RunItemId, r.RunId
            FROM notes n
            INNER JOIN note_authoring_state s
                ON s.NoteId = n.NoteId
            INNER JOIN authoring_result_receipts r
                ON r.Id = (
                    SELECT accepted.Id
                    FROM authoring_result_receipts accepted
                    WHERE accepted.OperationId = s.OperationId
                      AND accepted.RunId = s.RunId
                      AND accepted.RunItemId = s.RunItemId
                    LIMIT 1
                )
            WHERE {BallotNoteReceiptValidation.CurrentReceiptBackedPredicate};

            DELETE FROM note_source_files
            WHERE NoteId NOT IN (SELECT NoteId FROM current_snapshot_notes);
            DELETE FROM note_commits
            WHERE NoteId NOT IN (SELECT NoteId FROM current_snapshot_notes);
            DELETE FROM note_tickets
            WHERE NoteId NOT IN (SELECT NoteId FROM current_snapshot_notes);
            DELETE FROM note_structural_changes
            WHERE NoteId NOT IN (SELECT NoteId FROM current_snapshot_notes);
            DELETE FROM note_extension_refs
            WHERE NoteId NOT IN (SELECT NoteId FROM current_snapshot_notes);
            DELETE FROM notes
            WHERE NoteId NOT IN (SELECT NoteId FROM current_snapshot_notes);

            DELETE FROM notes_hydration_run_items
            WHERE NoteId NOT IN (SELECT NoteId FROM current_snapshot_notes)
               OR Status <> 'completed'
               OR ExecutionId NOT IN (
                   SELECT DISTINCT CurrentHydrationExecutionId FROM notes
               );
            DELETE FROM notes_hydration_executions
            WHERE Id NOT IN (
                SELECT DISTINCT ExecutionId FROM notes_hydration_run_items
            );

            DELETE FROM authoring_result_receipts
            WHERE Id NOT IN (
                SELECT ReceiptId FROM current_snapshot_notes
            );
            DELETE FROM authoring_run_items
            WHERE RunId <> @runId
              AND Id NOT IN (
                  SELECT RunItemId FROM current_snapshot_notes
              );
            DELETE FROM authoring_runs
            WHERE Id <> @runId
              AND Id NOT IN (
                  SELECT RunId FROM current_snapshot_notes
              );
            """,
            ct,
            ("@runId", runId));

        await base.SanitizeAsync(connection, ct);
    }

    private static IReadOnlyList<AuthoringSnapshotTable> CreateTables()
        =>
        [
            .. AuthoringSnapshotSanitizer.GetCoreTableDefinitions(),
            new("notes"),
            new("note_source_files"),
            new("note_commits"),
            new("note_tickets"),
            new("note_structural_changes"),
            new("note_extension_refs"),
            new(
                "notes_hydration_executions",
                [
                    "Id",
                    "RunKey",
                    "RepoOwner",
                    "RepoName",
                    "RepoCategory",
                    "SinceSha",
                    "SinceShortSha",
                    "HeadSha",
                    "HeadShortSha",
                    "WindowLabel",
                    "Status",
                    "UnitsTotal",
                    "UnitsHydrated",
                    "CommitsInWindow",
                    "TicketsAttributed",
                    "IsCutoverBaseline",
                    "StartedAt",
                    "CompletedAt",
                ]),
            new(
                "notes_hydration_run_items",
                [
                    "Id",
                    "ExecutionId",
                    "NoteId",
                    "Type",
                    "ItemOrder",
                    "Status",
                    "EvidenceHash",
                    "EvidenceRevision",
                    "HydratedAt",
                ]),
        ];

    private static async Task ExecuteAsync(
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
        await command.ExecuteNonQueryAsync(ct);
    }
}
