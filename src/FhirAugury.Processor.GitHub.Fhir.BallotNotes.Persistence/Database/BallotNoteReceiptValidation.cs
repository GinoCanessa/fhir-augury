using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;

public static class BallotNoteReceiptValidation
{
    public const string CurrentReceiptBackedPredicate =
        """
        n.ProseVerificationStatus = 'receipt-backed'
        AND n.ProseHydrationExecutionId = n.CurrentHydrationExecutionId
        AND n.ProseEvidenceRevision = n.CurrentEvidenceRevision
        AND EXISTS(
            SELECT 1
            FROM note_authoring_state s
            INNER JOIN authoring_result_receipts r
                ON r.OperationId = s.OperationId
               AND r.RunId = s.RunId
               AND r.RunItemId = s.RunItemId
               AND r.BusinessKey = s.NoteId COLLATE NOCASE
               AND r.ContentHash = s.ProseHash
               AND r.ExpectedSourceRevision = s.EvidenceRevision
               AND r.ObservedSourceRevision = s.EvidenceRevision
            INNER JOIN authoring_run_items i
                ON i.Id = s.RunItemId
               AND i.RunId = s.RunId
               AND i.BusinessKey = s.NoteId COLLATE NOCASE
               AND i.ItemKind = n.Type COLLATE NOCASE
               AND i.ExpectedSourceRevision = s.EvidenceRevision
               AND i.AcceptedReceiptId = r.Id
               AND i.Status IN ('complete', 'superseded')
            INNER JOIN authoring_runs run
                ON run.Id = s.RunId
               AND run.ProcessorKind = 'github-fhir-ballot-notes'
               AND run.AuthoringEpoch = r.AuthoringEpoch
            INNER JOIN notes_hydration_run_items h
                ON h.ExecutionId = s.HydrationExecutionId
               AND h.NoteId = s.NoteId COLLATE NOCASE
               AND h.Type = n.Type COLLATE NOCASE
               AND h.EvidenceHash = s.EvidenceHash
               AND h.EvidenceRevision = s.EvidenceRevision
               AND h.Status = 'completed'
            WHERE s.NoteId = n.NoteId COLLATE NOCASE
              AND s.Classification = 'receipt-backed'
              AND s.OperationId = n.CurrentAuthoringOperationId
              AND s.EvidenceHash = n.CurrentEvidenceHash
              AND s.EvidenceRevision = n.CurrentEvidenceRevision
              AND s.ProseHash = ballot_note_prose_hash(
                  n.NeedsNote,
                  n.ProposedBallotNoteHtml,
                  n.RollupSummaryMarkdown,
                  n.NotesForReviewerMarkdown,
                  n.SourceFilesNote)
              AND s.HydrationExecutionId = n.CurrentHydrationExecutionId
        )
        """;

    public static void RegisterFunctions(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.CreateFunction<string?, string?, string?, string?, string?, string>(
            "ballot_note_prose_hash",
            static (
                needsNote,
                proposedBallotNoteHtml,
                rollupSummaryMarkdown,
                notesForReviewerMarkdown,
                sourceFilesNote) =>
                BallotNotesDatabase.ComputeProseHash(
                    new BallotNoteProse
                    {
                        NeedsNote = needsNote ?? string.Empty,
                        ProposedBallotNoteHtml =
                            proposedBallotNoteHtml ?? string.Empty,
                        RollupSummaryMarkdown =
                            rollupSummaryMarkdown ?? string.Empty,
                        NotesForReviewerMarkdown =
                            notesForReviewerMarkdown ?? string.Empty,
                        SourceFilesNote = sourceFilesNote ?? string.Empty,
                    }),
            isDeterministic: true);
    }
}
