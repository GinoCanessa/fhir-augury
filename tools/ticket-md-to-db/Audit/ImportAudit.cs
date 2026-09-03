using FhirAugury.Tools.TicketMdToDb.Compilation;

namespace FhirAugury.Tools.TicketMdToDb.Audit;

public sealed record ImportRunCounts(
    int Discovered,
    int Compiled,
    int BlockingDiagnostics,
    int Warnings,
    int OverriddenTickets,
    int OverriddenFields,
    int ExplicitlyMissingTickets,
    int ExplicitlyMissingFields);

public sealed record ImportRunEnvelope(
    string RunId,
    DateTimeOffset StartedAt,
    string InputRoot,
    string DestinationDatabase,
    string Orchestrator,
    string? OverridesPath,
    string AuditPath,
    string OverrideTemplatePath,
    bool DryRun,
    bool ReplaceExisting,
    bool AcceptUnresolvedHydration,
    string Status,
    ImportRunCounts Counts)
{
    public DateTimeOffset? CompletedAt { get; init; }
    public string? Failure { get; init; }
}

public sealed record ImportPersistenceOutcome(
    DateTimeOffset ImportedAt,
    int AttemptedTickets,
    int PersistedTickets,
    int PersistedRepositoryRows,
    int PersistedRelatedJiraRows,
    int PersistedRelatedZulipRows,
    int PersistedRelatedGitHubRows,
    bool DeepReadbackMatched);

public sealed record ImportHydrationIssue(
    string TicketKey,
    string SourcePath,
    string Code,
    string Message,
    bool Waivable,
    bool Waived);

public sealed record ImportHydrationOutcome(
    int AttemptedTickets,
    int SuccessfulBatches,
    int UnexpectedFailures,
    int ResolvedParents,
    int ResolvedSelfRows,
    int ProjectedSourceTickets,
    IReadOnlyList<ImportHydrationIssue> Issues);

public sealed record ImportReadinessOutcome(
    string Status,
    int FullyReadyTickets,
    IReadOnlyList<string> WorkGroups,
    IReadOnlyList<string> Partitions,
    IReadOnlyList<ImportHydrationIssue> Failures);

public sealed record ImportDatabaseOutcome(
    string Path,
    string Sha256,
    long Length);

public sealed record ImportPromotionOutcome(
    bool Replacement,
    bool Completed,
    string CandidateDatabasePath,
    string CandidateAuditPath,
    string? PriorDatabaseSha256,
    string PriorAuditClassification,
    string? DatabaseBackupPath,
    string? MatchingAuditBackupPath,
    string? PriorAuditEvidencePath,
    string? RecoveryCandidatePath);

public sealed record ImportAudit(
    ImportManifest Manifest,
    ImportRunEnvelope Run)
{
    public ImportPersistenceOutcome? Persistence { get; init; }
    public ImportHydrationOutcome? Hydration { get; init; }
    public ImportReadinessOutcome? Readiness { get; init; }
    public ImportDatabaseOutcome? Database { get; init; }
    public ImportPromotionOutcome? Promotion { get; init; }

    public static ImportAudit ForCompilation(
        CompilationResult compilation,
        string destinationDatabase,
        Uri orchestrator,
        string? overridesPath,
        string auditPath,
        string overrideTemplatePath,
        bool dryRun,
        bool replaceExisting,
        bool acceptUnresolvedHydration,
        DateTimeOffset? startedAt = null,
        string? runId = null)
    {
        ImportManifest manifest = compilation.Manifest;
        int overriddenTickets = manifest.Tickets.Count(ticket => ticket.Overrides.Count > 0);
        int overriddenFields = manifest.Tickets.Sum(ticket => ticket.Overrides.Count);
        ManifestScalar[] missing = manifest.Tickets
            .SelectMany(ticket => ticket.Scalars)
            .Where(scalar => scalar.SourceState is not ImportedValueState.Present)
            .ToArray();
        ImportRunCounts counts = new(
            manifest.Files.Count,
            manifest.Tickets.Count,
            manifest.Diagnostics.Count(diagnostic => diagnostic.IsBlocking),
            manifest.Diagnostics.Count(diagnostic => !diagnostic.IsBlocking),
            overriddenTickets,
            overriddenFields,
            manifest.Tickets.Count(ticket => ticket.Scalars.Any(
                scalar => scalar.SourceState is not ImportedValueState.Present)),
            missing.Length);
        ImportRunEnvelope run = new(
            runId ?? Guid.NewGuid().ToString("N"),
            startedAt ?? DateTimeOffset.UtcNow,
            compilation.InputRoot,
            Path.GetFullPath(destinationDatabase),
            orchestrator.AbsoluteUri,
            overridesPath is null ? null : Path.GetFullPath(overridesPath),
            Path.GetFullPath(auditPath),
            Path.GetFullPath(overrideTemplatePath),
            dryRun,
            replaceExisting,
            acceptUnresolvedHydration,
            manifest.IsValid ? "compiled" : "compile-failed",
            counts);
        return new ImportAudit(manifest, run);
    }
}
