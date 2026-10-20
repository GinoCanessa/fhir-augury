using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Authoring;

public sealed record BallotNotesAuthoringRunCreation(
    NotesHydrationExecutionRecord Execution,
    AuthoringRunRecord Run,
    IReadOnlyList<AuthoringRunItemRecord> Items);

public sealed class BallotNotesAuthoringRunCoordinator(
    BallotNotesDatabase database,
    AuthoringRunStore authoringStore)
    : IAuthoringRunLifecycleAdapter
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
        return new BallotNotesAuthoringRunCreation(
            execution,
            (await authoringStore.GetRunAsync(runId, ct))!,
            await authoringStore.GetRunItemsAsync(runId, ct));
    }

    public async Task<AuthoringRunReconciliationResult> ReconcileRunAsync(
        AuthoringRunRecord run,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!string.Equals(run.ProcessorKind, ProcessorKind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Run '{run.Id}' belongs to processor '{run.ProcessorKind}', not '{ProcessorKind}'.");
        }

        AuthoringProcessorModeRecord mode =
            await authoringStore.GetProcessorModeAsync(ProcessorKind, ct);
        bool initialRevalidation = mode.RevalidationRequired &&
            string.Equals(mode.RevalidationRunId, run.Id, StringComparison.Ordinal);
        if (!await SupersedeStaleItemsAsync(run.Id, ct))
        {
            return AuthoringRunReconciliationResult.Current;
        }

        if (initialRevalidation)
        {
            AuthoringProcessorModeRecord refreshed =
                await authoringStore.GetProcessorModeAsync(ProcessorKind, ct);
            return new AuthoringRunReconciliationResult(
                AuthoringRunReconciliationOutcome.Replaced,
                refreshed.RevalidationRunId);
        }

        return new AuthoringRunReconciliationResult(
            AuthoringRunReconciliationOutcome.Superseded);
    }

    public async Task<bool> SupersedeStaleItemsAsync(
        string runId,
        CancellationToken ct = default)
    {
        AuthoringProcessorModeRecord mode =
            await authoringStore.GetProcessorModeAsync(ProcessorKind, ct);
        bool initialRevalidation = mode.RevalidationRequired &&
            string.Equals(
                mode.RevalidationRunId,
                runId,
                StringComparison.Ordinal);
        IReadOnlyList<AuthoringRunItemRecord> items = initialRevalidation
            ? await authoringStore.GetRevalidationCorpusItemsAsync(runId, ct)
            : await authoringStore.GetRunItemsAsync(runId, ct);
        List<string> stale = [];
        foreach (AuthoringRunItemRecord item in items.Where(value =>
                     value.Status != AuthoringStatusValues.Items.Superseded &&
                     (initialRevalidation ||
                      value.AcceptedReceiptId is null)))
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
        if (stale.Count == 0)
        {
            return false;
        }

        if (initialRevalidation)
        {
            HashSet<string> staleIds = stale.ToHashSet(
                StringComparer.Ordinal);
            bool hasUnfinishedAcceptedCurrentItem = items.Any(item =>
                item.Status != AuthoringStatusValues.Items.Superseded &&
                item.Status != AuthoringStatusValues.Items.Complete &&
                item.AcceptedReceiptId is not null &&
                !staleIds.Contains(item.Id));
            if (hasUnfinishedAcceptedCurrentItem)
            {
                return false;
            }

            List<AuthoringRunItemDefinition> replacements = [];
            foreach (AuthoringRunItemRecord item in items.Where(value =>
                         value.Status != AuthoringStatusValues.Items.Superseded &&
                         (value.AcceptedReceiptId is null ||
                          staleIds.Contains(value.Id))))
            {
                Persistence.Models.NoteDetail? current =
                    database.GetNote(item.BusinessKey);
                if (current is null)
                {
                    throw new AuthoringConflictException(
                        AuthoringConflictCode.SourceRevisionMismatch,
                        $"BallotNotes unit '{item.BusinessKey}' disappeared during initial revalidation.");
                }
                replacements.Add(new AuthoringRunItemDefinition(
                    current.Note.NoteId,
                    current.Note.Type,
                    current.Note.CurrentEvidenceRevision));
            }
            await authoringStore.ReplaceRevalidationRunAsync(
                ProcessorKind,
                runId,
                replacements,
                ct: ct);
            return true;
        }

        return await authoringStore.SupersedeRunItemsAsync(
            runId,
            stale,
            "A newer BallotNotes hydration execution replaced one or more frozen run items.",
            ct: ct);
    }

}
