using FhirAugury.Tools.TicketMdToDb.Audit;
using FhirAugury.Tools.TicketMdToDb.Compilation;
using FhirAugury.Tools.TicketMdToDb.Import;

namespace FhirAugury.Tools.TicketMdToDb.Tests;

public sealed class DestinationAndPromotionTests
{
    [Fact]
    public void DestinationGuard_RejectsEveryCanonicalPathCollisionAndSourceDescendant()
    {
        using ImportTestDirectory directory = new();
        string source = directory.CopyFixture(
            "CanonicalWithEmbeddedHeading.md",
            "FHIR-100.md");
        CompilationResult compilation = ReportCompiler.Compile(
            new CompilationRequest(directory.CorpusRoot, 1));
        CliOptions baseline = directory.Options();

        Assert.Throws<UnsafeImportTopologyException>(() =>
            DestinationGuard.Validate(
                baseline with
                {
                    DatabasePath = Path.Combine(
                        directory.CorpusRoot,
                        "nested",
                        "..",
                        "unsafe.db"),
                },
                compilation,
                "collision-source"));
        Assert.Throws<UnsafeImportTopologyException>(() =>
            DestinationGuard.Validate(
                baseline with { AuditPath = baseline.DatabasePath },
                compilation,
                "collision-output"));
        Assert.Throws<UnsafeImportTopologyException>(() =>
            DestinationGuard.Validate(
                baseline with { OverrideTemplatePath = source },
                compilation,
                "collision-markdown"));

        string overrides = Path.Combine(directory.OutputRoot, "overrides.json");
        File.WriteAllText(overrides, """{"tickets":{}}""");
        Assert.Throws<UnsafeImportTopologyException>(() =>
            DestinationGuard.Validate(
                baseline with
                {
                    OverridesPath = overrides,
                    OverrideTemplatePath = Path.Combine(
                        directory.OutputRoot,
                        ".",
                        "overrides.json"),
                },
                compilation,
                "collision-overrides"));
    }

    [Fact]
    public void Staging_IsRunUniqueAndExclusive()
    {
        using ImportTestDirectory directory = new();
        string firstPath = Path.Combine(directory.OutputRoot, "prepared.db.run-a.staging");
        string secondPath = Path.Combine(directory.OutputRoot, "prepared.db.run-b.staging");
        using StagingDatabase first = StagingDatabase.Create(firstPath);
        using StagingDatabase second = StagingDatabase.Create(secondPath);

        Assert.NotEqual(first.Path, second.Path);
        Assert.True(File.Exists(first.Path));
        Assert.True(File.Exists(second.Path));
        Assert.Throws<IOException>(() => StagingDatabase.Create(firstPath));
    }

    [Fact]
    public void Staging_RejectsInjectedCrashResidue()
    {
        using ImportTestDirectory directory = new();
        string staging = Path.Combine(directory.OutputRoot, "prepared.db.crash.staging");
        File.WriteAllText($"{staging}-wal", "crash residue");

        IOException exception = Assert.Throws<IOException>(
            () => StagingDatabase.Create(staging));

        Assert.Contains("will not be reused", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(staging));
    }

