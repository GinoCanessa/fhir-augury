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

    /// <summary>Primary attribution upstream (orchestrator cross-source aggregation).</summary>
    public string OrchestratorAddress { get; set; } = "http://localhost:5150";

    /// <summary>Fallback attribution upstream (Jira source) when the orchestrator is unreachable.</summary>
    public string JiraSourceAddress { get; set; } = "http://localhost:5160";

    /// <summary>Maximum number of units hydrated concurrently.</summary>
    public int MaxParallelism { get; set; } = 4;

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
    }
}
