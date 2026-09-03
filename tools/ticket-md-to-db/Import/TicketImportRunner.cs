using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Hydration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Tools.TicketMdToDb.Audit;
using FhirAugury.Tools.TicketMdToDb.Compilation;
using FhirAugury.Tools.TicketMdToDb.Hydration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Tools.TicketMdToDb.Import;

public sealed record TicketImportRunResult(
    CompilationResult Compilation,
    ImportAudit Audit,
    int ExitCode,
    string? EvidencePath,
    IReadOnlyList<string> Messages);

public sealed class TicketImportRunner(
    Func<string>? runIdFactory = null,
    Func<DateTimeOffset>? clock = null,
    HttpMessageHandler? orchestratorHandler = null,
    Action<PromotionBoundary>? promotionFaultInjector = null)
{
    private readonly Func<string> _runIdFactory =
        runIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    private readonly Func<DateTimeOffset> _clock =
        clock ?? (() => DateTimeOffset.UtcNow);
    private readonly HttpMessageHandler? _orchestratorHandler = orchestratorHandler;
    private readonly Action<PromotionBoundary>? _promotionFaultInjector =
        promotionFaultInjector;

    public async Task<TicketImportRunResult> RunAsync(
        CliOptions options,
        CancellationToken ct = default)
    {
        DateTimeOffset startedAt = _clock();
        string runId = _runIdFactory();
        CompilationResult compilation = ReportCompiler.Compile(new CompilationRequest(
            options.InputRoot,
            options.ExpectedCount,
            options.OverridesPath));
        ImportAudit audit = CreateAudit(compilation, options, startedAt, runId);
        GuardedImportPaths? paths = null;
        StagingDatabase? staging = null;
        bool promotionBegan = false;
        List<string> messages = [];

        try
        {
            paths = DestinationGuard.Validate(options, compilation, runId);
            audit = CanonicalizeAuditPaths(audit, paths);
            if (options.DryRun)
            {
                string status = compilation.Success
                    ? "dry-run-valid"
                    : "dry-run-failed";
                audit = audit with
                {
                    Run = audit.Run with
                    {
                        Status = status,
                        CompletedAt = _clock(),
                        Failure = compilation.Success
                            ? null
                            : "Compilation produced blocking diagnostics.",
                    },
                };
                await ImportAuditWriter.WriteAuditAsync(paths.AuditPath, audit, ct);
                await ImportAuditWriter.WriteOverrideTemplateAsync(
                    paths.OverrideTemplatePath,
                    compilation.OverrideTemplate,
                    ct);
                return new TicketImportRunResult(
                    compilation,
                    audit,
                    compilation.Success ? 0 : 1,
                    paths.AuditPath,
                    messages);
            }

            CompilationResult confirmed = ReportCompiler.Compile(new CompilationRequest(
                paths.InputRoot,
                options.ExpectedCount,
                paths.OverridesPath));
            if (!string.Equals(
                    compilation.Manifest.ManifestId,
                    confirmed.Manifest.ManifestId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Markdown corpus changed during preflight; no staging database was created.");
            }
            compilation = confirmed;
            audit = CreateAudit(compilation, options, startedAt, runId);
            audit = CanonicalizeAuditPaths(audit, paths);
            await ImportAuditWriter.WriteOverrideTemplateAsync(
                paths.OverrideTemplatePath,
                compilation.OverrideTemplate,
                ct);
            if (!compilation.Success)
            {
                throw new InvalidOperationException(
                    "Compilation produced blocking diagnostics; write mode did not create staging.");
            }

            staging = StagingDatabase.Create(paths.StagingDatabasePath);
            DateTimeOffset importedAt = _clock();
            int persistedTickets = 0;
            int repoRows = 0;
            int jiraRows = 0;
            int zulipRows = 0;
            int githubRows = 0;
            foreach (ManifestTicket ticket in compilation.Manifest.Tickets.OrderBy(
                         ticket => ticket.Key,
                         StringComparer.Ordinal))
            {
                PreparedTicketPayload payload = CloneWithSavedAt(
                    ticket.Payload,
                    importedAt);
                PreparedTicketSaveResult result =
                    await staging.Database.SavePreparedTicketAsync(payload, ct);
                persistedTickets += result.PreparedTicketRows;
                repoRows += result.RepoRows;
                jiraRows += result.RelatedJiraRows;
                zulipRows += result.RelatedZulipRows;
                githubRows += result.RelatedGitHubRows;
            }

            audit = audit with
            {
                Persistence = new ImportPersistenceOutcome(
                    importedAt,
                    compilation.Manifest.Tickets.Count,
                    persistedTickets,
                    repoRows,
                    jiraRows,
                    zulipRows,
                    githubRows,
                    DeepReadbackMatched: false),
            };
            PersistedDatabaseState preparedState =
                await PersistedPreparedTicketReader.ReadAsync(staging.Path, ct);
            IReadOnlyList<ReadinessFailure> preparedFailures =
                ReadinessVerifier.VerifyPreparedProjection(
                    compilation.Manifest,
                    importedAt,
                    preparedState);
            if (preparedFailures.Count > 0)
            {
                audit = audit with
                {
                    Readiness = ToReadinessOutcome(
                        "persistence-failed",
                        preparedFailures,
                        [],
                        [],
                        []),
                };
                throw new InvalidOperationException(
                    $"Deep prepared-record certification failed: {preparedFailures[0].Message}");
            }
            audit = audit with
            {
                Persistence = audit.Persistence! with
                {
                    DeepReadbackMatched = true,
                },
            };

            Dictionary<string, HydrationBatch> expectedHydration =
                new(StringComparer.Ordinal);
            List<HydrationAttemptFailure> hydrationFailures = [];
            Dictionary<string, JiraProcessingSourceTicketRecord> expectedSourceTickets =
                new(StringComparer.Ordinal);
            using HttpClient httpClient = CreateHttpClient(options.Orchestrator);
            PreparedTicketHydrator hydrator = new(
                httpClient,
                staging.Database,
                NullLogger<PreparedTicketHydrator>.Instance);
            JiraProcessingSourceTicketStore sourceStore = new(staging.Path);
            foreach (ManifestTicket ticket in compilation.Manifest.Tickets.OrderBy(
                         ticket => ticket.Key,
                         StringComparer.Ordinal))
            {
                HydrationAttemptResult result =
                    await hydrator.HydrateWithResultAsync(ticket.Key, ct);
                if (result is HydrationAttemptFailure failure)
                {
                    hydrationFailures.Add(failure);
                    continue;
                }

                HydrationBatch batch = ((HydrationAttemptSuccess)result).Batch;
                expectedHydration.Add(ticket.Key, batch);
                JiraProcessingSourceTicketRecord? projected =
                    await PreparedSourceTicketProjector.ProjectAsync(
                        batch,
                        sourceStore,
                        _clock(),
                        ct);
                if (projected is not null)
                {
                    expectedSourceTickets.Add(ticket.Key, projected);
                }
            }

            PersistedDatabaseState finalState =
                await PersistedPreparedTicketReader.ReadAsync(staging.Path, ct);
            ReadinessVerificationResult readiness =
                await ReadinessVerifier.VerifyAsync(
                    compilation.Manifest,
                    importedAt,
                    expectedHydration,
                    hydrationFailures,
                    expectedSourceTickets,
                    finalState,
                    staging.Database,
                    staging.Path,
                    ct);
            bool degradedAccepted =
                options.AcceptUnresolvedHydration
                && readiness.HasWaivableFailures
                && !readiness.HasFatalFailures;
            IReadOnlyList<ImportHydrationIssue> issueRows = readiness.Failures
                .Select(failure => ToAuditIssue(failure, degradedAccepted))
                .ToArray();
            audit = audit with
            {
                Hydration = new ImportHydrationOutcome(
                    compilation.Manifest.Tickets.Count,
                    expectedHydration.Count,
                    hydrationFailures.Count,
                    expectedHydration.Values.Count(batch =>
                        string.Equals(
                            batch.Parent.HydrationStatus,
                            "resolved",
                            StringComparison.Ordinal)),
                    expectedHydration.Values.Count(batch =>
                        batch.JiraRows.Any(row =>
                            string.Equals(row.JiraKey, batch.TicketKey, StringComparison.Ordinal)
                            && string.Equals(
                                row.HydrationStatus,
                                "resolved",
                                StringComparison.Ordinal))),
                    expectedSourceTickets.Count,
                    issueRows),
                Readiness = ToReadinessOutcome(
                    readiness.CanPromote(options.AcceptUnresolvedHydration)
                        ? degradedAccepted
                            ? "degraded-accepted"
                            : "ready"
                        : "failed",
                    readiness.Failures,
                    readiness.FullyReadyKeys,
                    readiness.VisitedWorkGroups,
                    readiness.VisitedPartitions),
            };
            if (!readiness.CanPromote(options.AcceptUnresolvedHydration))
            {
                ReadinessFailure first = readiness.Failures.First();
                throw new InvalidOperationException(
                    $"Readiness certification failed ({first.Code}): {first.Message}");
            }

            string runStatus = degradedAccepted
                ? "degraded-accepted"
                : "ready";
            if (degradedAccepted)
            {
                messages.Add(
                    "WARNING: unresolved hydration was explicitly accepted; the promoted database is degraded and not downstream-ready.");
            }

            string candidateDigest = staging.CloseCheckpointAndHash();
            long candidateLength = new FileInfo(staging.Path).Length;
            audit = audit with
            {
                Database = new ImportDatabaseOutcome(
                    paths.DestinationDatabase,
                    candidateDigest,
                    candidateLength),
                Run = audit.Run with
                {
                    Status = runStatus,
                    CompletedAt = _clock(),
                    Failure = null,
                },
            };
            AtomicDatabasePromoter promoter = new(_promotionFaultInjector);
            try
            {
                DatabasePromotionResult promotion = await promoter.PromoteAsync(
                    paths,
                    audit,
                    candidateDigest,
                    ct);
                audit = promotion.Audit;
                promotionBegan = true;
            }
            catch (DatabasePromotionException ex)
            {
                promotionBegan = ex.PromotionBegan;
                messages.AddRange(ex.RetainedPaths.Select(
                    path => $"Retained recovery artifact: {path}"));
                messages.AddRange(ex.RecoveryErrors.Select(
                    error => $"Recovery error: {error}"));
                throw;
            }

            if (!ImportAuditWriter.TryReadAudit(paths.AuditPath, out ImportAudit? finalAudit)
                || finalAudit?.Database is null
                || !string.Equals(
                    finalAudit.Database.Sha256,
                    ImportFileHash.ComputeSha256(paths.DestinationDatabase),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Final audit is absent, malformed, or does not match the promoted database digest.");
            }

            return new TicketImportRunResult(
                compilation,
                audit,
                0,
                paths.AuditPath,
                messages);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            staging?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            staging?.Dispose();
            messages.Add($"{ex.GetType().Name}: {ex.Message}");
            audit = audit with
            {
                Run = audit.Run with
                {
                    Status = "failed",
                    CompletedAt = _clock(),
                    Failure = $"{ex.GetType().Name}: {ex.Message}",
                },
            };

            string? evidencePath = null;
            if (paths is not null)
            {
                if (!promotionBegan)
                {
                    messages.AddRange(
                        StagingDatabase.DeleteClosedFiles(paths.StagingDatabasePath)
                            .Select(error => $"Cleanup error: {error}"));
                }
                try
                {
                    await ImportAuditWriter.WriteNewAuditAsync(
                        paths.FailedAuditPath,
                        audit,
                        CancellationToken.None);
                    evidencePath = paths.FailedAuditPath;
                }
                catch (Exception evidenceException)
                {
                    messages.Add(
                        $"Could not write failed audit evidence: {evidenceException.GetType().Name}: {evidenceException.Message}");
                }
            }

            return new TicketImportRunResult(
                compilation,
                audit,
                1,
                evidencePath,
                messages);
        }
    }

    private ImportAudit CreateAudit(
        CompilationResult compilation,
        CliOptions options,
        DateTimeOffset startedAt,
        string runId) =>
        ImportAudit.ForCompilation(
            compilation,
            options.DatabasePath,
            options.Orchestrator,
            options.OverridesPath,
            options.AuditPath,
            options.OverrideTemplatePath,
            options.DryRun,
            options.ReplaceExisting,
            options.AcceptUnresolvedHydration,
            startedAt,
            runId);

    private HttpClient CreateHttpClient(Uri orchestrator)
    {
        HttpClient client = _orchestratorHandler is null
            ? new HttpClient()
            : new HttpClient(_orchestratorHandler, disposeHandler: false);
        string absolute = orchestrator.AbsoluteUri.EndsWith(
            "/",
            StringComparison.Ordinal)
            ? orchestrator.AbsoluteUri
            : $"{orchestrator.AbsoluteUri}/";
        client.BaseAddress = new Uri(absolute, UriKind.Absolute);
        return client;
    }

    private static ImportAudit CanonicalizeAuditPaths(
        ImportAudit audit,
        GuardedImportPaths paths) =>
        audit with
        {
            Run = audit.Run with
            {
                InputRoot = paths.InputRoot,
                DestinationDatabase = paths.DestinationDatabase,
                OverridesPath = paths.OverridesPath,
                AuditPath = paths.AuditPath,
                OverrideTemplatePath = paths.OverrideTemplatePath,
            },
        };

    private static PreparedTicketPayload CloneWithSavedAt(
        PreparedTicketPayload source,
        DateTimeOffset savedAt) =>
        new()
        {
            Key = source.Key,
            RequestSummary = source.RequestSummary,
            CommentSummary = source.CommentSummary,
            LinkedTicketSummary = source.LinkedTicketSummary,
            RelatedTicketSummary = source.RelatedTicketSummary,
            RelatedZulipSummary = source.RelatedZulipSummary,
            RelatedGitHubSummary = source.RelatedGitHubSummary,
            ExistingProposed = source.ExistingProposed,
            ProposalA = source.ProposalA,
            ProposalAJustification = source.ProposalAJustification,
            ProposalAImpact = source.ProposalAImpact,
            ProposalB = source.ProposalB,
            ProposalBJustification = source.ProposalBJustification,
            ProposalBImpact = source.ProposalBImpact,
            ProposalC = source.ProposalC,
            ProposalCJustification = source.ProposalCJustification,
            Recommendation = source.Recommendation,
            RecommendationJustification = source.RecommendationJustification,
            SavedAt = savedAt,
            Repos = source.Repos.Select(row => new PreparedTicketRepoPayload
            {
                Repo = row.Repo,
                RepoCategory = row.RepoCategory,
                Justification = row.Justification,
            }).ToList(),
            RelatedJiraTickets = source.RelatedJiraTickets.Select(
                row => new PreparedTicketRelatedJiraPayload
                {
                    AssociatedTicketKey = row.AssociatedTicketKey,
                    LinkType = row.LinkType,
                    Justification = row.Justification,
                }).ToList(),
            RelatedZulipThreads = source.RelatedZulipThreads.Select(
                row => new PreparedTicketRelatedZulipPayload
                {
                    ZulipThreadId = row.ZulipThreadId,
                    Justification = row.Justification,
                }).ToList(),
            RelatedGitHubItems = source.RelatedGitHubItems.Select(
                row => new PreparedTicketRelatedGitHubPayload
                {
                    GitHubItemId = row.GitHubItemId,
                    Justification = row.Justification,
                }).ToList(),
        };

    private static ImportHydrationIssue ToAuditIssue(
        ReadinessFailure failure,
        bool accepted) =>
        new(
            failure.TicketKey ?? string.Empty,
            failure.SourcePath ?? string.Empty,
            failure.Code,
            failure.Message,
            failure.Waivable,
            accepted && failure.Waivable);

    private static ImportReadinessOutcome ToReadinessOutcome(
        string status,
        IReadOnlyList<ReadinessFailure> failures,
        IReadOnlyList<string> readyKeys,
        IReadOnlyList<string> workGroups,
        IReadOnlyList<string> partitions) =>
        new(
            status,
            readyKeys.Count,
            workGroups,
            partitions,
            failures.Select(failure =>
                    ToAuditIssue(
                        failure,
                        status == "degraded-accepted"))
                .ToArray());
}