    [Fact]
    public async Task ConcurrentIdenticalRuns_UseDistinctStagingAndSerializePromotion()
    {
        using ImportTestDirectory directory = new();
        directory.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        FakeOrchestratorHandler handler = new();
        TicketImportRunner first = new(
            () => "concurrent-a",
            () => DateTimeOffset.Parse("2026-09-03T12:00:00Z"),
            handler);
        TicketImportRunner second = new(
            () => "concurrent-b",
            () => DateTimeOffset.Parse("2026-09-03T12:00:00Z"),
            handler);

        TicketImportRunResult[] results = await Task.WhenAll(
            first.RunAsync(directory.Options()),
            second.RunAsync(directory.Options()));

        Assert.Single(results, result => result.ExitCode == 0);
        Assert.Single(results, result => result.ExitCode == 1);
        Assert.Equal(2, results.Select(result => result.Audit.Run.RunId).Distinct().Count());
        Assert.True(File.Exists(directory.DatabasePath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(directory.OutputRoot),
            path => path.EndsWith(".staging", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Promotion_PreservesPriorDryRunOrMismatchedAuditAsUnpairedEvidence()
    {
        using ImportTestDirectory directory = new();
        directory.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        FakeOrchestratorHandler handler = new();
        TicketImportRunResult initial = await directory.RunAsync(
            handler,
            runId: "initial");
        Assert.Equal(0, initial.ExitCode);

        ImportAudit dryRunAudit = initial.Audit with
        {
            Database = null,
            Promotion = null,
            Run = initial.Audit.Run with { Status = "dry-run-valid" },
        };
        await ImportAuditWriter.WriteAuditAsync(directory.AuditPath, dryRunAudit);
        TicketImportRunResult afterDryRun = await directory.RunAsync(
            handler,
            replaceExisting: true,
            runId: "replace-dry");
        Assert.Equal(0, afterDryRun.ExitCode);
        Assert.Equal("dry-run", afterDryRun.Audit.Promotion!.PriorAuditClassification);
        Assert.True(File.Exists(afterDryRun.Audit.Promotion.PriorAuditEvidencePath));
        Assert.Null(afterDryRun.Audit.Promotion.MatchingAuditBackupPath);

        ImportAudit mismatched = afterDryRun.Audit with
        {
            Database = afterDryRun.Audit.Database! with
            {
                Sha256 = new string('0', 64),
            },
        };
        await ImportAuditWriter.WriteAuditAsync(directory.AuditPath, mismatched);
        TicketImportRunResult afterMismatch = await directory.RunAsync(
            handler,
            replaceExisting: true,
            runId: "replace-mismatch");
        Assert.Equal(0, afterMismatch.ExitCode);
        Assert.Equal(
            "digest-mismatch",
            afterMismatch.Audit.Promotion!.PriorAuditClassification);
        Assert.True(File.Exists(afterMismatch.Audit.Promotion.PriorAuditEvidencePath));
        Assert.Null(afterMismatch.Audit.Promotion.MatchingAuditBackupPath);
    }

    [Fact]
    public async Task Replacement_CheckpointsOldWalAndPreservesMatchingAuditBackup()
    {
        using ImportTestDirectory directory = new();
        directory.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        FakeOrchestratorHandler handler = new();
        TicketImportRunResult initial = await directory.RunAsync(
            handler,
            runId: "wal-initial");
        Assert.Equal(0, initial.ExitCode);
        string initialDigest = initial.Audit.Database!.Sha256;
        await File.WriteAllBytesAsync($"{directory.DatabasePath}-wal", []);

        TicketImportRunResult replacement = await directory.RunAsync(
            handler,
            replaceExisting: true,
            runId: "wal-replace");

        Assert.Equal(0, replacement.ExitCode);
        Assert.Equal("matching", replacement.Audit.Promotion!.PriorAuditClassification);
        Assert.True(File.Exists(replacement.Audit.Promotion.DatabaseBackupPath));
        Assert.True(File.Exists(replacement.Audit.Promotion.MatchingAuditBackupPath));
        Assert.Equal(
            initialDigest,
            ComputeSha256(replacement.Audit.Promotion.DatabaseBackupPath!));
        Assert.False(File.Exists($"{directory.DatabasePath}-wal"));
        Assert.False(File.Exists($"{directory.DatabasePath}-shm"));
    }

    [Theory]
    [InlineData(PromotionBoundary.BeforePriorAuditMove)]
    [InlineData(PromotionBoundary.AfterPriorAuditMove)]
    [InlineData(PromotionBoundary.AfterDatabasePromotion)]
    [InlineData(PromotionBoundary.AfterAuditPromotion)]
    public async Task PromotionFaults_RestoreOrLeaveDigestDetectableRecoveryState(
        PromotionBoundary boundary)
    {
        using ImportTestDirectory directory = new();
        directory.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        FakeOrchestratorHandler handler = new();
        TicketImportRunResult initial = await directory.RunAsync(
            handler,
            runId: $"fault-initial-{boundary}");
        Assert.Equal(0, initial.ExitCode);
        string originalDigest = initial.Audit.Database!.Sha256;

        TicketImportRunResult failed = await directory.RunAsync(
            handler,
            replaceExisting: true,
            runId: $"fault-{boundary}",
            fault: observed =>
            {
                if (observed == boundary)
                {
                    throw new IOException($"Injected fault at {boundary}.");
                }
            });

        Assert.Equal(1, failed.ExitCode);
        if (File.Exists(directory.DatabasePath)
            && ImportAuditWriter.TryReadAudit(directory.AuditPath, out ImportAudit? finalAudit)
            && finalAudit?.Database is not null)
        {
            Assert.Equal(
                finalAudit.Database.Sha256,
                ComputeSha256(directory.DatabasePath));
        }
        else
        {
            Assert.True(
                File.Exists(Path.Combine(
                    directory.OutputRoot,
                    $"prepared.db.fault-{boundary}.recovery-candidate"))
                || File.Exists(Path.Combine(
                    directory.OutputRoot,
                    $"prepared.db.fault-{boundary}.staging")));
        }

        if (File.Exists(directory.DatabasePath))
        {
            Assert.Equal(originalDigest, ComputeSha256(directory.DatabasePath));
        }
    }

    [Fact]
    public async Task WriteRun_PrePromotionFailureLeavesSidecarFreeDestinationBytesUnchanged()
    {
        using ImportTestDirectory directory = new();
        directory.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");
        FakeOrchestratorHandler handler = new();
        TicketImportRunResult initial = await directory.RunAsync(
            handler,
            runId: "pre-failure-initial");
        Assert.Equal(0, initial.ExitCode);
        string databaseDigest = ComputeSha256(directory.DatabasePath);
        string auditDigest = ComputeSha256(directory.AuditPath);
        handler.SetJiraMode("FHIR-100", FakeJiraHydrationMode.InvalidType);

        TicketImportRunResult failed = await directory.RunAsync(
            handler,
            replaceExisting: true,
            runId: "pre-failure");

        Assert.Equal(1, failed.ExitCode);
        Assert.Equal(databaseDigest, ComputeSha256(directory.DatabasePath));
        Assert.Equal(auditDigest, ComputeSha256(directory.AuditPath));
        Assert.False(File.Exists($"{directory.DatabasePath}-wal"));
        Assert.False(File.Exists($"{directory.DatabasePath}-shm"));
    }

    [Fact]
    public async Task PromotedAuditDigestMatchesDatabase()
    {
        using ImportTestDirectory directory = new();
        directory.CopyFixture("CanonicalWithEmbeddedHeading.md", "FHIR-100.md");

        TicketImportRunResult result = await directory.RunAsync(
            new FakeOrchestratorHandler());

        Assert.Equal(0, result.ExitCode);
        Assert.True(
            ImportAuditWriter.TryReadAudit(directory.AuditPath, out ImportAudit? audit));
        Assert.Equal(
            ComputeSha256(directory.DatabasePath),
            audit!.Database!.Sha256);
        Assert.True(audit.Promotion!.Completed);
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(stream))
            .ToLowerInvariant();
    }
}
