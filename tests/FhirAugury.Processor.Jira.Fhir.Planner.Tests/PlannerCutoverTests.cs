using System.Globalization;
using System.Security.Cryptography;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Api;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannerCutoverTests : IDisposable
{
    private const string SourceTable = "jira_processing_source_tickets";
    private static readonly DateTimeOffset SourceUpdatedAt =
        new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt =
        new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"planner-cutover-{Guid.NewGuid():N}");

    public PlannerCutoverTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(null)]
    [InlineData("legacy-completion")]
    public async Task CutoverRecapturesLegacyApplierCoordinates(string? completionId)
    {
        LegacySeed seed = await CreateLegacyTargetAsync(missingCompletion: false, completionId: completionId);
        using PlannerDatabase database = NewDatabase(seed.Path);
        await InitializeOwnedAsync(database, seed);
        await ActivateFromAbsentBackupAsync(database, seed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InitializeAndCutover_LegacySourceWithoutCompletionId_PreservesCoordinates(bool hasLastUpdated)
    {
        LegacySeed seed = await CreateLegacyTargetAsync(hasLastUpdated: hasLastUpdated);
        using PlannerDatabase database = NewDatabase(seed.Path);
        await InitializeOwnedAsync(database, seed);
        await ActivateFromAbsentBackupAsync(database, seed);
    }

    [Fact]
    public async Task InitializeAndCutover_DisabledActivationKeepsLegacyMode()
    {
        LegacySeed seed = await CreateLegacyTargetAsync();
        using PlannerDatabase database = NewDatabase(seed.Path);
        await InitializeOwnedAsync(database, seed);
        DatabaseSnapshot before = Capture(database.DatabasePath);
        string backupPath = Path.Combine(_directory, "disabled.backup.db");

        // The same disabled branch as Program.cs, without constructing a host.
        AuthoringProcessorModeRecord mode = await new AuthoringRunStore(database)
            .EnsureProcessorModeAsync("jira-fhir");

        Assert.Equal("legacy", mode.Mode);
        Assert.Equal(0, mode.Epoch);
        Assert.False(mode.RevalidationRequired);
        Assert.Null(mode.RevalidationRunId);
        Assert.False(File.Exists(backupPath));
        AssertSnapshotEqual(before, Capture(database.DatabasePath));
        Assert.Null(NullableScalar(database, "SELECT CompletionId FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
        Assert.Equal(0, Count(database, "authoring_runs"));
        Assert.Equal(0, Count(database, "authoring_result_receipts"));
        await AssertGraphReadableAsync(database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cutover_ExistingDifferentBackupRefusesWithoutReplacement(bool beforeAdditiveRepair)
    {
        LegacySeed seed = await CreateLegacyTargetAsync(interruptedLease: false);
        using PlannerDatabase database = NewDatabase(seed.Path);
        string backupPath = Path.Combine(_directory, "existing.backup.db");
        if (beforeAdditiveRepair)
        {
            CreateBackup(seed.Path, backupPath);
            using SqliteConnection backup = Open(backupPath, SqliteOpenMode.ReadOnly);
            Assert.False(HasCompletionColumn(backup));
        }
        await InitializeOwnedAsync(database, seed);
        if (!beforeAdditiveRepair)
        {
            CreateBackup(seed.Path, backupPath);
            Execute(database, "UPDATE fixture_cutover_sentinel SET Label = @label WHERE Id = 7",
                ("@label", "working content written after the backup"));
        }
        await AssertOperationalLegacyBackupAsync(backupPath);
        byte[] backupBytes = File.ReadAllBytes(backupPath);
        DatabaseSnapshot backupContent = Capture(backupPath);
        // Cutover is not part of the initializer's transaction. A later refusal
        // must retain this POST-initialization state, including the new column.
        DatabaseSnapshot initialized = Capture(seed.Path);
        AuthoringProcessorModeRecord mode = await new AuthoringRunStore(database).GetProcessorModeAsync("jira-fhir");

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ActivateAsync(database, backupPath));

        Assert.Contains("does not match the current committed legacy database", failure.Message, StringComparison.Ordinal);
        Assert.Equal(backupBytes, File.ReadAllBytes(backupPath));
        AssertSnapshotEqual(backupContent, Capture(backupPath));
        AssertSnapshotEqual(initialized, Capture(seed.Path));
        Assert.Equal(mode, await new AuthoringRunStore(database).GetProcessorModeAsync("jira-fhir"));
        Assert.Equal("legacy", mode.Mode);
        Assert.Equal(0, mode.Epoch);
        Assert.Equal(0, Count(database, "authoring_runs"));
        Assert.Equal(0, Count(database, "authoring_result_receipts"));
        using (SqliteConnection connection = Open(seed.Path))
        {
            Assert.True(HasCompletionColumn(connection));
        }
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        await AssertGraphReadableAsync(database);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("non-legacy")]
    [InlineData("corrupt")]
    public async Task Cutover_RetryHonorsModeAndBackupGuards(string backupKind)
    {
        LegacySeed seed = await CreateLegacyTargetAsync();
        using PlannerDatabase database = NewDatabase(seed.Path);
        await InitializeOwnedAsync(database, seed);
        string backupPath = Path.Combine(_directory, "retry.backup.db");
        if (backupKind is "valid" or "non-legacy")
        {
            CreateBackup(seed.Path, backupPath);
            if (backupKind == "non-legacy")
            {
                using SqliteConnection backup = Open(backupPath);
                Execute(backup, "UPDATE authoring_processor_modes SET Mode = 'cutting-over' WHERE ProcessorKind = 'jira-fhir'");
            }
        }
        else if (backupKind == "corrupt")
        {
            File.WriteAllBytes(backupPath, "synthetic invalid SQLite backup"u8.ToArray());
        }
        AuthoringRunStore store = new(database);
        AuthoringProcessorModeRecord cuttingOver = await store.TransitionProcessorModeAsync(
            "jira-fhir", "legacy", "cutting-over");
        Assert.Equal(0, cuttingOver.Epoch);
        DatabaseSnapshot before = Capture(seed.Path);
        byte[]? backupBytes = File.Exists(backupPath) ? File.ReadAllBytes(backupPath) : null;
        DatabaseSnapshot? backupContent = backupKind is "valid" or "non-legacy" ? Capture(backupPath) : null;

        if (backupKind == "valid")
        {
            await AssertOperationalLegacyBackupAsync(backupPath);
            AuthoringProcessorModeRecord active = await ActivateAsync(database, backupPath);
            await AssertActivatedAsync(database, seed, active, before);
            DatabaseSnapshot activated = Capture(seed.Path);
            Assert.Equal(active, await ActivateAsync(database, backupPath));
            AssertSnapshotEqual(activated, Capture(seed.Path));
        }
        else
        {
            if (backupKind == "corrupt")
            {
                SqliteException failure = await Assert.ThrowsAsync<SqliteException>(() => ActivateAsync(database, backupPath));
                Assert.Equal(26, failure.SqliteErrorCode);
            }
            else
            {
                InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    ActivateAsync(database, backupPath));
                Assert.Contains(
                    backupKind == "missing" ? "backup is missing" : "not an operational legacy database",
                    failure.Message, StringComparison.Ordinal);
            }
            Assert.Equal(cuttingOver, await store.GetProcessorModeAsync("jira-fhir"));
            AssertSnapshotEqual(before, Capture(seed.Path));
            Assert.Equal(0, Count(database, "authoring_runs"));
            Assert.Equal(0, Count(database, "authoring_result_receipts"));
        }
        if (backupBytes is null)
        {
            Assert.False(File.Exists(backupPath));
        }
        else
        {
            Assert.Equal(backupBytes, File.ReadAllBytes(backupPath));
        }
        if (backupContent is not null)
        {
            AssertSnapshotEqual(backupContent, Capture(backupPath));
        }
    }

    [Theory]
    [InlineData("valid-different")]
    [InlineData("missing")]
    [InlineData("invalid")]
    public async Task Cutover_RunBackedReopenPreservesReceiptsWithoutReallocatingEpochOrRun(string backupKind)
    {
        LegacySeed seed = await CreateLegacyTargetAsync(hasLastUpdated: false);
        string originalBackup = Path.Combine(_directory, "original.backup.db");
        AuthoringProcessorModeRecord mode;
        AuthoringResultReceipt receipt;
        DatabaseSnapshot retained;
        using (PlannerDatabase first = NewDatabase(seed.Path))
        {
            await InitializeOwnedAsync(first, seed);
            mode = await ActivateAsync(first, originalBackup);
            AuthoringRunStore store = new(first);
            Assert.NotNull(mode.RevalidationRunId);
            AuthoringRunItemRecord item = Assert.Single(await store.GetRunItemsAsync(mode.RevalidationRunId));
            AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
                await store.ClaimItemAsync(item.RunId, item.Id));
            PlannedTicketPayload payload = Plan();
            payload.ResolutionSummary = "Receipt-backed retained summary";
            string hash = PlannedTicketAuthoringDtos.ComputeContentHash(payload);
            AuthoringReceiptAcceptance accepted = await store.AcceptResultAsync(
                new AuthoringResultSubmission(item.RunId, item.Id, claim.OperationId, item.ExpectedSourceRevision, hash),
                claim.OperationToken,
                async (connection, ct) =>
                {
                    await JiraProcessingSourceTicketStore.EnsureCurrentSourceRevisionAsync(
                        connection, item.BusinessKey, item.ItemKind, item.ExpectedSourceRevision, ct);
                    await first.SavePlannedTicketForAuthoringAsync(
                        connection, payload, hash, item.RunId, item.Id, claim.OperationId, ct);
                });
            receipt = accepted.Receipt;
            await store.MarkItemCompleteAsync(item.Id, receipt.ReceiptId);
            // A genuine accepted graph is recognized, not recaptured as legacy.
            Assert.Equal(0, await first.ClassifyLegacyPlannedTicketsAsync());
            Assert.Equal(receipt.ReceiptId,
                NullableScalar(first, "SELECT CompletionId FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
            retained = Capture(seed.Path);
        }

        string backupPath = backupKind == "valid-different"
            ? originalBackup
            : Path.Combine(_directory, $"{backupKind}.backup.db");
        if (backupKind == "invalid")
        {
            File.WriteAllBytes(backupPath, "synthetic immutable invalid backup"u8.ToArray());
        }
        byte[]? backupBytes = File.Exists(backupPath) ? File.ReadAllBytes(backupPath) : null;
        if (backupKind == "valid-different")
        {
            await AssertOperationalLegacyBackupAsync(backupPath);
            Assert.NotEqual(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(seed.Path))),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(backupPath))));
        }

        using PlannerDatabase reopened = NewDatabase(seed.Path);
        reopened.AcquireStartupOwnership();
        using (PlannerDatabase competing = NewDatabase(seed.Path))
        {
            Assert.Throws<InvalidOperationException>(competing.AcquireStartupOwnership);
        }
        reopened.Initialize();
        Assert.Equal(0, await reopened.RecoverInterruptedMaintenanceLeasesAsync());
        AssertSnapshotEqual(retained, Capture(seed.Path));
        Assert.Equal(mode, await ActivateAsync(reopened, backupPath));
        Assert.Equal(mode, await ActivateAsync(reopened, backupPath));
        AssertSnapshotEqual(retained, Capture(seed.Path));
        AuthoringRunStore reopenedStore = new(reopened);
        Assert.Equal(receipt, await reopenedStore.GetReceiptByOperationAsync(receipt.OperationId));
        Assert.Equal(mode.RevalidationRunId, (await reopenedStore.GetFencedRunAsync("jira-fhir"))?.Id);
        Assert.Equal(1, Count(reopened, "authoring_runs"));
        Assert.Equal(1, Count(reopened, "authoring_result_receipts"));
        Assert.Equal(1, mode.Epoch);
        Assert.Equal("receipt-backed", NullableScalar(reopened,
            "SELECT Classification FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
        PlannedTicketDetail detail = Assert.IsType<PlannedTicketDetail>(await reopened.GetPlannedTicketAsync("FHIR-1"));
        Assert.Equal("Receipt-backed retained summary", detail.Ticket.ResolutionSummary);
        Assert.Equal("retained proposal", detail.Ticket.FeatureProposal);
        Assert.Equal("retained rationale", detail.Ticket.DesignRationale);
        Assert.Single(detail.RepoChanges);
        Assert.NotNull(await reopened.GetHydrationAsync("FHIR-1"));
        if (backupBytes is null)
        {
            Assert.False(File.Exists(backupPath));
        }
        else
        {
            Assert.Equal(backupBytes, File.ReadAllBytes(backupPath));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed record LegacySeed(
        string Path,
        bool MissingCompletion,
        string? CompletionId,
        bool HasLastUpdated,
        int InterruptedLeases);

    private sealed record SqliteRows(string[] Columns, object?[][] Values);
    private sealed record DatabaseSnapshot(SqliteRows Schema, IReadOnlyDictionary<string, SqliteRows> Tables);

    private async Task<LegacySeed> CreateLegacyTargetAsync(
        bool missingCompletion = true,
        string? completionId = null,
        bool hasLastUpdated = true,
        bool interruptedLease = true)
    {
        Assert.False(missingCompletion && completionId is not null);
        string donorDirectory = Path.Combine(_directory, $"donor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(donorDirectory);
        string donorPath = Path.Combine(donorDirectory, "current.db");
        using PlannerDatabase donor = NewDatabase(donorPath);
        donor.Initialize();
        await new AuthoringRunStore(donor).EnsureProcessorModeAsync("jira-fhir", now: SourceUpdatedAt);
        JiraProcessingSourceTicketStore source = new(donorPath);
        foreach (string key in new[] { "FHIR-1", "FHIR-2" })
        {
            await source.UpsertAsync(SourceEntry(key, hasLastUpdated), "fhir", false, CancellationToken.None);
        }
        Execute(donor, """
            UPDATE jira_processing_source_tickets
            SET CompletionId = @completion, CompletedProcessingAt = @completed, ProcessingStatus = 'complete'
            WHERE Key = 'FHIR-1'
            """, ("@completion", completionId), ("@completed", CompletedAt.ToString("O")));
        await donor.SavePlannedTicketAsync(Plan());
        await donor.SaveHydrationAsync(new HydrationBatch(
            "FHIR-1",
            new HydrationTicketRow(
                "FHIR-1", "Major", "Persuasive", "resolution prose", "FHIR", "5.0.0", "ballot",
                "clarification", "compatible", "synthetic", 4, "retained description",
                SourceUpdatedAt, "resolved", null, DescriptionHtml: "<p>retained description</p>",
                ResolutionDescriptionHtml: "<p>resolution</p>", Reporter: "Synthetic Reporter"),
            [
                new HydrationJiraRow(
                    "FHIR-1", "FHIR-1", "Title", "Resolved - change required", "Change Request", "Major",
                    "Persuasive", "resolution prose", "FHIR Infrastructure", "FHIR",
                    hasLastUpdated ? SourceUpdatedAt : null, "https://synthetic.invalid/FHIR-1",
                    SourceUpdatedAt, "resolved", null),
                new HydrationJiraRow(
                    "FHIR-1", "FHIR-9", "Linked title", "Open", "Bug", null, null, null,
                    "FHIR Infrastructure", "FHIR", null, "https://synthetic.invalid/FHIR-9",
                    SourceUpdatedAt, "resolved", null),
            ],
            [new HydrationZulipRow(
                "FHIR-1", "implementers:cutover", 11, "implementers", "cutover", 2,
                SourceUpdatedAt, SourceUpdatedAt, "retained message", "https://synthetic.invalid/chat",
                SourceUpdatedAt, "resolved", null)],
            [new HydrationGitHubRow(
                "FHIR-1", "HL7/fhir#9", "HL7", "fhir", 9, null, "Linked issue", "open",
                false, "synthetic", SourceUpdatedAt, "https://synthetic.invalid/issue/9",
                SourceUpdatedAt, "resolved", null)],
            [new HydrationRepoRow(
                "FHIR-1", "HL7/fhir", "core", "FHIR Infrastructure", "FHIR", "FhirCore",
                "https://synthetic.invalid/repo", SourceUpdatedAt, "resolved", null)],
            [new HydrationJiraXrefRow("FHIR-1", "FHIR-9", "Linked")]), CancellationToken.None);
        await donor.UpsertRelatedJiraAsync("FHIR-1", "FHIR-9", "Linked");
        await donor.UpsertRelatedZulipAsync("FHIR-1", "implementers:cutover");
        await donor.UpsertRelatedGitHubAsync("FHIR-1", "HL7/fhir#9");
        await donor.SaveWorkGroupCatalogAsync(
            [new HydrationWorkGroupRow("fhir", "FHIR Infrastructure", "FHIRInfrastructure", SourceUpdatedAt)]);
        await donor.SaveTopicGroupingAsync(new PlannedTicketTopicGroupingPayload
        {
            WorkGroupClean = "FHIRInfrastructure",
            WorkGroupDisplay = "FHIR Infrastructure",
            Specification = "FHIR",
            Type = "Change Request",
            SavedAt = SourceUpdatedAt,
            Topics =
            [
                new PlannedTicketTopicPayload
                {
                    ShortDescription = "Retained topic", LongerDescription = "Retained topic rationale",
                    SpannedRepos = ["HL7/fhir"], RemainingTicketKeys = ["FHIR-1"],
                },
            ],
        });
        Execute(donor, """
            UPDATE planned_ticket_authoring_state
            SET Classification = 'receipt-backed', RunId = 'missing-run', RunItemId = 'missing-item',
                OperationId = 'missing-operation', ReceiptContentHash = 'untrusted',
                LegacyCompletionId = 'stale-coordinate', LegacyCompletedProcessingAt = @stale
            WHERE TicketKey = 'FHIR-1';
            CREATE TABLE fixture_cutover_sentinel(Id INTEGER PRIMARY KEY, Label TEXT NOT NULL, Payload BLOB NOT NULL);
            INSERT INTO fixture_cutover_sentinel VALUES(7, 'retained cutover sentinel', @payload);
            INSERT INTO planner_schema_migrations(Id, AppliedAt) VALUES('synthetic-current-companions', @at);
            """,
            ("@stale", CompletedAt.AddDays(-1).ToString("O")),
            ("@at", SourceUpdatedAt.ToString("O")), ("@payload", new byte[] { 0, 128, 255, 9 }));
        if (interruptedLease)
        {
            Execute(donor, """
                INSERT INTO authoring_mutation_fences(ProcessorKind, RunId, LeaseId, AcquiredAt)
                VALUES('jira-fhir', 'maintenance:previous-synthetic-generation:grouping', 'old-lease', @at)
                """, ("@at", SourceUpdatedAt.ToString("O")));
        }

        string targetPath = Path.Combine(_directory, $"target-{Guid.NewGuid():N}.db");
        using (SqliteConnection target = Open(targetPath))
        {
            SeedIndependentSource(target, missingCompletion, completionId, hasLastUpdated);
            CopyCompanions(donorPath, target);
            Assert.Equal(!missingCompletion, HasCompletionColumn(target));
        }
        return new LegacySeed(targetPath, missingCompletion, completionId, hasLastUpdated, interruptedLease ? 1 : 0);
    }

    private static void SeedIndependentSource(
        SqliteConnection connection,
        bool missingCompletion,
        string? completionId,
        bool hasLastUpdated)
    {
        Execute(connection, """
            CREATE TABLE jira_processing_source_tickets(
                RowId INTEGER UNIQUE PRIMARY KEY NOT NULL,
                Id TEXT UNIQUE NOT NULL,
                Key TEXT NOT NULL,
                Title TEXT NOT NULL,
                Description TEXT,
                Project TEXT NOT NULL,
                Status TEXT NOT NULL,
                WorkGroup TEXT NOT NULL,
                Type TEXT NOT NULL,
                Specification TEXT NOT NULL DEFAULT '',
                SourceTicketShape TEXT NOT NULL,
                LastSyncedAt TEXT NOT NULL,
                LastUpdated TEXT,
                SourceProjectLastSuccessfulRefreshAt TEXT,
                SourceContentRevision INTEGER,
                StartedProcessingAt TEXT,
                CompletedProcessingAt TEXT,
                LastProcessingAttemptAt TEXT,
                ProcessingStatus TEXT,
                ProcessingError TEXT,
                ProcessingAttemptCount INTEGER NOT NULL,
                ErrorMessage TEXT,
                AgentExitCode INTEGER,
                ErrorOccurredAt TEXT);
            CREATE UNIQUE INDEX idx_jira_processing_source_tickets_key_shape
                ON jira_processing_source_tickets(Key COLLATE NOCASE, SourceTicketShape COLLATE NOCASE);
            """);
        foreach (int number in new[] { 1, 2 })
        {
            Execute(connection, """
                INSERT INTO jira_processing_source_tickets(
                    RowId, Id, Key, Title, Description, Project, Status, WorkGroup, Type, Specification,
                    SourceTicketShape, LastSyncedAt, LastUpdated, SourceProjectLastSuccessfulRefreshAt,
                    SourceContentRevision, StartedProcessingAt, CompletedProcessingAt, LastProcessingAttemptAt,
                    ProcessingStatus, ProcessingError, ProcessingAttemptCount, ErrorMessage, AgentExitCode, ErrorOccurredAt)
                VALUES(
                    @rowId, @id, @key, 'Title', @description, 'FHIR', 'Resolved - change required',
                    'FHIR Infrastructure', 'Change Request', 'FHIR', 'fhir', @synced, @updated, @refresh,
                    441, @started, @completed, @started, @status, NULL, @attempts, NULL, NULL, NULL)
                """,
                ("@rowId", 30 + number), ("@id", $"legacy-source-{number}"), ("@key", $"FHIR-{number}"),
                ("@description", $"Retained source description {number}"),
                ("@synced", SourceUpdatedAt.AddHours(2).ToString("O")),
                ("@updated", hasLastUpdated ? SourceUpdatedAt.ToString("O") : null),
                ("@refresh", SourceUpdatedAt.AddHours(1).ToString("O")),
                ("@started", number == 1 ? CompletedAt.AddMinutes(-5).ToString("O") : null),
                ("@completed", number == 1 ? CompletedAt.ToString("O") : null),
                ("@status", number == 1 ? "complete" : null),
                ("@attempts", number == 1 ? 3 : 0));
        }
        if (!missingCompletion)
        {
            Execute(connection, "ALTER TABLE jira_processing_source_tickets ADD COLUMN CompletionId TEXT NULL");
            Execute(connection, "UPDATE jira_processing_source_tickets SET CompletionId = @completion WHERE Key = 'FHIR-1'",
                ("@completion", completionId));
        }
    }

    private static JiraIssueSummaryEntry SourceEntry(string key, bool hasLastUpdated) => new()
    {
        Key = key, ProjectKey = "FHIR", Title = "Title", Type = "Change Request",
        Status = "Resolved - change required", WorkGroup = "FHIR Infrastructure",
        Specification = "FHIR", UpdatedAt = hasLastUpdated ? SourceUpdatedAt : null,
    };

    private static PlannedTicketPayload Plan() => new()
    {
        Key = "FHIR-1", Resolution = "Persuasive", ResolutionSummary = "retained summary",
        FeatureProposal = "retained proposal", DesignRationale = "retained rationale", SavedAt = SourceUpdatedAt,
        Repos = [new() { RepoKey = "HL7/fhir", RepoRevision = "abc123", Justification = "primary" }],
        RepoChanges =
        [
            new()
            {
                TicketRepoId = "retained-repo", RepoKey = "HL7/fhir", ChangeSequence = 2,
                FilePath = "source/observation.html", ChangeTitle = "retained change",
                ChangeDescription = "retained details", SourceLineStart = 5, SourceLineEnd = 7,
                ReplacementLines = ["retained line one", "retained line two"], Reason = "clarification",
            },
        ],
        RepoImpacts =
        [
            new()
            {
                TicketRepoId = "retained-repo", RepoKey = "HL7/fhir", TicketRepoChangeId = "retained-change",
                AffectedFilePath = "source/mappings.html", HowAffected = "retained impact",
            },
        ],
        ChangeValidations = [new() { TicketRepoId = "retained-repo", RepoKey = "HL7/fhir", Action = "retained validation" }],
        TestingConsiderations = [new() { TicketRepoId = "retained-repo", RepoKey = "HL7/fhir", Consideration = "retained test" }],
        OpenQuestions = [new() { TicketRepoId = "retained-repo", RepoKey = "HL7/fhir", Question = "retained question" }],
    };

    private static async Task InitializeOwnedAsync(PlannerDatabase database, LegacySeed seed)
    {
        DatabaseSnapshot before = Capture(seed.Path);
        database.AcquireStartupOwnership();
        using (PlannerDatabase competing = NewDatabase(seed.Path))
        {
            InvalidOperationException rejected = Assert.Throws<InvalidOperationException>(competing.AcquireStartupOwnership);
            Assert.Contains("Another Planner service generation owns", rejected.Message, StringComparison.Ordinal);
        }
        using (SqliteConnection connection = Open(seed.Path))
        {
            Assert.Equal(!seed.MissingCompletion, HasCompletionColumn(connection));
            if (seed.MissingCompletion)
            {
                Assert.Equal(24, ReadRows(connection, "PRAGMA table_info(jira_processing_source_tickets)").Values.Length);
            }
        }
        database.Initialize();
        using (SqliteConnection connection = Open(seed.Path))
        {
            AssertRetainedTables(before.Tables, connection, allowCompletionAddition: seed.MissingCompletion);
            Assert.True(HasCompletionColumn(connection));
        }
        Assert.Equal(seed.InterruptedLeases, await database.RecoverInterruptedMaintenanceLeasesAsync());
        Assert.Equal(0, Count(database, "authoring_mutation_fences"));
        Assert.Equal(seed.CompletionId,
            NullableScalar(database, "SELECT CompletionId FROM jira_processing_source_tickets WHERE Key = 'FHIR-1'"));
        await AssertGraphReadableAsync(database);
    }

    private async Task ActivateFromAbsentBackupAsync(PlannerDatabase database, LegacySeed seed)
    {
        string backupPath = Path.Combine(_directory, "planner.backup.db");
        Assert.False(File.Exists(backupPath));
        DatabaseSnapshot initialized = Capture(seed.Path);

        AuthoringProcessorModeRecord active = await ActivateAsync(database, backupPath);

        Assert.NotEqual(Path.GetFullPath(seed.Path), Path.GetFullPath(backupPath));
        await AssertOperationalLegacyBackupAsync(backupPath);
        AssertSnapshotEqual(initialized, Capture(backupPath));
        byte[] backupBytes = File.ReadAllBytes(backupPath);
        await AssertActivatedAsync(database, seed, active, initialized);
        DatabaseSnapshot activated = Capture(seed.Path);
        Assert.Equal(active, await ActivateAsync(database, backupPath));
        AssertSnapshotEqual(activated, Capture(seed.Path));
        Assert.Equal(backupBytes, File.ReadAllBytes(backupPath));
    }

    private static async Task AssertActivatedAsync(
        PlannerDatabase database,
        LegacySeed seed,
        AuthoringProcessorModeRecord active,
        DatabaseSnapshot before)
    {
        Assert.Equal("run-backed", active.Mode);
        Assert.Equal(1, active.Epoch);
        Assert.True(active.RevalidationRequired);
        Assert.NotNull(active.RevalidationRunId);
        AuthoringRunStore store = new(database);
        Assert.Equal(active, await store.GetProcessorModeAsync("jira-fhir"));
        AuthoringRunRecord run = Assert.IsType<AuthoringRunRecord>(await store.GetRunAsync(active.RevalidationRunId));
        Assert.Equal(AuthoringRunPurposeValues.InitialRevalidation, run.Purpose);
        Assert.Equal("running", run.Status);
        Assert.Equal(active.Epoch, run.AuthoringEpoch);
        Assert.False(run.DatabaseOnly);
        Assert.Equal(1, run.TotalItems);
        Assert.Equal(run, await store.GetFencedRunAsync("jira-fhir"));
        AuthoringRunItemRecord item = Assert.Single(await store.GetRunItemsAsync(run.Id));
        Assert.Equal("FHIR-1", item.BusinessKey); // FHIR-2 has no retained plan.
        Assert.Equal("fhir", item.ItemKind);
        Assert.Equal("pending", item.Status);
        Assert.Equal(0, item.AttemptCount);
        Assert.Null(item.AcceptedReceiptId);
        Assert.Null(item.CurrentOperationId);
        JiraProcessingSourceTicketStore source = new(seed.Path);
        JiraProcessingSourceTicketRecord ticket = Assert.IsType<JiraProcessingSourceTicketRecord>(
            await source.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None));
        Assert.Equal(JiraProcessingSourceTicketStore.GetSourceRevision(ticket), item.ExpectedSourceRevision);
        if (!seed.HasLastUpdated)
        {
            Assert.Null(ticket.LastUpdated);
            Assert.Equal(AuthoringResultHasher.HashNormalizedUtf8(
                string.Join("\n", "FHIR-1", "Title", "Resolved - change required",
                    "FHIR Infrastructure", "Change Request", "FHIR")), item.ExpectedSourceRevision);
            Assert.NotEqual(AuthoringResultHasher.HashNormalizedUtf8(
                string.Join("\n", "fhir-1", "Title", "Resolved - change required",
                    "FHIR Infrastructure", "Change Request", "FHIR")), item.ExpectedSourceRevision);
        }
        Assert.Equal(seed.CompletionId, ticket.CompletionId);
        Assert.Equal(CompletedAt, ticket.CompletedProcessingAt);
        Assert.Equal("complete", ticket.ProcessingStatus);
        Assert.Equal("legacy-unverified", NullableScalar(database,
            "SELECT Classification FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
        Assert.Equal(seed.CompletionId, NullableScalar(database,
            "SELECT LegacyCompletionId FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
        Assert.Equal(CompletedAt, DateTimeOffset.Parse(
            Assert.IsType<string>(NullableScalar(database,
                "SELECT LegacyCompletedProcessingAt FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-1'")),
            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        foreach (string column in new[] { "RunId", "RunItemId", "OperationId", "ReceiptContentHash" })
        {
            Assert.Null(NullableScalar(database,
                $"SELECT {column} FROM planned_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
        }
        using (SqliteConnection connection = Open(seed.Path))
        {
            Dictionary<string, SqliteRows> protectedTables = before.Tables
                .Where(table => !table.Key.StartsWith("authoring_", StringComparison.Ordinal) &&
                    table.Key != "planned_ticket_authoring_state")
                .ToDictionary(table => table.Key, table => table.Value, StringComparer.Ordinal);
            AssertRetainedTables(protectedTables, connection);
            AssertRowsEqual(before.Tables["authoring_result_receipts"], ReadTable(connection, "authoring_result_receipts"));
        }
        Assert.Equal(1, Count(database, "authoring_runs"));
        Assert.Equal(0, Count(database, "authoring_result_receipts"));
        Assert.True(await database.HasActiveAuthoringFenceAsync());
        await Assert.ThrowsAsync<AuthoringConflictException>(() => database.SavePlannedTicketAsync(
            new PlannedTicketPayload { Key = "FHIR-2", ResolutionSummary = "blocked" }));
        await AssertGraphReadableAsync(database);
    }

    private static async Task AssertGraphReadableAsync(PlannerDatabase database)
    {
        PlannedTicketDetail detail = Assert.IsType<PlannedTicketDetail>(await database.GetPlannedTicketAsync("FHIR-1"));
        Assert.Equal("retained summary", detail.Ticket.ResolutionSummary);
        Assert.Equal("retained proposal", detail.Ticket.FeatureProposal);
        Assert.Equal("retained rationale", detail.Ticket.DesignRationale);
        Assert.Equal("abc123", Assert.Single(detail.Repos).RepoRevision);
        Assert.Equal(["retained line one", "retained line two"], Assert.Single(detail.RepoChanges).ReplacementLines);
        Assert.Equal("retained impact", Assert.Single(detail.RepoImpacts).HowAffected);
        Assert.Equal("retained validation", Assert.Single(detail.ChangeValidations).Action);
        Assert.Equal("retained test", Assert.Single(detail.TestingConsiderations).Consideration);
        Assert.Equal("retained question", Assert.Single(detail.OpenQuestions).Question);
        PlannedTicketHydrationReadModel hydration = Assert.IsType<PlannedTicketHydrationReadModel>(
            await database.GetHydrationAsync("FHIR-1"));
        Assert.Equal("<p>retained description</p>", hydration.Parent?.DescriptionHtml);
        Assert.Equal(2, hydration.JiraRows.Count);
        Assert.Equal("retained message", Assert.Single(hydration.ZulipRows).FirstMessageExcerpt);
        Assert.False(Assert.Single(hydration.GitHubRows).IsPullRequest);
        Assert.Equal("HL7/fhir", Assert.Single(hydration.RepoRows).RepoKey);
        Assert.Equal("FHIR-9", Assert.Single(hydration.JiraXrefRows).JiraKey);
        PlannedTicketTopicsForCategory topics = Assert.IsType<PlannedTicketTopicsForCategory>(
            await database.GetWorkGroupTopicsAsync("FHIRInfrastructure", "FHIR", "Change Request"));
        Assert.Equal(["FHIR-1"], Assert.Single(topics.Topics).RemainingTicketKeys);
        PlannedTicketClusteringSignals signals = Assert.IsType<PlannedTicketClusteringSignals>(
            await database.GetClusteringSignalsAsync("FHIRInfrastructure"));
        Assert.Equal("retained summary", Assert.Single(signals.Tickets).ResolutionSummary);
    }

    private static Task<AuthoringProcessorModeRecord> ActivateAsync(PlannerDatabase database, string backupPath) =>
        new AuthoringCutoverCoordinator(database.OpenConnection).ActivateAsync(
            new AuthoringCutoverRequest("jira-fhir", database.DatabasePath, backupPath), database);

    private static async Task AssertOperationalLegacyBackupAsync(string path)
    {
        using (SqliteConnection connection = Open(path, SqliteOpenMode.ReadOnly))
        {
            Assert.Equal("ok", Assert.Single(ReadRows(connection, "PRAGMA integrity_check").Values)[0]);
            Assert.Single(ReadRows(connection,
                "SELECT ProcessorKind FROM authoring_processor_modes WHERE ProcessorKind = 'jira-fhir'").Values);
        }
        AuthoringRunStore store = new(() => Open(path, SqliteOpenMode.ReadOnly));
        AuthoringProcessorModeRecord mode = await store.GetProcessorModeAsync("jira-fhir");
        Assert.Equal("legacy", mode.Mode);
        Assert.Equal(0, mode.Epoch);
        Assert.False(mode.RevalidationRequired);
        Assert.Null(mode.RevalidationRunId);
    }

    private static void CreateBackup(string sourcePath, string backupPath)
    {
        Assert.False(File.Exists(backupPath));
        using SqliteConnection source = Open(sourcePath);
        using SqliteConnection target = Open(backupPath);
        source.BackupDatabase(target);
    }

    private static void CopyCompanions(string donorPath, SqliteConnection target)
    {
        using SqliteConnection donor = Open(donorPath, SqliteOpenMode.ReadOnly);
        SqliteRows schema = ReadRows(donor,
            "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY type, name");
        string[] tables = schema.Values
            .Where(row => Equals(row[0], "table"))
            .Select(row => Assert.IsType<string>(row[1]))
            .Where(name => name.StartsWith("planned_", StringComparison.Ordinal) ||
                name.StartsWith("authoring_", StringComparison.Ordinal) ||
                name is "jira_review_workgroups" or "planner_schema_migrations" or "fixture_cutover_sentinel")
            .ToArray();
        Assert.DoesNotContain(SourceTable, tables);
        foreach (string table in tables)
        {
            object?[] definition = Assert.Single(schema.Values,
                row => Equals(row[0], "table") && Equals(row[1], table));
            Execute(target, Assert.IsType<string>(definition[3]));
            SqliteRows rows = ReadTable(donor, table);
            foreach (object?[] values in rows.Values)
            {
                string[] parameters = Enumerable.Range(0, rows.Columns.Length).Select(index => $"@p{index}").ToArray();
                Execute(target,
                    $"INSERT INTO {Quote(table)} ({string.Join(", ", rows.Columns.Select(Quote))}) " +
                    $"VALUES ({string.Join(", ", parameters)})",
                    parameters.Select((name, index) => (name, values[index])).ToArray());
            }
        }
        // No donor source DDL or source indices, and no receipt trigger firing
        // during seed. Only current companion constraints are attached here.
        foreach (object?[] row in schema.Values.Where(row =>
                     (Equals(row[0], "index") || Equals(row[0], "trigger")) &&
                     tables.Contains(Assert.IsType<string>(row[2]), StringComparer.Ordinal)))
        {
            Execute(target, Assert.IsType<string>(row[3]));
        }
    }

    private static PlannerDatabase NewDatabase(string path) => new(path, NullLogger<PlannerDatabase>.Instance);

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string Quote(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    private static bool HasCompletionColumn(SqliteConnection connection) =>
        ReadRows(connection, "PRAGMA table_info(jira_processing_source_tickets)")
            .Values.Any(row => Equals(row[1], "CompletionId"));

    private static SqliteRows ReadRows(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        string[] columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        List<object?[]> rows = [];
        while (reader.Read())
        {
            rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
        }
        return new SqliteRows(columns, rows.ToArray());
    }

    private static SqliteRows ReadTable(SqliteConnection connection, string table, string[]? columns = null)
    {
        string projection = columns is null ? "*" : string.Join(", ", columns.Select(Quote));
        SqliteRows shape = ReadRows(connection, $"SELECT {projection} FROM {Quote(table)} LIMIT 0");
        return ReadRows(connection,
            $"SELECT {projection} FROM {Quote(table)} ORDER BY {string.Join(", ", shape.Columns.Select(Quote))}");
    }

    private static DatabaseSnapshot Capture(string path)
    {
        using SqliteConnection connection = Open(path, SqliteOpenMode.ReadOnly);
        SqliteRows schema = ReadRows(connection,
            "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name");
        Dictionary<string, SqliteRows> tables = schema.Values
            .Where(row => Equals(row[0], "table"))
            .ToDictionary(row => Assert.IsType<string>(row[1]),
                row => ReadTable(connection, Assert.IsType<string>(row[1])), StringComparer.Ordinal);
        return new DatabaseSnapshot(schema, tables);
    }

    private static void AssertRowsEqual(SqliteRows expected, SqliteRows actual)
    {
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.Values.Length, actual.Values.Length);
        for (int row = 0; row < expected.Values.Length; row++)
        {
            for (int column = 0; column < expected.Columns.Length; column++)
            {
                object? value = expected.Values[row][column];
                object? retained = actual.Values[row][column];
                Assert.Equal(value?.GetType(), retained?.GetType());
                if (value is byte[] bytes)
                {
                    Assert.Equal(bytes, Assert.IsType<byte[]>(retained));
                }
                else
                {
                    Assert.True(Equals(value, retained), $"Retained {expected.Columns[column]} changed at row {row}.");
                }
            }
        }
    }

    private static void AssertRetainedTables(
        IReadOnlyDictionary<string, SqliteRows> expected,
        SqliteConnection connection,
        bool allowCompletionAddition = false)
    {
        foreach ((string table, SqliteRows rows) in expected)
        {
            if (allowCompletionAddition && table == SourceTable)
            {
                Assert.Equal(rows.Columns.Append("CompletionId"), ReadTable(connection, table).Columns);
                AssertRowsEqual(rows, ReadTable(connection, table, rows.Columns));
                Assert.All(ReadRows(connection, "SELECT CompletionId FROM jira_processing_source_tickets").Values,
                    row => Assert.Null(row[0]));
            }
            else
            {
                AssertRowsEqual(rows, ReadTable(connection, table));
            }
        }
    }

    private static void AssertSnapshotEqual(DatabaseSnapshot expected, DatabaseSnapshot actual)
    {
        AssertRowsEqual(expected.Schema, actual.Schema);
        Assert.Equal(expected.Tables.Keys, actual.Tables.Keys);
        foreach ((string table, SqliteRows rows) in expected.Tables)
        {
            AssertRowsEqual(rows, actual.Tables[table]);
        }
    }

    private static void Execute(
        PlannerDatabase database,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteConnection connection = database.OpenConnection();
        Execute(connection, sql, parameters);
    }

    private static void Execute(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }

    private static string? NullableScalar(PlannerDatabase database, string sql)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Assert.IsType<string>(value);
    }

    private static long Count(PlannerDatabase database, string table)
    {
        using SqliteConnection connection = database.OpenConnection();
        return Assert.IsType<long>(Assert.Single(ReadRows(connection, $"SELECT COUNT(*) FROM {Quote(table)}").Values)[0]);
    }
}
