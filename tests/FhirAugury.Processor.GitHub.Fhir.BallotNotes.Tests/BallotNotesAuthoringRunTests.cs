using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Authoring;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Contracts;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Controllers;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Database.Records;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Persistence.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

public sealed class BallotNotesAuthoringRunTests
{
    [Theory]
    [InlineData("Artifact", "artifact-worker")]
    [InlineData("Page", "page-worker")]
    [InlineData("DataType", "datatype-worker")]
    public async Task HandlerDispatchesByNoteTypeWithEnvironmentOnlyToken(
        string type,
        string expectedCommand)
    {
        using Fixture fixture = new();
        fixture.Options.ArtifactAuthoringCommand =
            "artifact-worker {noteId}";
        fixture.Options.PageAuthoringCommand = "page-worker {noteId}";
        fixture.Options.DataTypeAuthoringCommand =
            "datatype-worker {noteId}";
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution("note-a", type);
        (AuthoringRunRecord run, _, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync(execution.Id);
        AuthoringRunItemRecord item =
            (await fixture.AuthoringStore.GetRunItemsAsync(run.Id)).Single();
        NotesHydrationRunItemRecord hydration =
            fixture.Database.GetHydrationExecutionItems(execution.Id).Single();
        FakeCommandRunner runner = new();
        BallotNotesAuthoringHandler handler = new(
            fixture.AuthoringStore,
            runner,
            Options.Create(fixture.Options));

        AuthoringWorkResult result = await handler.ProcessAsync(
            new BallotNotesAuthoringWorkItem(item, hydration),
            new AuthoringQueueClaim(
                claim.OperationId,
                claim.OperationToken,
                claim.AttemptNumber),
            CancellationToken.None);

        Assert.Equal(AuthoringWorkDisposition.RetryableError, result.Disposition);
        Assert.Equal(expectedCommand, runner.LastCommand!.FileName);
        Assert.DoesNotContain(
            claim.OperationToken,
            string.Join(" ", runner.LastCommand.Arguments));
        Assert.Equal(
            claim.OperationToken,
            runner.LastCommand.Environment[
                "FHIR_AUGURY_AUTHORING_OPERATION_TOKEN"]);
        Assert.Equal(
            execution.Id,
            runner.LastCommand.Environment[
                "FHIR_AUGURY_BALLOT_NOTES_EXECUTION_ID"]);
    }

    [Fact]
    public async Task RunCopiesOneCompletedExecutionAndSupportsSingleNoteResolution()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution("note-a", "Artifact");

        BallotNotesAuthoringRunCreation creation =
            await fixture.Coordinator.CreateRunAsync(
                new BallotNotesAuthoringRunRequest(
                    NoteIds: ["note-a"],
                    DatabaseOnly: true));

        AuthoringRunItemRecord item = Assert.Single(creation.Items);
        Assert.Equal(execution.Id, creation.Execution.Id);
        Assert.Equal("note-a", item.BusinessKey);
        Assert.Equal(
            fixture.Database.GetHydrationExecutionItems(execution.Id)
                .Single().EvidenceRevision,
            item.ExpectedSourceRevision);
    }

    [Fact]
    public async Task CreateEndpointExactlyReplaysAndRejectsPartialOverlap()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution(
                ("note-a", "Artifact"),
                ("note-b", "Page"));
        BallotNotesAuthoringRunsController controller =
            fixture.CreateController();
        BallotNotesAuthoringRunRequest request = new(
            execution.Id,
            ["note-a", "note-b"],
            DatabaseOnly: true);

        AcceptedResult first = Assert.IsType<AcceptedResult>(
            await controller.CreateRun(
                request,
                CancellationToken.None));
        AcceptedResult replay = Assert.IsType<AcceptedResult>(
            await controller.CreateRun(
                new BallotNotesAuthoringRunRequest(
                    execution.Id,
                    ["note-b", "note-a"],
                    DatabaseOnly: true),
                CancellationToken.None));
        BallotNotesAuthoringRunResponse firstBody =
            Assert.IsType<BallotNotesAuthoringRunResponse>(first.Value);
        BallotNotesAuthoringRunResponse replayBody =
            Assert.IsType<BallotNotesAuthoringRunResponse>(replay.Value);

        Assert.Equal(firstBody.Run, replayBody.Run);
        Assert.Equal(firstBody.Items, replayBody.Items);
        Assert.Equal(
            1,
            fixture.Scalar<int>("SELECT COUNT(*) FROM authoring_runs"));

