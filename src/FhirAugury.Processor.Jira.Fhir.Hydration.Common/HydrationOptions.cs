namespace FhirAugury.Processor.Jira.Fhir.Hydration.Common;

/// <summary>
/// Configuration for the hydration startup sweep and admin-triggered
/// hydration on a processor service. Bound under the
/// <c>Processing:Hydration</c> section by the host.
/// </summary>
public sealed class HydrationOptions
{
    /// <summary>
    /// When true, the host runs a full hydration sweep
    /// (Specification backfill + per-ticket hydrate-all-unresolved) at
    /// startup, before the processing queue begins to drain. Defaults to
    /// true; set to false for local dev workflows that want to start
    /// fast against a deliberately-stale DB.
    /// </summary>
    public bool BackfillOnStartup { get; set; } = true;

    /// <summary>
    /// Maximum number of concurrent per-ticket hydration calls the sweep
    /// will issue. The hydrator itself is sequential internally; this
    /// bounds the fan-out across distinct tickets. Defaults to 4.
    /// </summary>
    public int MaxParallelism { get; set; } = 4;

    /// <summary>
    /// Optional path to a Jira source service SQLite database that the
    /// Specification backfill falls back to when the Jira source HTTP
    /// service is unreachable. When null/empty, only HTTP is attempted
    /// and the sweep hard-fails at startup if HTTP is unreachable.
    /// </summary>
    public string? JiraSourceDbPath { get; set; }

    public IEnumerable<string> Validate()
    {
        if (MaxParallelism < 1)
        {
            yield return "Processing:Hydration:MaxParallelism must be >= 1.";
        }
    }
}
