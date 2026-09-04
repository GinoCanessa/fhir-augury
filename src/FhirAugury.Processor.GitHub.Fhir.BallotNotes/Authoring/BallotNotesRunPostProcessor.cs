using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Authoring;

public sealed class BallotNotesRunPostProcessor(
    BallotNotesDatabase database,
    AuthoringRunStore authoringStore,
    SqliteReviewSnapshotReconciler snapshotReconciler,
    BallotNotesAuthoringRunCoordinator coordinator,
    IOptions<BallotNotesServiceOptions> optionsAccessor,
    ILogger<BallotNotesRunPostProcessor> logger)
    : BackgroundService
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
        if (!run.DatabaseOnly &&
            await database.CountLegacyUnverifiedAsync(ct) > 0)
        {
            await authoringStore.SupersedeRunAsync(
                runId,
                "Canonical snapshot blocked until all legacy-unverified notes are revalidated.",
                ct: ct);
            throw new InvalidOperationException(
                "A complete BallotNotes legacy revalidation run is required before the first canonical snapshot.");
        }

        AuthoringRunFinalizer finalizer = new(authoringStore);
        AuthoringSnapshotDescriptor? descriptor =
            await finalizer.FinalizeAsync(
                runId,
                [],
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
                ct);
        await coordinator.TryActivateNextQueuedRunAsync(ct);
        return descriptor;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        if (_options.ReconcileSnapshotsOnStartup)
        {
            await snapshotReconciler.ReconcileAsync(stoppingToken);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string mode = (await authoringStore.EnsureProcessorModeAsync(
                    coordinator.ProcessorKind,
                    ct: stoppingToken)).Mode;
                if (string.Equals(
                    mode,
                    AuthoringStatusValues.ProcessorModes.RunBacked,
                    StringComparison.Ordinal))
                {
                    foreach (string runId in
                        await database.ListRunsReadyForFinalizationAsync(
                            stoppingToken))
                    {
                        await FinalizeRunAsync(runId, stoppingToken);
                    }
                    await coordinator.TryActivateNextQueuedRunAsync(
                        stoppingToken);
                }
            }
            catch (OperationCanceledException)
                when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "BallotNotes run finalization pass failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
