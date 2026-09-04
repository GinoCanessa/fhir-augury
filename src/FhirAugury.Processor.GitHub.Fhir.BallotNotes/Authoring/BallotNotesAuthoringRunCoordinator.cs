using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Authoring;

public sealed record BallotNotesAuthoringRunCreation(
    NotesHydrationExecutionRecord Execution,
    AuthoringRunRecord Run,
    IReadOnlyList<AuthoringRunItemRecord> Items);

public sealed class BallotNotesAuthoringRunCoordinator(
    BallotNotesDatabase database,
    AuthoringRunStore authoringStore)
{
    public string ProcessorKind => BallotNotesDatabase.AuthoringProcessorKind;

    public async Task<BallotNotesAuthoringRunCreation> CreateRunAsync(
        BallotNotesAuthoringRunRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        NotesHydrationExecutionRecord execution =
            database.ResolveAuthoringExecution(
                request.HydrationExecutionId,
                request.NoteIds);
        IReadOnlyList<NotesHydrationRunItemRecord> hydrationItems =
            database.GetCompletedHydrationItems(execution.Id, request.NoteIds);
        if (hydrationItems.Count == 0)
        {
            throw new ArgumentException(
                $"Hydration execution '{execution.Id}' contains no completed notes.");
        }

        AuthoringRunItemDefinition[] definitions = hydrationItems
            .Select(item => new AuthoringRunItemDefinition(
                item.NoteId,
                item.Type,
                item.EvidenceRevision))
            .ToArray();
        string runId = database.CreateOrReplayAuthoringRun(
            definitions,
            request.DatabaseOnly);
        await TryActivateNextQueuedRunAsync(ct);
        return new BallotNotesAuthoringRunCreation(
            execution,
            (await authoringStore.GetRunAsync(runId, ct))!,
            await authoringStore.GetRunItemsAsync(runId, ct));
    }

    public async Task<bool> TryActivateNextQueuedRunAsync(
        CancellationToken ct = default)
    {
        string mode = (await authoringStore.GetProcessorModeAsync(
            ProcessorKind,
            ct)).Mode;
        if (!string.Equals(
            mode,
            AuthoringStatusValues.ProcessorModes.RunBacked,
            StringComparison.Ordinal))
        {
            return false;
        }

        while (true)
        {
            string? runId = await GetOldestQueuedRunIdAsync(ct);
            if (runId is null)
            {
                return false;
            }
            if (await SupersedeStaleItemsAsync(runId, ct))
            {
                continue;
            }
            return await authoringStore.TryAcquireMutationFenceAsync(
                ProcessorKind,
                runId,
                ct: ct);
        }
    }

    public async Task<bool> SupersedeStaleItemsAsync(
        string runId,
        CancellationToken ct = default)
    {
        IReadOnlyList<AuthoringRunItemRecord> items =
            await authoringStore.GetRunItemsAsync(runId, ct);
        List<string> stale = [];
        foreach (AuthoringRunItemRecord item in items.Where(value =>
                     value.AcceptedReceiptId is null &&
                     value.Status is not (
                         AuthoringStatusValues.Items.Complete or
                         AuthoringStatusValues.Items.Superseded)))
        {
            Persistence.Models.NoteDetail? current =
                database.GetNote(item.BusinessKey);
            if (current is null ||
                !string.Equals(
                    current.Note.Type,
                    item.ItemKind,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    current.Note.CurrentEvidenceRevision,
                    item.ExpectedSourceRevision,
                    StringComparison.Ordinal))
            {
                stale.Add(item.Id);
            }
        }
        return stale.Count > 0 &&
            await authoringStore.SupersedeRunItemsAsync(
                runId,
                stale,
                "A newer BallotNotes hydration execution replaced one or more frozen run items.",
                ct: ct);
    }

    private async Task<string?> GetOldestQueuedRunIdAsync(
        CancellationToken ct)
    {
        await using SqliteConnection connection = database.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id
            FROM authoring_runs
            WHERE ProcessorKind = @processorKind AND Status = @status
            ORDER BY CreatedAt, RowId
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@processorKind", ProcessorKind);
        command.Parameters.AddWithValue(
            "@status",
            AuthoringStatusValues.Runs.Queued);
        return (string?)await command.ExecuteScalarAsync(ct);
    }
}
