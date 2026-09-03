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
    ImportRunCounts Counts);

public sealed record ImportAudit(
    ImportManifest Manifest,
    ImportRunEnvelope Run)
{
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
