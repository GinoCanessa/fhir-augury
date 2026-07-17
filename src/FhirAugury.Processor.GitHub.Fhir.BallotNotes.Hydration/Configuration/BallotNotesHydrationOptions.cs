namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;

/// <summary>
/// Options for the BallotNotes hydration pipeline: where cloned repos live and
/// which upstream services back ticket attribution. Cross-reference / ticket
/// detail lookups try <see cref="OrchestratorAddress"/> first and fall back to
/// <see cref="JiraSourceAddress"/>.
/// </summary>
public sealed class BallotNotesHydrationOptions
{
    /// <summary>Root holding per-repo clones at <c>&lt;CloneRoot&gt;/&lt;owner&gt;_&lt;name&gt;/clone</c>.</summary>
    public string CloneRoot { get; set; } = "./cache/github/repos";

    /// <summary>
    /// Path to the read-only GitHub source SQLite DB used to cross-reference
    /// extensions (the <c>HL7/fhir-extensions</c> pack) and resolve owning work
    /// groups from the JIRA-Spec-Artifacts registry. Empty/missing disables those
    /// lookups (best-effort).
    /// </summary>
    public string GitHubDbPath { get; set; } = "./cache/github.db";

    /// <summary>
    /// Path to the read-only current-build FHIR R6 reference DB
    /// (<c>Structures.WorkGroup</c>) used as an owning-WG fallback for artifacts.
    /// Preferred over <see cref="FhirSpecDbPath"/>. Empty/missing is allowed
    /// (best-effort).
    /// </summary>
    public string FhirR6DbPath { get; set; } = "./cache/fhir-r6.db";

    /// <summary>
    /// Path to the read-only published multi-release FHIR spec reference DB
    /// (<c>Structures.WorkGroup</c>), used as the owning-WG fallback for artifacts
    /// when <see cref="FhirR6DbPath"/> is absent. Empty/missing is allowed
    /// (best-effort).
    /// </summary>
    public string FhirSpecDbPath { get; set; } = "./cache/fhir-spec.db";

    /// <summary>Primary attribution upstream (orchestrator cross-source aggregation).</summary>
    public string OrchestratorAddress { get; set; } = "http://localhost:5150";

    /// <summary>Fallback attribution upstream (Jira source) when the orchestrator is unreachable.</summary>
    public string JiraSourceAddress { get; set; } = "http://localhost:5160";

    /// <summary>Maximum number of units hydrated concurrently.</summary>
    public int MaxParallelism { get; set; } = 4;

    /// <summary>
    /// Bounds the TCP connect phase of best-effort attribution lookups so they fail
    /// fast against an unreachable or black-holed upstream (orchestrator / Jira)
    /// instead of stalling on the OS connect timeout. Applied to the
    /// <c>TicketAttributor</c> typed client's primary handler.
    /// </summary>
    public TimeSpan AttributionConnectTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Validates the options. Returns human-readable errors; empty means valid.</summary>
    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(CloneRoot))
        {
            yield return "BallotNotes hydration CloneRoot must be configured.";
        }

        if (string.IsNullOrWhiteSpace(OrchestratorAddress) && string.IsNullOrWhiteSpace(JiraSourceAddress))
        {
            yield return "At least one of OrchestratorAddress or JiraSourceAddress must be configured for attribution.";
        }

        if (MaxParallelism < 1)
        {
            yield return "MaxParallelism must be greater than or equal to 1.";
        }

        if (AttributionConnectTimeout <= TimeSpan.Zero)
        {
            yield return "BallotNotes hydration AttributionConnectTimeout must be greater than zero.";
        }
    }
}
