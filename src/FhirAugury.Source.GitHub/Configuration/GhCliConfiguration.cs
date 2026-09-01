namespace FhirAugury.Source.GitHub.Configuration;

/// <summary>
/// Configuration for the gh CLI data provider.
/// </summary>
public class GhCliConfiguration
{
    /// <summary>Path to the gh executable (default: "gh", found via PATH).</summary>
    public string ExecutablePath { get; set; } = "gh";

    /// <summary>
    /// Maximum results per gh command invocation.
    /// gh list commands can exceed 1000; gh search commands cap at 1000.
    /// </summary>
    public int Limit { get; set; } = 1000;

    /// <summary>
    /// Hostname for GitHub Enterprise. Leave null for github.com.
    /// Equivalent to GH_HOST environment variable.
    /// </summary>
    public string? Hostname { get; set; }

    /// <summary>Timeout for individual gh process invocations.</summary>
    public string ProcessTimeout { get; set; } = "00:05:00";

    /// <summary>
    /// Maximum number of concurrent gh CLI processes. Default is 1 to prevent
    /// CLI state file contention and rate-limit pressure.
    /// </summary>
    public int MaxConcurrentProcesses { get; set; } = 1;

    /// <summary>
    /// Maximum results per gh list command during a per-repo history backfill
    /// (the one-time full-history fetch that drops the <c>updated:&gt;=</c> bound).
    /// Set high to favor full-lifetime completeness; the backfill is one-time per
    /// repo (gated by a <c>backfill:&lt;repo&gt;</c> sync-state marker) and is
    /// rate-limited via <see cref="MaxConcurrentProcesses"/>.
    /// </summary>
    public int BackfillLimit { get; set; } = 5000;

    /// <summary>
    /// How many items a history backfill processes between durable checkpoint writes.
    /// A hard kill loses at most one interval of work; the graceful path always
    /// checkpoints on exit regardless of this value.
    /// </summary>
    public int BackfillCheckpointInterval { get; set; } = 250;

    /// <summary>
    /// Consecutive resume passes that may fail to shrink the pending-retry set before the
    /// repo is marked complete anyway (with a warning naming the abandoned items). Bounds
    /// the case where an item is permanently unfetchable — a deleted or transferred PR —
    /// so it cannot recreate a backfill that never completes.
    /// </summary>
    public int BackfillMaxRepairPasses { get; set; } = 3;

    /// <summary>Parses <see cref="ProcessTimeout"/> as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan GetProcessTimeout() => TimeSpan.TryParse(ProcessTimeout, out TimeSpan ts) ? ts : TimeSpan.FromMinutes(5);
}