        ConflictObjectResult partial =
            Assert.IsType<ConflictObjectResult>(
                await controller.CreateRun(
                    new BallotNotesAuthoringRunRequest(
                        execution.Id,
                        ["note-a"],
                        DatabaseOnly: true),
                    CancellationToken.None));
        Assert.Contains(
            nameof(AuthoringConflictCode.RevisionAlreadyScheduled),
            partial.Value!.ToString());
    }

    [Fact]
    public async Task ReadySnapshotBytesAreReturnedBesideDescriptorEndpoint()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution("note-a", "Artifact");
        BallotNotesAuthoringRunCreation creation =
            await fixture.Coordinator.CreateRunAsync(
                new BallotNotesAuthoringRunRequest(
                    execution.Id,
                    DatabaseOnly: false));
        byte[] expected = [9, 8, 7, 6];
        string snapshotPath = Path.Combine(
            Path.GetDirectoryName(fixture.DatabasePath)!,
            "ready-snapshot.db");
        await File.WriteAllBytesAsync(snapshotPath, expected);
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO authoring_review_snapshots(
                    Id, ProcessorKind, RunId, AuthoringEpoch, Sequence,
                    SchemaVersion, Status, TempPath, Path, ChecksumSha256,
                    SizeBytes, ItemCount, ReceiptCount, TableCountsJson,
                    CreatedAt)
                VALUES(
                    'snapshot-ready', 'ballot-notes', @runId, 1, 1,
                    1, 'ready', '', @path, 'hash',
                    @size, 1, 1, '{}', @createdAt);
                UPDATE authoring_runs
                SET SnapshotId = 'snapshot-ready'
                WHERE Id = @runId
                """;
            command.Parameters.AddWithValue("@runId", creation.Run.Id);
            command.Parameters.AddWithValue("@path", snapshotPath);
            command.Parameters.AddWithValue("@size", expected.Length);
            command.Parameters.AddWithValue(
                "@createdAt",
                DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        PhysicalFileResult result = Assert.IsType<PhysicalFileResult>(
            await fixture.CreateController().GetSnapshotBytes(
                creation.Run.Id,
                CancellationToken.None));

        Assert.Equal(
            "application/vnd.sqlite3",
            result.ContentType);
        Assert.Equal(expected, await File.ReadAllBytesAsync(result.FileName));
    }

    [Fact]
    public async Task CoordinatorRejectsDatabaseOnlyMismatchAsScheduledRevision()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution("note-a", "Artifact");
        await fixture.Coordinator.CreateRunAsync(
            new BallotNotesAuthoringRunRequest(
                execution.Id,
                DatabaseOnly: true));

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => fixture.Coordinator.CreateRunAsync(
                    new BallotNotesAuthoringRunRequest(
                        execution.Id,
                        DatabaseOnly: false)));

        Assert.Equal(
            AuthoringConflictCode.RevisionAlreadyScheduled,
            conflict.Code);
    }

    [Fact]
    public async Task SingleNoteResolutionRejectsNewerRunningExecutionBeforeMembership()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        fixture.CreateCompletedExecution("note-a", "Artifact");
        (NotesHydrationExecutionRecord _, HydrationMutationLease lease) =
            fixture.CreateRunningExecution();

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Coordinator.CreateRunAsync(
                    new BallotNotesAuthoringRunRequest(
                        NoteIds: ["note-a"],
                        DatabaseOnly: true)));

        Assert.Contains("newer hydration", error.Message,
            StringComparison.OrdinalIgnoreCase);
        fixture.Database.ReleaseMutationLease(lease);
    }

    [Fact]
    public async Task ResultAcceptanceIsAtomicReplaysAndRequiresToken()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution("note-a", "Artifact");
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync(execution.Id);
        NoteRecord evidenceBefore =
            fixture.Database.GetNote("note-a")!.Note;
        BallotNoteProse prose = SampleProse("draft") with
        {
            SourceFilesNote = "prose-only source note",
        };
        BallotNoteAuthoringResultRequest request =
            fixture.CreateResultRequest(run, item, claim, prose);
        BallotNotesAuthoringRunsController controller =
            fixture.CreateController();

        UnauthorizedObjectResult missingToken =
            Assert.IsType<UnauthorizedObjectResult>(
                await controller.SubmitResult(
                    run.Id,
                    item.Id,
                    item.ItemKind,
                    item.BusinessKey,
                    null,
                    request,
                    CancellationToken.None));
        Assert.Contains("operation-token-required", missingToken.Value!.ToString());

        OkObjectResult first = Assert.IsType<OkObjectResult>(
            await controller.SubmitResult(
                run.Id,
                item.Id,
                item.ItemKind,
                item.BusinessKey,
                claim.OperationToken,
                request,
                CancellationToken.None));
        OkObjectResult replay = Assert.IsType<OkObjectResult>(
            await controller.SubmitResult(
                run.Id,
                item.Id,
                item.ItemKind,
                item.BusinessKey,
                claim.OperationToken,
                request,
                CancellationToken.None));
        AuthoringReceiptAcceptance accepted =
            Assert.IsType<AuthoringReceiptAcceptance>(first.Value);
        AuthoringReceiptAcceptance replayed =
            Assert.IsType<AuthoringReceiptAcceptance>(replay.Value);
        BallotNoteProse changedProse = SampleProse("changed");
        ConflictObjectResult changedReplay =
            Assert.IsType<ConflictObjectResult>(
                await controller.SubmitResult(
                    run.Id,
                    item.Id,
                    item.ItemKind,
                    item.BusinessKey,
                    claim.OperationToken,
                    fixture.CreateResultRequest(
                        run,
                        item,
                        claim,
                        changedProse),
                    CancellationToken.None));

        Assert.False(accepted.IsReplay);
        Assert.True(replayed.IsReplay);
        Assert.Equal(accepted.Receipt, replayed.Receipt);
        Assert.Contains(
            nameof(AuthoringConflictCode.ContentChanged),
            changedReplay.Value!.ToString());
        Assert.Equal(1, fixture.Scalar<int>(
            "SELECT COUNT(*) FROM authoring_result_receipts"));
        Assert.Equal("receipt-backed", fixture.Scalar<string>(
            "SELECT ProseVerificationStatus FROM notes WHERE NoteId = 'note-a'"));
        NoteRecord evidenceAfter =
            fixture.Database.GetNote("note-a")!.Note;
        Assert.Equal(
            evidenceBefore.CurrentEvidenceHash,
            evidenceAfter.CurrentEvidenceHash);
        Assert.Equal(
            evidenceBefore.CurrentEvidenceRevision,
            evidenceAfter.CurrentEvidenceRevision);
        Assert.Equal("stale", fixture.Database.GetNote("note-a")!.Status);
        await fixture.AuthoringStore.MarkItemCompleteAsync(
            item.Id,
            accepted.Receipt.ReceiptId);
        Assert.Equal("authored", fixture.Database.GetNote("note-a")!.Status);
        Assert.NotEqual(
            claim.OperationToken,
            fixture.Scalar<string>(
                "SELECT TokenVerifier FROM authoring_run_attempts WHERE OperationId = '" +
                claim.OperationId + "'"));
        Assert.DoesNotContain(
            claim.OperationToken,
            File.ReadAllText(fixture.DatabasePath),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostReceiptRetryUsesLeaseWithoutRedispatchingAuthor()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution("note-a", "Artifact");
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync(execution.Id);
        AuthoringReceiptAcceptance receipt =
            await fixture.AcceptAsync(run, item, claim, SampleProse("draft"));

        BallotNotesAuthoringWorkItemStore store =
            new(fixture.Database, fixture.AuthoringStore);
        BallotNotesAuthoringWorkItem pending =
            Assert.Single(await store.GetPendingAsync(
                run.Id,
                1,
                CancellationToken.None));
        AuthoringQueueClaim firstLease = (await store.TryClaimAsync(
            pending,
            DateTimeOffset.UtcNow,
            CancellationToken.None))!;
        await store.ApplyResultAsync(
            pending,
            firstLease,
            AuthoringWorkResult.Retry("post-receipt failure"),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        AuthoringRetryResult retry =
            await fixture.ControlService.RetryItemAsync(
                fixture.Coordinator.ProcessorKind,
                run.Id,
                item.Id);
        Assert.False(retry.RequiresAuthoring);

        pending = Assert.Single(
            await store.GetPendingAsync(
                run.Id,
                1,
                CancellationToken.None));
        AuthoringQueueClaim secondLease = (await store.TryClaimAsync(
            pending,
            DateTimeOffset.UtcNow,
            CancellationToken.None))!;
        FakeCommandRunner runner = new();
        BallotNotesAuthoringHandler handler = new(
            fixture.AuthoringStore,
            runner,
            Options.Create(fixture.Options));
        AuthoringWorkResult result =
            await handler.ProcessAsync(
                pending,
                secondLease,
                CancellationToken.None);
        await store.ApplyResultAsync(
            pending,
            secondLease,
            result,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Equal(0, runner.CallCount);
        Assert.Equal(AuthoringWorkDisposition.Complete, result.Disposition);
        Assert.Equal(receipt.Receipt.ReceiptId, result.ReceiptId);
        Assert.Equal(
            AuthoringStatusValues.Items.Complete,
            (await fixture.AuthoringStore.GetRunItemsAsync(run.Id))
                .Single().Status);
    }

    [Fact]
    public async Task UnpersistedRetryRotatesOperationAndRejectsStaleCallback()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution("note-a", "Artifact");
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim first) =
            await fixture.CreateClaimAsync(execution.Id);
        await fixture.AuthoringStore.MarkClaimErrorAsync(
            item.Id,
            first.OperationId,
            "worker failed");
        AuthoringRetryResult retry =
            await fixture.ControlService.RetryItemAsync(
                fixture.Coordinator.ProcessorKind,
                run.Id,
                item.Id);
        AuthoringOperationClaim second =
            (await fixture.AuthoringStore.ClaimItemAsync(run.Id, item.Id))!;

        Assert.True(retry.RequiresAuthoring);
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.NotEqual(first.OperationToken, second.OperationToken);
        ConflictObjectResult stale = Assert.IsType<ConflictObjectResult>(
            await fixture.CreateController().SubmitResult(
                run.Id,
                item.Id,
                item.ItemKind,
                item.BusinessKey,
                first.OperationToken,
                fixture.CreateResultRequest(
                    run,
                    item,
                    first,
                    SampleProse("stale")),
                CancellationToken.None));
        Assert.Contains(
            nameof(AuthoringConflictCode.StaleOperation),
            stale.Value!.ToString());
    }

    [Fact]
    public async Task OrphanRecovery_ReevaluatesPreReceiptClaimUntilDue()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution("note-a", "Artifact");
        BallotNotesAuthoringRunCreation creation =
            await fixture.Coordinator.CreateRunAsync(
                new BallotNotesAuthoringRunRequest(
                    execution.Id,
                    DatabaseOnly: true));
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        AuthoringRunItemRecord item = Assert.Single(creation.Items);
        DateTimeOffset startedAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        _ = Assert.IsType<AuthoringOperationClaim>(
            await fixture.AuthoringStore.ClaimItemAsync(
                creation.Run.Id,
                item.Id,
                startedAt));
        BallotNotesAuthoringWorkItemStore store =
            new(fixture.Database, fixture.AuthoringStore);

        AuthoringOrphanRecoveryResult early =
            await store.ResetOrphanedItemsAsync(
                creation.Run.Id,
                TimeSpan.FromMinutes(10),
                startedAt.AddMinutes(5),
                CancellationToken.None);

        Assert.Equal(0, early.RecoveredItems);
        Assert.Equal(startedAt.AddMinutes(10), early.NextRecoveryAt);
        Assert.Equal(
            AuthoringStatusValues.Items.InProgress,
            Assert.Single(
                await fixture.AuthoringStore.GetRunItemsAsync(
                    creation.Run.Id)).Status);

        AuthoringOrphanRecoveryResult due =
            await store.ResetOrphanedItemsAsync(
                creation.Run.Id,
                TimeSpan.FromMinutes(10),
                startedAt.AddMinutes(10),
                CancellationToken.None);
        AuthoringRunItemRecord recovered = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(creation.Run.Id));

        Assert.Equal(1, due.RecoveredItems);
        Assert.Null(due.NextRecoveryAt);
        Assert.Equal(AuthoringStatusValues.Items.Error, recovered.Status);
        Assert.Equal(startedAt.AddMinutes(10), recovered.CompletedAt);
        Assert.Equal(1, recovered.AttemptCount);
    }

    [Fact]
    public async Task OrphanRecovery_UsesPostReceiptLeaseAcquiredAt()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution("note-a", "Artifact");
        BallotNotesAuthoringRunCreation creation =
            await fixture.Coordinator.CreateRunAsync(
                new BallotNotesAuthoringRunRequest(
                    execution.Id,
                    DatabaseOnly: true));
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        AuthoringRunItemRecord item = Assert.Single(creation.Items);
        DateTimeOffset authoringStartedAt =
            new(2026, 9, 8, 11, 0, 0, TimeSpan.Zero);
        AuthoringOperationClaim claim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    creation.Run.Id,
                    item.Id,
                    authoringStartedAt));
        AuthoringReceiptAcceptance receipt =
            await fixture.AcceptAsync(
                creation.Run,
                item,
                claim,
                SampleProse("accepted"));
        BallotNotesAuthoringWorkItemStore store =
            new(fixture.Database, fixture.AuthoringStore);
        BallotNotesAuthoringWorkItem persisted = Assert.Single(
            await store.GetPendingAsync(
                creation.Run.Id,
                1,
                CancellationToken.None));
        DateTimeOffset leaseStartedAt = authoringStartedAt.AddHours(1);
        _ = Assert.IsType<AuthoringQueueClaim>(
            await store.TryClaimAsync(
                persisted,
                leaseStartedAt,
                CancellationToken.None));

        AuthoringOrphanRecoveryResult early =
            await store.ResetOrphanedItemsAsync(
                creation.Run.Id,
                TimeSpan.FromMinutes(10),
                leaseStartedAt.AddMinutes(5),
                CancellationToken.None);

        Assert.Equal(0, early.RecoveredItems);
        Assert.Equal(leaseStartedAt.AddMinutes(10), early.NextRecoveryAt);
        Assert.Equal(
            AuthoringStatusValues.Items.InProgress,
            Assert.Single(
                await fixture.AuthoringStore.GetRunItemsAsync(
                    creation.Run.Id)).Status);

        AuthoringOrphanRecoveryResult due =
            await store.ResetOrphanedItemsAsync(
                creation.Run.Id,
                TimeSpan.FromMinutes(10),
                leaseStartedAt.AddMinutes(10),
                CancellationToken.None);
        AuthoringRunItemRecord recovered = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(creation.Run.Id));

        Assert.Equal(1, due.RecoveredItems);
        Assert.Null(due.NextRecoveryAt);
        Assert.Equal(AuthoringStatusValues.Items.Persisted, recovered.Status);
        Assert.Equal(receipt.Receipt.ReceiptId, recovered.AcceptedReceiptId);
        Assert.Equal(1, recovered.AttemptCount);
    }

    [Fact]
    public async Task ExhaustedError_FinalizesAndActivatesOldestQueuedRun()
    {
        using Fixture fixture = new(authoringMaxAttempts: 1);
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord firstExecution =
            fixture.CreateCompletedExecution("note-a", "Artifact");
        NotesHydrationExecutionRecord secondExecution =
            fixture.CreateCompletedExecution("note-b", "Page");
        BallotNotesAuthoringRunCreation first =
            await fixture.Coordinator.CreateRunAsync(
                new BallotNotesAuthoringRunRequest(
                    firstExecution.Id,
                    DatabaseOnly: true));
        BallotNotesAuthoringRunCreation second =
            await fixture.Coordinator.CreateRunAsync(
                new BallotNotesAuthoringRunRequest(
                    secondExecution.Id,
                    DatabaseOnly: true));
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            first.Run.Id));
        AuthoringRunItemRecord firstItem = Assert.Single(first.Items);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await fixture.AuthoringStore.ClaimItemAsync(
                first.Run.Id,
                firstItem.Id));
        await fixture.AuthoringStore.MarkClaimErrorAsync(
            firstItem.Id,
            claim.OperationId,
            "terminal failure");
        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            Assert.Single(
                await fixture.AuthoringStore.GetRunItemsAsync(first.Run.Id))
                .Status);

        AuthoringRunScheduler<BallotNotesAuthoringWorkItem> scheduler =
            fixture.CreateScheduler();
        await scheduler.RunCycleAsync();

        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await fixture.AuthoringStore.GetRunAsync(first.Run.Id))!.Status);
        Assert.Equal(
            AuthoringStatusValues.Runs.Queued,
            (await fixture.AuthoringStore.GetRunAsync(second.Run.Id))!.Status);

        await scheduler.RunCycleAsync();

        Assert.Equal(
            AuthoringStatusValues.Runs.Running,
            (await fixture.AuthoringStore.GetRunAsync(second.Run.Id))!.Status);
        Assert.Equal(
            second.Run.Id,
            (await fixture.AuthoringStore.GetFencedRunAsync(
                fixture.Coordinator.ProcessorKind))!.Id);
    }

    [Fact]
    public async Task SupersedeEndpoint_RequiresReasonAndRejectsReceiptBackedItem()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution(
                ("note-a", "Artifact"),
                ("note-b", "Page"));
        BallotNotesAuthoringRunCreation creation =
            await fixture.Coordinator.CreateRunAsync(
                new BallotNotesAuthoringRunRequest(
                    execution.Id,
                    DatabaseOnly: true));
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        AuthoringRunItemRecord unpersisted =
            creation.Items.Single(item => item.BusinessKey == "note-a");
        AuthoringOperationClaim unpersistedClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    creation.Run.Id,
                    unpersisted.Id));
        await fixture.AuthoringStore.MarkClaimErrorAsync(
            unpersisted.Id,
            unpersistedClaim.OperationId,
            "worker failure");

        AuthoringRunItemRecord receiptBacked =
            creation.Items.Single(item => item.BusinessKey == "note-b");
        AuthoringOperationClaim receiptClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    creation.Run.Id,
                    receiptBacked.Id));
        _ = await fixture.AcceptAsync(
            creation.Run,
            receiptBacked,
            receiptClaim,
            SampleProse("accepted"));
        BallotNotesAuthoringWorkItemStore store =
            new(fixture.Database, fixture.AuthoringStore);
        BallotNotesAuthoringWorkItem persisted = Assert.Single(
            await store.GetPendingAsync(
                creation.Run.Id,
                10,
                CancellationToken.None));
        AuthoringQueueClaim persistenceLease =
            Assert.IsType<AuthoringQueueClaim>(
                await store.TryClaimAsync(
                    persisted,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None));
        await store.ApplyResultAsync(
            persisted,
            persistenceLease,
            AuthoringWorkResult.Retry("post-receipt failure"),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        BallotNotesAuthoringRunsController controller =
            fixture.CreateController();
        Assert.IsType<BadRequestObjectResult>(
            await controller.SupersedeItem(
                creation.Run.Id,
                unpersisted.Id,
                new AuthoringItemSupersedeRequest(" "),
                CancellationToken.None));
        OkObjectResult superseded = Assert.IsType<OkObjectResult>(
            await controller.SupersedeItem(
                creation.Run.Id,
                unpersisted.Id,
                new AuthoringItemSupersedeRequest("not actionable"),
                CancellationToken.None));
        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            Assert.IsType<AuthoringItemSupersedeResult>(superseded.Value).Status);
        Assert.IsType<ConflictObjectResult>(
            await controller.SupersedeItem(
                creation.Run.Id,
                receiptBacked.Id,
                new AuthoringItemSupersedeRequest("discard"),
                CancellationToken.None));

        OkObjectResult statusResult = Assert.IsType<OkObjectResult>(
            await controller.GetRun(
                creation.Run.Id,
                CancellationToken.None));
        BallotNotesAuthoringRunResponse status =
            Assert.IsType<BallotNotesAuthoringRunResponse>(statusResult.Value);
        Assert.Equal(2, status.Run.FailedItems);
        Assert.Equal(1, status.Run.RetryableErrorItems);
        Assert.Equal(1, status.Run.SupersededItems);
        Assert.Null(status.Items.Single(
            item => item.ItemId == receiptBacked.Id).AttemptsRemaining);
    }

    [Fact]
    public async Task InitialRevalidation_RetiresSupersededLegacyNoteBeforeCompletion()
    {
        using Fixture fixture = new();
        fixture.SeedLegacyNote("legacy-note");
        fixture.Database.UpdateNoteProse(
            "legacy-note",
            SampleProse("legacy"),
            DateTimeOffset.UtcNow);
        Assert.Equal(1, await fixture.Database.ClassifyLegacyNotesAsync());
        NotesHydrationExecutionRecord baseline =
            await fixture.Database.BuildCutoverBaselineExecutionAsync();
        await fixture.ActivateAsync();
        (AuthoringRunRecord run, AuthoringRunItemRecord item,
            AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync(
                baseline.Id,
                databaseOnly: true,
                noteIds: ["legacy-note"]);
        await fixture.AuthoringStore.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "not actionable");
        await fixture.AuthoringStore.SupersedeErroredItemAsync(
            run.Id,
            item.Id,
            "not actionable");
        fixture.MarkAsInitialRevalidation(run.Id);

        Assert.Equal(1, await fixture.Database.CountLegacyUnverifiedAsync());
        Assert.Null(await fixture.CreatePostProcessor().FinalizeRunAsync(run.Id));

        Assert.Equal(0, await fixture.Database.CountLegacyUnverifiedAsync());
        Assert.Equal(
            "superseded",
            fixture.Scalar<string>(
                "SELECT Classification FROM note_authoring_state WHERE NoteId = 'legacy-note'"));
        Assert.Equal(
            "superseded",
            fixture.Scalar<string>(
                "SELECT ProseVerificationStatus FROM notes WHERE NoteId = 'legacy-note'"));
        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
        Assert.False((await fixture.AuthoringStore.GetProcessorModeAsync(
            fixture.Coordinator.ProcessorKind)).RevalidationRequired);
    }

    [Fact]
    public async Task CompletedReceipt_IsNeverReauthoredOrSuperseded()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution("note-a", "Artifact");
        (AuthoringRunRecord run, AuthoringRunItemRecord item,
            AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync(execution.Id);
        AuthoringReceiptAcceptance receipt =
            await fixture.AcceptAsync(
                run,
                item,
                claim,
                SampleProse("complete"));
        await fixture.AuthoringStore.MarkItemCompleteAsync(
            item.Id,
            receipt.Receipt.ReceiptId);

        await Assert.ThrowsAsync<AuthoringConflictException>(
            () => fixture.ControlService.SupersedeItemAsync(
                fixture.Coordinator.ProcessorKind,
                run.Id,
                item.Id,
                new AuthoringItemSupersedeRequest("discard")));
        await fixture.AuthoringStore.ReconcileErroredItemsAsync(run.Id);
        BallotNotesAuthoringWorkItemStore store =
            new(fixture.Database, fixture.AuthoringStore);

        Assert.Empty(await store.GetPendingAsync(
            run.Id,
            10,
            CancellationToken.None));
        AuthoringRunItemRecord current = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(run.Id));
        Assert.Equal(AuthoringStatusValues.Items.Complete, current.Status);
        Assert.Equal(receipt.Receipt.ReceiptId, current.AcceptedReceiptId);
        Assert.Equal(1, current.AttemptCount);
    }

    [Fact]
    public async Task FinalizingRunRecoversAfterRestartAndReleasesFence()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution("note-a", "Artifact");
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync(execution.Id, databaseOnly: true);
        AuthoringReceiptAcceptance receipt =
            await fixture.AcceptAsync(run, item, claim, SampleProse("draft"));
        await fixture.AuthoringStore.MarkItemCompleteAsync(
            item.Id,
            receipt.Receipt.ReceiptId);
        await fixture.AuthoringStore.MarkRunFinalizingAsync(run.Id);

        BallotNotesRunPostProcessor restarted =
            fixture.CreatePostProcessor();
        Assert.Null(await restarted.FinalizeRunAsync(run.Id));

        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
        Assert.NotNull(fixture.Database.TryAcquireHydrationLease("after-run"));
    }

    [Fact]
    public async Task ActiveAuthoringRunFencesHydration()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution("note-a", "Artifact");

        BallotNotesAuthoringRunCreation creation =
            await fixture.Coordinator.CreateRunAsync(
            new BallotNotesAuthoringRunRequest(execution.Id, DatabaseOnly: true));
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));

        Assert.Null(fixture.Database.TryAcquireHydrationLease("concurrent"));
    }

    [Fact]
    public async Task CanonicalSnapshotBlocksUntilLegacyRevalidationCompletes()
    {
        using Fixture fixture = new();
        fixture.SeedLegacyNote("legacy-note");
        fixture.Database.UpdateNoteProse(
            "legacy-note",
            SampleProse("legacy"),
            DateTimeOffset.UtcNow);
        await fixture.Database.ClassifyLegacyNotesAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution("current-note", "Artifact");
        await fixture.ActivateAsync();
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync(execution.Id, databaseOnly: false);
        AuthoringReceiptAcceptance receipt =
            await fixture.AcceptAsync(run, item, claim, SampleProse("current"));
        await fixture.AuthoringStore.MarkItemCompleteAsync(
            item.Id,
            receipt.Receipt.ReceiptId);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.CreatePostProcessor().FinalizeRunAsync(run.Id));

        Assert.Contains(
            "legacy revalidation",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            AuthoringStatusValues.Runs.Superseded,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task LegacyRevalidationProducesReceiptBackedSnapshotOnly()
    {
        using Fixture fixture = new();
        fixture.SeedLegacyNote("legacy-note");
        fixture.Database.UpdateNoteProse(
            "legacy-note",
            SampleProse("legacy"),
            DateTimeOffset.UtcNow);
        Assert.Equal(1, await fixture.Database.ClassifyLegacyNotesAsync());
        Assert.Equal(0, fixture.Scalar<int>(
            "SELECT COUNT(*) FROM authoring_result_receipts"));
        fixture.SeedLegacyNote("foreign-note");
        NotesHydrationExecutionRecord baseline =
            await fixture.Database.BuildCutoverBaselineExecutionAsync();
        await fixture.ActivateAsync();
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync(
                baseline.Id,
                databaseOnly: false,
                noteIds: ["legacy-note"]);
        AuthoringReceiptAcceptance receipt =
            await fixture.AcceptAsync(run, item, claim, SampleProse("revalidated"));
        await fixture.AuthoringStore.MarkItemCompleteAsync(
            item.Id,
            receipt.Receipt.ReceiptId);

        AuthoringSnapshotDescriptor descriptor =
            (await fixture.CreatePostProcessor().FinalizeRunAsync(run.Id))!;
        string snapshotPath = Path.Combine(
            fixture.Options.SnapshotDirectory,
            descriptor.FileName);
        using SqliteConnection snapshot = OpenReadOnly(snapshotPath);

        Assert.Equal(0, await fixture.Database.CountLegacyUnverifiedAsync());
        Assert.Equal(1, Scalar<int>(snapshot, "SELECT COUNT(*) FROM notes"));
        Assert.Equal(1, Scalar<int>(
            snapshot,
            "SELECT COUNT(*) FROM authoring_result_receipts"));
        Assert.Equal(0, Scalar<int>(
            snapshot,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('authoring_run_attempts','authoring_processor_modes','authoring_mutation_fences','note_authoring_state')"));
        foreach ((string table, long count) in descriptor.TableCounts)
        {
            Assert.Equal(
                count,
                Scalar<long>(
                    snapshot,
                    $"SELECT COUNT(*) FROM \"{table}\""));
        }
    }

    [Fact]
    public async Task SnapshotExcludesForeignCompletedExecution()
    {
        using Fixture fixture = new();
        NotesHydrationExecutionRecord selected =
            fixture.CreateCompletedExecution("note-a", "Artifact");
        NotesHydrationExecutionRecord foreign =
            fixture.CreateCompletedExecution("note-b", "Page");
        await fixture.ActivateAsync();
        (AuthoringRunRecord run, AuthoringRunItemRecord item, AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync(
                selected.Id,
                databaseOnly: false);
        AuthoringReceiptAcceptance receipt =
            await fixture.AcceptAsync(run, item, claim, SampleProse("draft"));
        await fixture.AuthoringStore.MarkItemCompleteAsync(
            item.Id,
            receipt.Receipt.ReceiptId);

        AuthoringSnapshotDescriptor descriptor =
            (await fixture.CreatePostProcessor().FinalizeRunAsync(run.Id))!;
        using SqliteConnection snapshot = OpenReadOnly(Path.Combine(
            fixture.Options.SnapshotDirectory,
            descriptor.FileName));

        Assert.Equal(
            selected.Id,
            Scalar<string>(
                snapshot,
                "SELECT Id FROM notes_hydration_executions"));
        Assert.DoesNotContain(
            foreign.Id,
            Scalar<string>(
                snapshot,
                "SELECT group_concat(Id, ',') FROM notes_hydration_executions"));
        Assert.Equal("note-a", Scalar<string>(snapshot, "SELECT NoteId FROM notes"));
    }

    [Fact]
    public async Task SnapshotIncludesCurrentReceiptBackedNotesFromSupersededPriorRuns()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord firstExecution =
            fixture.CreateCompletedExecution("note-a", "Artifact");
        (AuthoringRunRecord firstRun, AuthoringRunItemRecord firstItem,
            AuthoringOperationClaim firstClaim) =
            await fixture.CreateClaimAsync(firstExecution.Id);
        AuthoringReceiptAcceptance firstReceipt =
            await fixture.AcceptAsync(
                firstRun,
                firstItem,
                firstClaim,
                SampleProse("first"));
        await fixture.AuthoringStore.MarkItemCompleteAsync(
            firstItem.Id,
            firstReceipt.Receipt.ReceiptId);
        await fixture.CreatePostProcessor().FinalizeRunAsync(firstRun.Id);
        fixture.Execute(
            """
            UPDATE authoring_run_items
            SET Status = 'superseded'
            WHERE Id = @itemId;
            UPDATE authoring_runs
            SET Status = 'superseded'
            WHERE Id = @runId;
            """,
            ("@itemId", firstItem.Id),
            ("@runId", firstRun.Id));
        Assert.True(fixture.Database.GetNote("note-a")!.IsCurrentProseReceiptBacked);
        Assert.Single(
            fixture.Database.ListNotes(
                new NoteQueryFilter
                {
                    Status = "authored",
                }),
            note => note.NoteId == "note-a");

        NotesHydrationExecutionRecord secondExecution =
            fixture.CreateCompletedExecution("note-b", "Page");
        (AuthoringRunRecord secondRun, AuthoringRunItemRecord secondItem,
            AuthoringOperationClaim secondClaim) =
            await fixture.CreateClaimAsync(
                secondExecution.Id,
                databaseOnly: false);
        AuthoringReceiptAcceptance secondReceipt =
            await fixture.AcceptAsync(
                secondRun,
                secondItem,
                secondClaim,
                SampleProse("second"));
        await fixture.AuthoringStore.MarkItemCompleteAsync(
            secondItem.Id,
            secondReceipt.Receipt.ReceiptId);

        AuthoringSnapshotDescriptor descriptor =
            (await fixture.CreatePostProcessor()
                .FinalizeRunAsync(secondRun.Id))!;
        using SqliteConnection snapshot = OpenReadOnly(Path.Combine(
            fixture.Options.SnapshotDirectory,
            descriptor.FileName));

        Assert.Equal(secondRun.Id, descriptor.RunId);
        Assert.Equal(2, descriptor.ReceiptCount);
        Assert.Equal(2, Scalar<int>(
            snapshot,
            "SELECT COUNT(*) FROM notes"));
        Assert.Equal(2, Scalar<int>(
            snapshot,
            "SELECT COUNT(*) FROM authoring_result_receipts"));
        Assert.Equal(2, Scalar<int>(
            snapshot,
            "SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(
            "note-a,note-b",
            Scalar<string>(
                snapshot,
                "SELECT group_concat(NoteId, ',') FROM (SELECT NoteId FROM notes ORDER BY NoteId)"));
    }

    [Fact]
    public async Task SnapshotItemCountIncludesCompleteAndSupersededItems()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution(
                ("note-a", "Artifact"),
                ("note-b", "Page"));
        BallotNotesAuthoringRunCreation creation =
            await fixture.Coordinator.CreateRunAsync(
                new BallotNotesAuthoringRunRequest(
                    execution.Id,
                    DatabaseOnly: false));
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        AuthoringRunItemRecord completeItem =
            creation.Items.Single(item => item.BusinessKey == "note-a");
        AuthoringOperationClaim claim =
            (await fixture.AuthoringStore.ClaimItemAsync(
                creation.Run.Id,
                completeItem.Id))!;
        AuthoringReceiptAcceptance receipt =
            await fixture.AcceptAsync(
                creation.Run,
                completeItem,
                claim,
                SampleProse("complete"));
        await fixture.AuthoringStore.MarkItemCompleteAsync(
            completeItem.Id,
            receipt.Receipt.ReceiptId);
        fixture.Execute(
            "UPDATE notes SET CurrentEvidenceRevision = 'newer' WHERE NoteId = 'note-b'");
        await fixture.Coordinator.SupersedeStaleItemsAsync(creation.Run.Id);
        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            (await fixture.AuthoringStore.GetRunItemsAsync(creation.Run.Id))
                .Single(item => item.BusinessKey == "note-b")
                .Status);

        AuthoringSnapshotDescriptor descriptor =
            (await fixture.CreatePostProcessor()
                .FinalizeRunAsync(creation.Run.Id))!;

        Assert.Equal(2, descriptor.ItemCount);
        Assert.Equal(1, descriptor.ReceiptCount);
    }

    [Fact]
    public async Task InitialReplacementWaitsForAcceptedPostPersistenceWork()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution(
                ("note-a", "Artifact"),
                ("note-b", "Page"));
        BallotNotesAuthoringRunCreation creation =
            await fixture.Coordinator.CreateRunAsync(
                new BallotNotesAuthoringRunRequest(
                    execution.Id,
                    DatabaseOnly: false));
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        AuthoringRunItemRecord persisted =
            creation.Items.Single(item => item.BusinessKey == "note-b");
        AuthoringOperationClaim claim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    creation.Run.Id,
                    persisted.Id));
        AuthoringReceiptAcceptance receipt =
            await fixture.AcceptAsync(
                creation.Run,
                persisted,
                claim,
                SampleProse("persisted"));
        fixture.Execute(
            """
            UPDATE authoring_processor_modes
            SET RevalidationRequired = 1,
                RevalidationRunId = @runId
            WHERE ProcessorKind = @processorKind;
            UPDATE notes
            SET CurrentEvidenceRevision = 'new-revision'
            WHERE NoteId = 'note-a';
            """,
            ("@runId", creation.Run.Id),
            ("@processorKind", fixture.Coordinator.ProcessorKind));

        Assert.False(
            await fixture.Coordinator.SupersedeStaleItemsAsync(
                creation.Run.Id));
        BallotNotesAuthoringWorkItemStore workItems = new(
            fixture.Database,
            fixture.AuthoringStore);
        BallotNotesAuthoringWorkItem pending = Assert.Single(
            await workItems.GetPendingAsync(
                creation.Run.Id,
                10,
                CancellationToken.None));
        Assert.Equal("note-b", pending.RunItem.BusinessKey);
        Assert.Equal(
            AuthoringStatusValues.Items.Persisted,
            pending.RunItem.Status);

        await fixture.AuthoringStore.MarkItemCompleteAsync(
            persisted.Id,
            receipt.Receipt.ReceiptId);
        Assert.True(
            await fixture.Coordinator.SupersedeStaleItemsAsync(
                creation.Run.Id));
        AuthoringProcessorModeRecord mode =
            await fixture.AuthoringStore.GetProcessorModeAsync(
                fixture.Coordinator.ProcessorKind);
        Assert.Equal(
            "note-a",
            Assert.Single(
                await fixture.AuthoringStore.GetRunItemsAsync(
                    mode.RevalidationRunId!)).BusinessKey);
        Assert.Equal(
            AuthoringStatusValues.Items.Complete,
            (await fixture.AuthoringStore.GetRunItemsAsync(creation.Run.Id))
            .Single(item => item.BusinessKey == "note-b")
            .Status);
    }

    [Fact]
    public async Task WorkgroupReallocationPreservesReceiptFreshness()
    {
        using Fixture fixture = new();
        await fixture.ActivateAsync();
        NotesHydrationExecutionRecord execution =
            fixture.CreateCompletedExecution("note-a", "Artifact");
        (AuthoringRunRecord run, AuthoringRunItemRecord item,
            AuthoringOperationClaim claim) =
            await fixture.CreateClaimAsync(execution.Id);
        AuthoringReceiptAcceptance receipt =
            await fixture.AcceptAsync(
                run,
                item,
                claim,
                SampleProse("draft"));
        await fixture.AuthoringStore.MarkItemCompleteAsync(
            item.Id,
            receipt.Receipt.ReceiptId);
        await fixture.CreatePostProcessor().FinalizeRunAsync(run.Id);
        NoteRecord before = fixture.Database.GetNote("note-a")!.Note;

        await fixture.Database.ReallocateWorkGroupsAsync(
            new BallotNotesWorkGroupReallocationRequest(
                [
                    new BallotNoteWorkGroupReallocation(
                        "note-a",
                        before.CurrentEvidenceRevision,
                        "Patient Administration",
                        "pa",
                        "Patient Administration",
                        "pa"),
                ]));

        NoteDetail after = fixture.Database.GetNote("note-a")!;
        Assert.Equal(before.CurrentEvidenceHash, after.Note.CurrentEvidenceHash);
        Assert.Equal(
            before.CurrentEvidenceRevision,
            after.Note.CurrentEvidenceRevision);
        Assert.Equal("pa", after.Note.WorkGroupCode);
        Assert.Equal("authored", after.Status);
    }

    private static BallotNoteProse SampleProse(string text)
        => new()
        {
            NeedsNote = "yes",
            ProposedBallotNoteHtml =
                $"<blockquote class=\"ballot-note\">{text}</blockquote>",
            RollupSummaryMarkdown = text,
            NotesForReviewerMarkdown = text,
        };

    private static SqliteConnection OpenReadOnly(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private sealed class FakeCommandRunner
        : IBallotNotesAuthoringCommandRunner
    {
        public int CallCount { get; private set; }
        public BallotNotesAuthoringCommand? LastCommand { get; private set; }

        public Task<BallotNotesAuthoringCommandResult> RunAsync(
            BallotNotesAuthoringCommand command,
            CancellationToken ct)
        {
            CallCount++;
            LastCommand = command;
            return Task.FromResult(
                new BallotNotesAuthoringCommandResult(0, "", "", false));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly IOptions<ProcessingServiceOptions> _processingOptions;

        public Fixture(int authoringMaxAttempts = 3)
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                $"ballotnotes-authoring-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            DatabasePath = Path.Combine(_directory, "notes.db");
            Database = new BallotNotesDatabase(
                DatabasePath,
                NullLogger<BallotNotesDatabase>.Instance);
            Database.Initialize();
            Options = new BallotNotesServiceOptions
            {
                DatabasePath = DatabasePath,
                SnapshotDirectory = Path.Combine(_directory, "snapshots"),
                StartProcessingOnStartup = true,
                AuthoringMaxAttempts = authoringMaxAttempts,
            };
            _processingOptions =
                Microsoft.Extensions.Options.Options.Create<ProcessingServiceOptions>(
                    Options);
            RetryPolicy = new AuthoringRetryPolicy(_processingOptions);
            AuthoringStore = new AuthoringRunStore(
                Database.OpenConnection,
                retryPolicy: RetryPolicy);
            ControlService =
                new AuthoringRunControlService(AuthoringStore, RetryPolicy);
            Coordinator = new BallotNotesAuthoringRunCoordinator(
                Database,
                AuthoringStore);
        }

        public string DatabasePath { get; }
        public BallotNotesDatabase Database { get; }
        public AuthoringRunStore AuthoringStore { get; }
        public AuthoringRetryPolicy RetryPolicy { get; }
        public AuthoringRunControlService ControlService { get; }
        public BallotNotesAuthoringRunCoordinator Coordinator { get; }
        public BallotNotesServiceOptions Options { get; }

        public async Task ActivateAsync()
        {
            await AuthoringStore.EnsureProcessorModeAsync(
                Coordinator.ProcessorKind);
            await AuthoringStore.TransitionProcessorModeAsync(
                Coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.Legacy,
                AuthoringStatusValues.ProcessorModes.CuttingOver);
            await AuthoringStore.TransitionProcessorModeAsync(
                Coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.CuttingOver,
                AuthoringStatusValues.ProcessorModes.RunBacked);
        }

        public NotesHydrationExecutionRecord CreateCompletedExecution(
            string noteId,
            string type)
            => CreateCompletedExecution((noteId, type));

        public NotesHydrationExecutionRecord CreateCompletedExecution(
            params (string NoteId, string Type)[] notes)
        {
            string id = Guid.NewGuid().ToString("N");
            HydrationMutationLease lease =
                Database.TryAcquireHydrationLease(id)!;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            NotesHydrationExecutionRecord execution = new()
            {
                Id = id,
                RunKey = $"HL7/fhir@since..head-{id}",
                RepoOwner = "HL7",
                RepoName = "fhir",
                SinceSha = "since",
                SinceShortSha = "since",
                HeadSha = "head",
                HeadShortSha = "head",
                Status = "running",
                StartedAt = now,
            };
            Database.BeginHydrationExecution(execution, lease);
            Database.SetHydrationMembership(
                id,
                lease,
                notes.Select(note =>
                    new HydrationMembershipDefinition(
                        note.NoteId,
                        note.Type))
                    .ToArray());
            foreach ((string noteId, string type) in notes)
            {
                Database.UpsertUnitEvidence(
                    id,
                    lease,
                    Evidence(noteId, type),
                    [new NoteSourceFileRecord
                    {
                        NoteId = noteId,
                        Path = $"source/{noteId}.xml",
                        Role = "source",
                        TouchedInWindow = true,
                    }],
                    [],
                    []);
            }
            Database.BumpHydrationProgress(
                id,
                lease,
                notes.Length,
                0,
                0);
            Database.FinishHydrationExecution(
                id,
                lease,
                "completed",
                null);
            Database.ReleaseMutationLease(lease);
            return Database.GetHydrationExecution(id)!;
        }

        public void Execute(
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using SqliteConnection connection = Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            foreach ((string name, object value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }
            command.ExecuteNonQuery();
        }

        public (NotesHydrationExecutionRecord Execution, HydrationMutationLease Lease)
            CreateRunningExecution()
        {
            string id = Guid.NewGuid().ToString("N");
            HydrationMutationLease lease =
                Database.TryAcquireHydrationLease(id)!;
            NotesHydrationExecutionRecord execution = new()
            {
                Id = id,
                RunKey = $"HL7/fhir@since..running-{id}",
                RepoOwner = "HL7",
                RepoName = "fhir",
                SinceSha = "since",
                SinceShortSha = "since",
                HeadSha = "running",
                HeadShortSha = "running",
                Status = "running",
                StartedAt = DateTimeOffset.UtcNow.AddSeconds(1),
            };
            Database.BeginHydrationExecution(execution, lease);
            return (execution, lease);
        }

        public void SeedLegacyNote(string noteId)
            => Database.UpsertUnitEvidence(
                Evidence(noteId, "Artifact"),
                [],
                [],
                []);

        public async Task<(AuthoringRunRecord, AuthoringRunItemRecord, AuthoringOperationClaim)>
            CreateClaimAsync(
                string executionId,
                bool databaseOnly = true,
                IReadOnlyList<string>? noteIds = null)
        {
            BallotNotesAuthoringRunCreation creation =
                await Coordinator.CreateRunAsync(
                    new BallotNotesAuthoringRunRequest(
                        executionId,
                        noteIds,
                        DatabaseOnly: databaseOnly));
            Assert.True(await AuthoringStore.TryAcquireMutationFenceAsync(
                Coordinator.ProcessorKind,
                creation.Run.Id));
            AuthoringRunItemRecord item = Assert.Single(creation.Items);
            AuthoringOperationClaim claim =
                (await AuthoringStore.ClaimItemAsync(
                    creation.Run.Id,
                    item.Id))!;
            return (creation.Run, item, claim);
        }

        public BallotNoteAuthoringResultRequest CreateResultRequest(
            AuthoringRunRecord run,
            AuthoringRunItemRecord item,
            AuthoringOperationClaim claim,
            BallotNoteProse prose)
            => new(
                new AuthoringResultSubmission(
                    run.Id,
                    item.Id,
                    claim.OperationId,
                    item.ExpectedSourceRevision,
                    BallotNotesDatabase.ComputeProseHash(prose)),
                new BallotNoteProsePutRequest
                {
                    NeedsNote = prose.NeedsNote,
                    ProposedBallotNoteHtml =
                        prose.ProposedBallotNoteHtml,
                    RollupSummaryMarkdown =
                        prose.RollupSummaryMarkdown,
                    NotesForReviewerMarkdown =
                        prose.NotesForReviewerMarkdown,
                    SourceFilesNote = prose.SourceFilesNote,
                });

        public async Task<AuthoringReceiptAcceptance> AcceptAsync(
            AuthoringRunRecord run,
            AuthoringRunItemRecord item,
            AuthoringOperationClaim claim,
            BallotNoteProse prose)
        {
            OkObjectResult result = Assert.IsType<OkObjectResult>(
                await CreateController().SubmitResult(
                    run.Id,
                    item.Id,
                    item.ItemKind,
                    item.BusinessKey,
                    claim.OperationToken,
                    CreateResultRequest(run, item, claim, prose),
                    CancellationToken.None));
            return Assert.IsType<AuthoringReceiptAcceptance>(result.Value);
        }

        public BallotNotesAuthoringRunsController CreateController()
            => new(
                AuthoringStore,
                ControlService,
                Coordinator,
                Database);

        public BallotNotesRunPostProcessor CreatePostProcessor()
            => new(
                Database,
                AuthoringStore,
                new AuthoringRunFinalizer(AuthoringStore),
                new SqliteReviewSnapshotReconciler(AuthoringStore),
                Coordinator,
                Microsoft.Extensions.Options.Options.Create(Options));

        public AuthoringRunScheduler<BallotNotesAuthoringWorkItem> CreateScheduler()
        {
            ProcessingLifecycleService lifecycle =
                new(_processingOptions);
            BallotNotesAuthoringWorkItemStore store =
                new(Database, AuthoringStore);
            BallotNotesAuthoringHandler handler = new(
                AuthoringStore,
                new FakeCommandRunner(),
                Microsoft.Extensions.Options.Options.Create(Options));
            AuthoringQueueRunner<BallotNotesAuthoringWorkItem> runner = new(
                store,
                handler,
                lifecycle,
                _processingOptions,
                NullLogger<AuthoringQueueRunner<BallotNotesAuthoringWorkItem>>.Instance);
            return new AuthoringRunScheduler<BallotNotesAuthoringWorkItem>(
                AuthoringStore,
                Coordinator,
                CreatePostProcessor(),
                runner,
                lifecycle,
                _processingOptions,
                NullLogger<AuthoringRunScheduler<BallotNotesAuthoringWorkItem>>.Instance);
        }

        public void MarkAsInitialRevalidation(string runId)
            => Execute(
                """
                UPDATE authoring_processor_modes
                SET RevalidationRequired = 1,
                    RevalidationRunId = @runId
                WHERE ProcessorKind = @processorKind
                """,
                ("@runId", runId),
                ("@processorKind", Coordinator.ProcessorKind));

        public T Scalar<T>(string sql)
        {
            using SqliteConnection connection = Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            TestFileCleanup.SafeDeleteDirectory(_directory);
        }

        private static NoteRecord Evidence(string noteId, string type)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return new NoteRecord
            {
                NoteId = noteId,
                Type = type,
                Name = noteId,
                RepoOwner = "HL7",
                RepoName = "fhir",
                SinceSha = "since",
                SinceShortSha = "since",
                HeadSha = "head",
                HeadShortSha = "head",
                WorkGroup = "FHIR Infrastructure",
                WorkGroupCode = "fhir",
                GeneratedAt = now,
                SavedAt = now,
            };
        }
    }
}
