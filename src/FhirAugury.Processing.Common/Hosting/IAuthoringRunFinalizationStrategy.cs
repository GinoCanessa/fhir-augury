namespace FhirAugury.Processing.Common.Hosting;

public interface IAuthoringRunFinalizationStrategy
{
    Task ReconcileSnapshotsOnStartupAsync(CancellationToken ct);

    Task FinalizeRunAsync(string runId, CancellationToken ct);
}
