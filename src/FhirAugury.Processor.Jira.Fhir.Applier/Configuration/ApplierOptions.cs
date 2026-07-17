namespace FhirAugury.Processor.Jira.Fhir.Applier.Configuration;

public sealed class ApplierCommitOptions
{
    public string AuthorName { get; set; } = "FhirAugury Applier";
    public string AuthorEmail { get; set; } = "applier@fhir-augury.local";
    public string MessageTemplate { get; set; } =
        "Apply {ticketKey} ({plannerCompletionId})\n\nAppliedBy: fhir-augury-applier\nApplyCompletionId: {applyCompletionId}\nPlannerCompletionId: {plannerCompletionId}\nPlannerCompletedAt: {plannerCompletedAt}";

    public string FailureMessageTemplate { get; set; } =
        "Apply {ticketKey} FAILED ({failureKind})\n\nAppliedBy: fhir-augury-applier\nApplyCompletionId: {applyCompletionId}\nPlannerCompletionId: {plannerCompletionId}\nFailure: {failureSummary}";
}

public sealed class ApplierPushOptions
{
    public string RemoteName { get; set; } = "origin";
}

/// <summary>
/// Top-level <c>Processing:Applier</c> options block. Holds working-directory layout,
/// per-repo settings, the volatile-output regex set used by <c>OutputDiffer</c>, and the
/// commit / push templates used by the worktree commit + push services.
/// </summary>
public sealed class ApplierOptions
{
    public const string SectionName = "Processing:Applier";

    public string WorkingDirectory { get; set; } = "./data/applier-workspaces";
    public string OutputDirectory { get; set; } = "./out/applier";

    /// <summary>
    /// Path to the planner database the applier reads from to discover completed
    /// tickets. Required at runtime.
    /// </summary>
    public string PlannerDatabasePath { get; set; } = "./data/processor.jira.fhir.planner.db";

    public List<ApplierRepoOptions> Repos { get; set; } = [];

    /// <summary>
    /// Regex patterns applied to text-file output before diffing the worktree against
    /// the baseline. Each pattern's matches are removed before comparison so that
    /// generation timestamps / build IDs / similar volatile content do not register as
    /// real changes. Operator-overridable.
    /// </summary>
    public List<string> VolatileOutputPatterns { get; set; } =
    [
        @"<meta[^>]*generated[^>]*/?>",
        @"Generated\s+at[^\r\n<]*",
        @"<!--\s*Build\s+timestamp:[^>]*-->",
        @"\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})\b"
    ];

    public ApplierCommitOptions Commit { get; set; } = new();
    public ApplierPushOptions Push { get; set; } = new();

    public string BaselineSyncSchedule { get; set; } = "01:00:00";
    public string BaselineMinSyncAge { get; set; } = "00:30:00";
    public bool BaselineRefreshOnStartup { get; set; } = true;

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            yield return "Processing:Applier:WorkingDirectory must be non-empty.";
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            yield return "Processing:Applier:OutputDirectory must be non-empty.";
        }

        if (string.IsNullOrWhiteSpace(PlannerDatabasePath))
        {
            yield return "Processing:Applier:PlannerDatabasePath must be non-empty.";
        }

        if (Repos.Count == 0)
        {
            yield return "Processing:Applier:Repos must include at least one entry.";
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (ApplierRepoOptions repo in Repos)
        {
            if (string.IsNullOrWhiteSpace(repo.Owner) || string.IsNullOrWhiteSpace(repo.Name))
            {
                yield return "Processing:Applier:Repos entries must have non-empty Owner and Name.";
                continue;
            }

            if (string.IsNullOrWhiteSpace(repo.BuildCommand))
            {
                yield return $"Processing:Applier:Repos[{repo.FullName}].BuildCommand must be non-empty.";
            }

            if (repo.OutputRoots.Count == 0)
            {
                yield return $"Processing:Applier:Repos[{repo.FullName}].OutputRoots must include at least one glob.";
            }

            if (!seen.Add(repo.FullName))
            {
                yield return $"Processing:Applier:Repos contains duplicate entry for '{repo.FullName}'.";
            }
        }

        if (string.IsNullOrWhiteSpace(Commit.AuthorName) || string.IsNullOrWhiteSpace(Commit.AuthorEmail))
        {
            yield return "Processing:Applier:Commit.AuthorName / AuthorEmail must be non-empty.";
        }

        if (string.IsNullOrWhiteSpace(Commit.MessageTemplate) || string.IsNullOrWhiteSpace(Commit.FailureMessageTemplate))
        {
            yield return "Processing:Applier:Commit.MessageTemplate / FailureMessageTemplate must be non-empty.";
        }

        if (!TimeSpan.TryParse(BaselineSyncSchedule, out TimeSpan syncSchedule) || syncSchedule <= TimeSpan.Zero)
        {
            yield return "Processing:Applier:BaselineSyncSchedule must be a positive TimeSpan string.";
        }

        if (!TimeSpan.TryParse(BaselineMinSyncAge, out TimeSpan minAge) || minAge < TimeSpan.Zero)
        {
            yield return "Processing:Applier:BaselineMinSyncAge must be a non-negative TimeSpan string.";
        }
    }
}
