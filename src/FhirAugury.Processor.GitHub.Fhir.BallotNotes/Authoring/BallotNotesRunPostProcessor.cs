using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Authoring;

public sealed class BallotNotesRunPostProcessor(
    BallotNotesDatabase database,
    AuthoringRunStore authoringStore,
    AuthoringRunFinalizer finalizer,
    SqliteReviewSnapshotReconciler snapshotReconciler,
    BallotNotesAuthoringRunCoordinator coordinator,
    IOptions<BallotNotesServiceOptions> optionsAccessor)
    : IAuthoringRunFinalizationStrategy
{
    private readonly BallotNotesServiceOptions _options =
        optionsAccessor.Value;

    public async Task<AuthoringSnapshotDescriptor?> FinalizeRunAsync(
        string runId,
        CancellationToken ct = default)
    {
        AuthoringRunRecord run = await authoringStore.GetRunAsync(runId, ct)
            ?? throw new KeyNotFoundException(
                $"Authoring run '{runId}' was not found.");
        AuthoringProcessorModeRecord mode =
            await authoringStore.GetProcessorModeAsync(
                coordinator.ProcessorKind,
                ct: ct);
        bool initialRevalidation = mode.RevalidationRequired &&
            string.Equals(mode.RevalidationRunId, runId, StringComparison.Ordinal);
        if (initialRevalidation &&
            await coordinator.SupersedeStaleItemsAsync(runId, ct))
        {
            throw new AuthoringConflictException(
                AuthoringConflictCode.SourceRevisionMismatch,
                $"Initial revalidation run '{runId}' was replaced because evidence revisions changed before finalization.");
        }
        int blockingLegacyRows = initialRevalidation
            ? await database.CountLegacyUnverifiedNotSupersededByRunAsync(
                runId,
                ct)
            : await database.CountLegacyUnverifiedAsync(ct);
        if (!run.DatabaseOnly && blockingLegacyRows > 0)
        {
            await authoringStore.SupersedeRunAsync(
                runId,
                "Canonical snapshot blocked until all legacy-unverified notes are revalidated.",
                ct: ct);
            throw new InvalidOperationException(
                "A complete BallotNotes legacy revalidation run is required before the first canonical snapshot.");
        }

        IReadOnlyList<AuthoringRunItemRecord> runItems =
            await authoringStore.GetRunItemsAsync(runId, ct);
        List<AuthoringFinalizationStage> stages = [];
        if (initialRevalidation)
        {
            string retirementFingerprint = AuthoringResultHasher.HashNormalizedUtf8(
                string.Join(
                    "\n",
                    runItems
                        .Where(item =>
                            item.Status == AuthoringStatusValues.Items.Superseded)
                        .OrderBy(item => item.RowId)
                        .Select(item => $"{item.Id}:{item.BusinessKey}")));
            stages.Add(new AuthoringFinalizationStage(
                "revalidation-retirement",
                "",
                retirementFingerprint,
                (lease, cancellationToken) =>
                    database.RetireSupersededLegacyNotesAsync(
                        runId,
                        lease,
                        retirementFingerprint,
                        cancellationToken)));
        }

        Func<SqliteConnection, CancellationToken, Task>? completionGuard =
            initialRevalidation
                ? (connection, cancellationToken) =>
                    BallotNotesDatabase.EnsureRunEvidenceRevisionsCurrentAsync(
                        connection,
                        runId,
                        cancellationToken)
                : null;
        try
        {
            return await finalizer.FinalizeAsync(
                runId,
                stages,
                run.DatabaseOnly
                    ? null
                    : async cancellationToken =>
                    {
                        IReadOnlyList<AuthoringRunItemRecord> items =
                            await authoringStore.GetRunItemsAsync(
                                runId,
                                cancellationToken);
                        IReadOnlyDictionary<string, long> counts =
                            await database.GetSnapshotTableCountsAsync(
                                cancellationToken);
                        SqliteReviewSnapshotWriter writer = new(
                            database.OpenConnection,
                            authoringStore);
                        return await writer.WriteAsync(
                            new SqliteReviewSnapshotRequest(
                                coordinator.ProcessorKind,
                                runId,
                                Path.GetFullPath(
                                    _options.SnapshotDirectory),
                                    _options.SnapshotSchemaVersion,
                                    items.Count(item =>
                                        item.Status is
                                            AuthoringStatusValues.Items.Complete or
                                            AuthoringStatusValues.Items.Superseded),
                                    await database.CountCurrentReceiptBackedNotesAsync(
                                        cancellationToken),
                                    counts,
                                    new BallotNotesSnapshotSanitizer(runId)),
                            cancellationToken);
                    },
                completionGuard,
                ct);
        }
        catch (AuthoringConflictException ex)
            when (initialRevalidation &&
                  ex.Code == AuthoringConflictCode.SourceRevisionMismatch)
        {
            if (await coordinator.SupersedeStaleItemsAsync(runId, ct))
            {
                throw new AuthoringConflictException(
                    AuthoringConflictCode.SourceRevisionMismatch,
                    $"Initial revalidation run '{runId}' was replaced because evidence revisions changed during finalization.");
            }
            throw;
        }
    }

    public Task ReconcileSnapshotsOnStartupAsync(CancellationToken ct)
        => snapshotReconciler.ReconcileAsync(ct);

    async Task IAuthoringRunFinalizationStrategy.FinalizeRunAsync(
        string runId,
        CancellationToken ct)
        => _ = await FinalizeRunAsync(runId, ct);
}
