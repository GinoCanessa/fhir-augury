using FhirAugury.Processing.Common.Database.Records;

namespace FhirAugury.Processing.Common.Queue;

public enum AuthoringRunReconciliationOutcome
{
    Current,
    Replaced,
    Superseded,
}

public sealed record AuthoringRunReconciliationResult(
    AuthoringRunReconciliationOutcome Outcome,
    string? ReplacementRunId = null)
{
    public static AuthoringRunReconciliationResult Current { get; } =
        new(AuthoringRunReconciliationOutcome.Current);
}

public interface IAuthoringRunLifecycleAdapter
{
    string ProcessorKind { get; }

    Task<AuthoringRunReconciliationResult> ReconcileRunAsync(
        AuthoringRunRecord run,
        CancellationToken ct);
}
