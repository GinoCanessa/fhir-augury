using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Jira.Common.Tests.Authoring;

public sealed class JiraAuthoringRunCoordinatorTests
{
    [Fact]
    public async Task CreateScheduledRun_LegacyModeIsNotActivatable()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.SeedAsync("FHIR-1", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => fixture.Coordinator.CreateScheduledRunAsync());

        Assert.Equal(AuthoringConflictCode.AuthoringNotActivated, conflict.Code);
    }

    [Fact]
    public async Task CreateScheduledRun_FreezesRevisionAndIgnoresLegacyCompletion()
    {
        using JiraAuthoringTestFixture fixture = new();
        DateTimeOffset firstRevision = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord source = await fixture.SeedAsync("FHIR-1", firstRevision);
        await fixture.SourceStore.MarkCompleteAsync(source, DateTimeOffset.UtcNow, CancellationToken.None);
        await fixture.ActivateAsync();

        JiraAuthoringRunCreation first =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;
        Assert.Equal(AuthoringStatusValues.Runs.Queued, first.Run.Status);
        Assert.Equal(firstRevision.ToString("O"), Assert.Single(first.Items).ExpectedSourceRevision);
        Assert.Null(await fixture.Coordinator.CreateScheduledRunAsync());

        DateTimeOffset secondRevision = firstRevision.AddDays(1);
        await fixture.SeedAsync("FHIR-1", secondRevision, title: "Updated");
        JiraAuthoringRunCreation second =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;

        Assert.Equal(AuthoringStatusValues.Runs.Queued, second.Run.Status);
        Assert.Equal(secondRevision.ToString("O"), Assert.Single(second.Items).ExpectedSourceRevision);
        Assert.Equal(
            firstRevision.ToString("O"),
            Assert.Single(await fixture.AuthoringStore.GetRunItemsAsync(first.Run.Id)).ExpectedSourceRevision);
    }

    [Fact]
    public async Task CreateScheduledRun_LabelsApplyToPreviouslyStoredCandidates()
    {
        using JiraAuthoringTestFixture fixture = new();
        DateTimeOffset revision = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", revision.AddDays(-1));
        await fixture.SeedAsync(
            "FHIR-2", revision, sourceRefresh: revision.AddHours(-1), contentRevision: 42);
        await fixture.SeedAsync("FHIR-3", revision);
        JiraProcessingSourceTicketRecord before = Assert.IsType<JiraProcessingSourceTicketRecord>(
            await fixture.SourceStore.GetByKeyAsync("FHIR-2", "fhir", CancellationToken.None));
        await fixture.ActivateAsync();
        fixture.Options.Value.LabelsToInclude = ["cohort"];
        fixture.Options.Value.LabelsToExclude = ["blocked"];
        fixture.Matcher.Enqueue(["FHIR-3", "fhir-2", "FHIR-999"]);

        JiraAuthoringRunCreation creation = Assert.IsType<JiraAuthoringRunCreation>(
            await fixture.Coordinator.CreateScheduledRunAsync(maxItems: 1));

        AuthoringRunItemRecord item = Assert.Single(creation.Items);
        Assert.Equal("FHIR-2", item.BusinessKey);
        Assert.Equal(JiraProcessingSourceTicketStore.GetSourceRevision(before), item.ExpectedSourceRevision);
        Assert.Equal(0, item.AttemptCount);
        Assert.Equal(["FHIR-1", "FHIR-2", "FHIR-3"], Assert.Single(fixture.Matcher.Calls).Keys);
        Assert.Equal(["cohort"], fixture.Matcher.Calls[0].Filters.LabelsToInclude);
        Assert.Equal(["blocked"], fixture.Matcher.Calls[0].Filters.LabelsToExclude);
        AuthoringRunInputProvenanceRecord provenance = Assert.Single(
            await fixture.AuthoringStore.GetRunInputProvenanceAsync(creation.Run.Id));
        Assert.Equal(42, provenance.ContentRevision);
        Assert.Equal(revision.AddHours(-1), provenance.LatestSuccessfulRefreshAt);
        Assert.Equal(before, await fixture.SourceStore.GetByKeyAsync(
            "FHIR-2", "fhir", CancellationToken.None));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateScheduledRun_SelectionFailureCreatesNoRunOrAttempt(bool laterBatch)
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        fixture.Options.Value.LabelsToInclude = ["cohort"];
        DateTimeOffset revision = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        int count = laterBatch ? 501 : 1;
        for (int index = 1; index <= count; index++)
        {
            await fixture.SeedAsync($"FHIR-{index:D4}", revision.AddSeconds(index));
        }
        JiraProcessingSourceTicketRecord before = Assert.IsType<JiraProcessingSourceTicketRecord>(
            await fixture.SourceStore.GetByKeyAsync("FHIR-0001", "fhir", CancellationToken.None));
        JiraTicketSelectionUnavailableException failure = new("Source selection is unavailable.");
        if (laterBatch)
        {
            fixture.Matcher.Enqueue(["FHIR-0001"]);
        }
        fixture.Matcher.Enqueue((_, _, _) => throw failure);

        JiraTicketSelectionUnavailableException actual =
            await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
                () => fixture.Coordinator.CreateScheduledRunAsync(maxItems: 2));

        Assert.Same(failure, actual);
        Assert.Equal(laterBatch ? 2 : 1, fixture.Matcher.Calls.Count);
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_items"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_input_provenance"));
        Assert.Equal(0, fixture.Scalar("SELECT SUM(ProcessingAttemptCount) FROM jira_processing_source_tickets"));
        Assert.Equal(before, await fixture.SourceStore.GetByKeyAsync(
            "FHIR-0001", "fhir", CancellationToken.None));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CreateScheduledRun_CollisionRetryReselectsConfiguredCandidates(int collisions)
    {
        using JiraAuthoringTestFixture fixture = new();
        await SeedCollisionCandidatesAsync(fixture, collisions + 1);
        QueueCollidingAdmissions(fixture, collisions);
        fixture.Matcher.Enqueue((keys, _, _) => Task.FromResult<IReadOnlyList<string>>([keys[0]]));

        JiraAuthoringRunCreation creation = Assert.IsType<JiraAuthoringRunCreation>(
            await fixture.Coordinator.CreateScheduledRunAsync(maxItems: 1, databaseOnly: true));

        Assert.Equal($"FHIR-{collisions + 1}", Assert.Single(creation.Items).BusinessKey);
        Assert.Equal(collisions + 1, fixture.Matcher.Calls.Count);
        for (int index = 0; index <= collisions; index++)
        {
            Assert.Equal(
                Enumerable.Range(index + 1, collisions + 1 - index).Select(value => $"FHIR-{value}"),
                fixture.Matcher.Calls[index].Keys);
            Assert.Same(fixture.Matcher.Calls[0].Filters, fixture.Matcher.Calls[index].Filters);
        }
        Assert.Equal(collisions + 1, fixture.Scalar("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(collisions + 1, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_items"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
    }

    [Fact]
    public async Task CreateScheduledRun_CollisionRetriesStopAfterThreeAttempts()
    {
        using JiraAuthoringTestFixture fixture = new();
        await SeedCollisionCandidatesAsync(fixture, 4);
        QueueCollidingAdmissions(fixture, 3);

        SqliteException conflict = await Assert.ThrowsAsync<SqliteException>(
            () => fixture.Coordinator.CreateScheduledRunAsync(maxItems: 1, databaseOnly: true));

        Assert.Equal(19, conflict.SqliteErrorCode);
        Assert.Equal([4, 3, 2], fixture.Matcher.Calls.Select(call => call.Keys.Count));
        Assert.Equal(["FHIR-1", "FHIR-2", "FHIR-3"], fixture.Matcher.Calls.Select(call => call.Keys[0]));
        Assert.Equal(3, fixture.Scalar("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(3, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_items"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
        Assert.Equal(
            "FHIR-4",
            Assert.Single(await fixture.SourceStore.GetLocalAuthoringCandidatesAsync(
                new JiraProcessingFilterResolver().Resolve(fixture.Options.Value),
                maxItems: null,
                CancellationToken.None)).Key);
    }

    [Fact]
    public async Task CreateScheduledRun_SelectionIsOutsideCollisionRetryCatch()
    {
        using JiraAuthoringTestFixture fixture = new();
        await SeedCollisionCandidatesAsync(fixture, 1);
        SqliteException failure = new("Matcher failure is not an admission collision.", 19);
        fixture.Matcher.Enqueue((_, _, _) => throw failure);

        Assert.Same(failure, await Assert.ThrowsAsync<SqliteException>(
            () => fixture.Coordinator.CreateScheduledRunAsync()));

        Assert.Single(fixture.Matcher.Calls);
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
    }

    [Fact]
    public async Task CreateScheduledRun_CapacityPrecheckDoesNotCallMatcher()
    {
        using JiraAuthoringTestFixture fixture = new(maxActiveAuthoringRuns: 1);
        await fixture.ActivateAsync();
        DateTimeOffset revision = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord first = await fixture.SeedAsync("FHIR-1", revision);
        await fixture.SeedAsync("FHIR-2", revision);
        JiraAuthoringRunCreation existing = await fixture.Coordinator.CreateExplicitRunAsync(
            [first], databaseOnly: true);
        fixture.Options.Value.LabelsToInclude = ["cohort"];

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => fixture.Coordinator.CreateScheduledRunAsync());

        Assert.Equal(AuthoringConflictCode.ActiveRunCapacityReached, conflict.Code);
        Assert.Equal([existing.Run.Id], conflict.RelatedRunIds);
        Assert.Empty(fixture.Matcher.Calls);
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM authoring_runs"));
    }

    [Fact]
    public async Task CreateScheduledRun_WorkflowPrecheckPrecedesCapacityAndDoesNotCallMatcher()
    {
        using JiraAuthoringTestFixture fixture = new(
            maxActiveAuthoringRuns: 1,
            snapshotWorkflowGuard: new RejectingSnapshotWorkflowGuard());
        await fixture.ActivateAsync();
        JiraProcessingSourceTicketRecord first = await fixture.SeedAsync(
            "FHIR-1", new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero));
        await fixture.Coordinator.CreateExplicitRunAsync([first], databaseOnly: true);
        fixture.Options.Value.LabelsToInclude = ["cohort"];

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => fixture.Coordinator.CreateScheduledRunAsync(databaseOnly: false));

        Assert.Equal(AuthoringConflictCode.CanonicalUnpublishedRestriction, conflict.Code);
        Assert.Empty(fixture.Matcher.Calls);
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM authoring_runs"));
    }

    [Fact]
    public async Task CreateScheduledRun_SelectionCancellationCreatesNoRunOrAttempt()
    {
        using JiraAuthoringTestFixture fixture = new();
        using CancellationTokenSource cancellation = new();
        await SeedCollisionCandidatesAsync(fixture, 1);
        fixture.Matcher.Enqueue((_, _, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>([]);
        });

        OperationCanceledException actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Coordinator.CreateScheduledRunAsync(ct: cancellation.Token));

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Single(fixture.Matcher.Calls);
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
    }

    [Fact]
    public async Task ReconcileRun_LabelConfigurationChangeDoesNotPruneFrozenMembership()
    {
        using JiraAuthoringTestFixture fixture = new();
        await SeedCollisionCandidatesAsync(fixture, 2);
        fixture.Matcher.Enqueue(["FHIR-2", "FHIR-1"]);
        JiraAuthoringRunCreation creation = Assert.IsType<JiraAuthoringRunCreation>(
            await fixture.Coordinator.CreateScheduledRunAsync());
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind, creation.Run.Id));
        IReadOnlyList<AuthoringRunItemRecord> before =
            await fixture.AuthoringStore.GetRunItemsAsync(creation.Run.Id);
        IReadOnlyList<AuthoringRunInputProvenanceRecord> provenance =
            await fixture.AuthoringStore.GetRunInputProvenanceAsync(creation.Run.Id);
        fixture.Options.Value.LabelsToInclude = ["different-cohort"];
        fixture.Options.Value.LabelsToExclude = ["cohort"];

        AuthoringRunReconciliationResult result = await fixture.Coordinator.ReconcileRunAsync(
            (await fixture.AuthoringStore.GetRunAsync(creation.Run.Id))!, CancellationToken.None);

        Assert.Equal(AuthoringRunReconciliationOutcome.Current, result.Outcome);
        Assert.Equal(before, await fixture.AuthoringStore.GetRunItemsAsync(creation.Run.Id));
        Assert.Equal(provenance, await fixture.AuthoringStore.GetRunInputProvenanceAsync(creation.Run.Id));
        Assert.Single(fixture.Matcher.Calls);
        Assert.Equal(1, fixture.Scalar("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(0, fixture.Scalar("SELECT COUNT(*) FROM authoring_run_attempts"));
    }

    [Fact]
    public async Task CreateRun_DeduplicatesRepeatedBusinessKeys()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        JiraProcessingSourceTicketRecord source = await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        JiraAuthoringRunCreation creation = await fixture.Coordinator.CreateRunAsync(
            [source, source],
            databaseOnly: true);

        Assert.Single(creation.Items);
    }

    [Fact]
    public async Task ScheduledRun_UsesMaximumCompleteProjectRefreshAndSharedRevision()
    {
        using JiraAuthoringTestFixture fixture = new();
        DateTimeOffset sourceRevision =
            new(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset firstRefresh = sourceRevision.AddHours(-2);
        DateTimeOffset secondRefresh = sourceRevision.AddHours(-1);
        await fixture.SeedAsync(
            "FHIR-1",
            sourceRevision,
            sourceRefresh: firstRefresh,
            contentRevision: 42);
        await fixture.SeedAsync(
            "FHIR-2",
            sourceRevision,
            sourceRefresh: secondRefresh,
            contentRevision: 42);
        await fixture.ActivateAsync();

        JiraAuthoringRunCreation creation =
            (await fixture.Coordinator.CreateScheduledRunAsync())!;

        AuthoringRunInputProvenanceRecord provenance = Assert.Single(
            await fixture.AuthoringStore.GetRunInputProvenanceAsync(
                creation.Run.Id));
        Assert.Equal("jira", provenance.Source);
        Assert.Equal(secondRefresh, provenance.LatestSuccessfulRefreshAt);
        Assert.Equal(42, provenance.ContentRevision);
        Assert.Equal(creation.Run.CreatedAt, provenance.CapturedAt);
    }

    [Fact]
    public async Task RunProvenanceSuppressesMixedOrIncompleteCoordinates()
    {
        using JiraAuthoringTestFixture mixedRevisionFixture = new();
        DateTimeOffset sourceRevision =
            new(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset firstRefresh = sourceRevision.AddHours(-2);
        DateTimeOffset secondRefresh = sourceRevision.AddHours(-1);
        JiraProcessingSourceTicketRecord first =
            await mixedRevisionFixture.SeedAsync(
                "FHIR-1",
                sourceRevision,
                sourceRefresh: firstRefresh,
                contentRevision: 41);
        JiraProcessingSourceTicketRecord second =
            await mixedRevisionFixture.SeedAsync(
                "FHIR-2",
                sourceRevision,
                sourceRefresh: secondRefresh,
                contentRevision: 42);
        await mixedRevisionFixture.ActivateAsync();

        JiraAuthoringRunCreation mixed =
            await mixedRevisionFixture.Coordinator.CreateExplicitRunAsync(
                [first, second],
                databaseOnly: false);
        AuthoringRunInputProvenanceRecord mixedProvenance = Assert.Single(
            await mixedRevisionFixture.AuthoringStore
                .GetRunInputProvenanceAsync(mixed.Run.Id));
        Assert.Equal(
            secondRefresh,
            mixedProvenance.LatestSuccessfulRefreshAt);
        Assert.Null(mixedProvenance.ContentRevision);

        using JiraAuthoringTestFixture missingRefreshFixture = new();
        JiraProcessingSourceTicketRecord known =
            await missingRefreshFixture.SeedAsync(
                "FHIR-1",
                sourceRevision,
                sourceRefresh: firstRefresh,
                contentRevision: 42);
        JiraProcessingSourceTicketRecord unknown =
            await missingRefreshFixture.SeedAsync(
                "FHIR-2",
                sourceRevision,
                sourceRefresh: null,
                contentRevision: 42);
        await missingRefreshFixture.ActivateAsync();

        JiraAuthoringRunCreation incomplete =
            await missingRefreshFixture.Coordinator.CreateExplicitRunAsync(
                [known, unknown],
                databaseOnly: false);
        AuthoringRunInputProvenanceRecord incompleteProvenance =
            Assert.Single(
                await missingRefreshFixture.AuthoringStore
                    .GetRunInputProvenanceAsync(incomplete.Run.Id));
        Assert.Null(incompleteProvenance.LatestSuccessfulRefreshAt);
        Assert.Equal(42, incompleteProvenance.ContentRevision);
    }

    [Fact]
    public async Task OneItemRunAlwaysBindsConservativeJiraProvenance()
    {
        using JiraAuthoringTestFixture fixture = new();
        JiraProcessingSourceTicketRecord source = await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero));
        await fixture.ActivateAsync();

        JiraAuthoringRunCreation creation =
            await fixture.Coordinator.CreateOneItemRunAsync(source);

        AuthoringRunInputProvenanceRecord provenance = Assert.Single(
            await fixture.AuthoringStore.GetRunInputProvenanceAsync(
                creation.Run.Id));
        Assert.Equal("jira", provenance.Source);
        Assert.Null(provenance.LatestSuccessfulRefreshAt);
        Assert.Null(provenance.ContentRevision);
    }

    [Fact]
    public async Task ExactReplayPreservesPreviouslyFrozenProvenance()
    {
        using JiraAuthoringTestFixture fixture = new();
        DateTimeOffset ticketRevision =
            new(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        DateTimeOffset firstRefresh = ticketRevision.AddHours(-2);
        JiraProcessingSourceTicketRecord firstSource = await fixture.SeedAsync(
            "FHIR-1",
            ticketRevision,
            sourceRefresh: firstRefresh,
            contentRevision: 41);
        await fixture.ActivateAsync();
        JiraAuthoringRunCreation first =
            await fixture.Coordinator.CreateExplicitRunAsync(
                [firstSource],
                databaseOnly: false);

        JiraProcessingSourceTicketRecord refreshedSource =
            await fixture.SeedAsync(
                "FHIR-1",
                ticketRevision,
                sourceRefresh: firstRefresh.AddHours(1),
                contentRevision: 42);
        JiraAuthoringRunCreation replay =
            await fixture.Coordinator.CreateExplicitRunAsync(
                [refreshedSource],
                databaseOnly: false);

        Assert.Equal(first.Run.Id, replay.Run.Id);
        AuthoringRunInputProvenanceRecord provenance = Assert.Single(
            await fixture.AuthoringStore.GetRunInputProvenanceAsync(
                replay.Run.Id));
        Assert.Equal(firstRefresh, provenance.LatestSuccessfulRefreshAt);
        Assert.Equal(41, provenance.ContentRevision);
    }

    [Fact]
    public async Task CreateExplicitRun_RejectsSecondActiveRunAtCapacity()
    {
        using JiraAuthoringTestFixture fixture = new(maxActiveAuthoringRuns: 1);
        await fixture.ActivateAsync();
        DateTimeOffset revision = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord first =
            await fixture.SeedAsync("FHIR-1", revision);
        JiraProcessingSourceTicketRecord second =
            await fixture.SeedAsync("FHIR-2", revision);
        JiraAuthoringRunCreation active =
            await fixture.Coordinator.CreateExplicitRunAsync([first], databaseOnly: false);

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => fixture.Coordinator.CreateExplicitRunAsync(
                    [second],
                    databaseOnly: false));

        Assert.Equal(AuthoringConflictCode.ActiveRunCapacityReached, conflict.Code);
        Assert.Contains(active.Run.Id, conflict.Message, StringComparison.Ordinal);
        Assert.Equal([active.Run.Id], conflict.RelatedRunIds);
        Assert.Equal(
            active.Run.Id,
            (await fixture.AuthoringStore.GetOldestQueuedRunAsync(
                fixture.Coordinator.ProcessorKind))!.Id);
    }

    [Fact]
    public async Task CreateExplicitRun_RevisionConflictReportsRunIds()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        DateTimeOffset revision =
            new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord first =
            await fixture.SeedAsync("FHIR-1", revision);
        JiraProcessingSourceTicketRecord second =
            await fixture.SeedAsync("FHIR-2", revision);
        JiraProcessingSourceTicketRecord third =
            await fixture.SeedAsync("FHIR-3", revision);
        JiraAuthoringRunCreation existing =
            await fixture.Coordinator.CreateExplicitRunAsync(
                [first, second],
                databaseOnly: false);

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => fixture.Coordinator.CreateExplicitRunAsync(
                    [first, third],
                    databaseOnly: false));

        Assert.Equal(
            AuthoringConflictCode.RevisionAlreadyScheduled,
            conflict.Code);
        Assert.Equal([existing.Run.Id], conflict.RelatedRunIds);
    }

    [Fact]
    public async Task CreateExplicitRun_AfterTerminalRun_PersistsRequestForRestart()
    {
        using JiraAuthoringTestFixture fixture = new(maxActiveAuthoringRuns: 1);
        await fixture.ActivateAsync();
        DateTimeOffset revision = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord completedTicket =
            await fixture.SeedAsync("FHIR-1", revision);
        JiraAuthoringRunCreation completed =
            await fixture.Coordinator.CreateExplicitRunAsync(
                [completedTicket],
                databaseOnly: true);
        await CompleteDatabaseOnlyRunAsync(fixture, completed);

        JiraProcessingSourceTicketRecord supersededTicket =
            await fixture.SeedAsync("FHIR-2", revision);
        JiraAuthoringRunCreation superseded =
            await fixture.Coordinator.CreateExplicitRunAsync(
                [supersededTicket],
                databaseOnly: false);
        await fixture.AuthoringStore.SupersedeRunAsync(
            superseded.Run.Id,
            "Test terminal transition.");

        JiraProcessingSourceTicketRecord restartedTicket =
            await fixture.SeedAsync("FHIR-3", revision);
        JiraAuthoringRunCreation accepted =
            await fixture.Coordinator.CreateExplicitRunAsync(
                [restartedTicket],
                databaseOnly: false);
        AuthoringRunStore restartedStore = new(fixture.SourceStoreConnection);
        AuthoringRunRecord restored =
            (await restartedStore.GetRunAsync(accepted.Run.Id))!;
        JiraAuthoringRunRequestSnapshot request =
            JsonSerializer.Deserialize<JiraAuthoringRunRequestSnapshot>(
                restored.RequestJson!,
                JsonSerializerOptions.Web)!;

        Assert.Equal(accepted.Run.Id, restored.Id);
        Assert.Equal(["FHIR-3"], request.TicketKeys);
        Assert.False(request.DatabaseOnly);
    }

    [Fact]
    public async Task CreateScheduledRun_ConcurrentAdmissionCreatesOneRevisionRun()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        await fixture.SeedAsync(
            "FHIR-1",
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

        JiraAuthoringRunCreation?[] results = await Task.WhenAll(
            fixture.Coordinator.CreateScheduledRunAsync(),
            fixture.Coordinator.CreateScheduledRunAsync());

        Assert.Single(results, result => result is not null);
        Assert.Null(results.SingleOrDefault(result => result is null));
        using Microsoft.Data.Sqlite.SqliteConnection connection =
            fixture.SourceStoreConnection();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM authoring_run_items";
        Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
    }

    [Fact]
    public async Task CreateRun_SupersedesStaleQueuedRunBeforeNewerRevision()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        DateTimeOffset revision = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", revision);
        JiraAuthoringRunCreation first =
            (await fixture.Coordinator.CreateScheduledRunAsync(databaseOnly: true))!;

        await fixture.SeedAsync("FHIR-1", revision.AddDays(1), title: "Second");
        JiraAuthoringRunCreation second =
            (await fixture.Coordinator.CreateScheduledRunAsync(databaseOnly: true))!;
        Assert.Equal(AuthoringStatusValues.Runs.Queued, second.Run.Status);
        await CompleteDatabaseOnlyRunAsync(fixture, first);

        await fixture.SeedAsync("FHIR-1", revision.AddDays(2), title: "Third");
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            second.Run.Id));
        AuthoringRunReconciliationResult reconciliation =
            await fixture.Coordinator.ReconcileRunAsync(
                (await fixture.AuthoringStore.GetRunAsync(second.Run.Id))!,
                CancellationToken.None);
        AuthoringRunRecord third =
            (await fixture.AuthoringStore.GetOldestQueuedRunAsync(
                fixture.Coordinator.ProcessorKind))!;

        Assert.Equal(
            AuthoringRunReconciliationOutcome.Superseded,
            reconciliation.Outcome);
        Assert.Equal(
            AuthoringStatusValues.Runs.Superseded,
            (await fixture.AuthoringStore.GetRunAsync(second.Run.Id))!.Status);
        Assert.Equal(
            AuthoringStatusValues.Runs.Queued,
            third.Status);
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            third.Id));
        JiraAuthoringWorkItemStore queue = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem current = Assert.Single(
            await queue.GetPendingAsync(third.Id, 10, CancellationToken.None));
        Assert.Equal(revision.AddDays(2).ToString("O"), current.RunItem.ExpectedSourceRevision);
    }

    [Fact]
    public async Task StaleBatchItemDoesNotStrandCurrentSibling()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        DateTimeOffset revision = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", revision);
        await fixture.SeedAsync("FHIR-2", revision);
        JiraAuthoringRunCreation batch =
            (await fixture.Coordinator.CreateScheduledRunAsync(databaseOnly: true))!;
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            batch.Run.Id));

        await fixture.SeedAsync("FHIR-1", revision.AddDays(1), title: "Updated");
        await fixture.Coordinator.ReconcileRunAsync(
            (await fixture.AuthoringStore.GetRunAsync(batch.Run.Id))!,
            CancellationToken.None);
        JiraAuthoringWorkItemStore queue = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem current = Assert.Single(
            await queue.GetPendingAsync(batch.Run.Id, 10, CancellationToken.None));
        AuthoringRunItemRecord[] batchItems =
            (await fixture.AuthoringStore.GetRunItemsAsync(batch.Run.Id)).ToArray();

        Assert.Equal("FHIR-2", current.SourceTicket.Key);
        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            batchItems.Single(item => item.BusinessKey == "FHIR-1").Status);
        Assert.Equal(
            AuthoringStatusValues.Items.Pending,
            batchItems.Single(item => item.BusinessKey == "FHIR-2").Status);

        JiraAuthoringRunCreation replacement =
            (await fixture.Coordinator.CreateScheduledRunAsync(databaseOnly: true))!;
        Assert.Equal("FHIR-1", Assert.Single(replacement.Items).BusinessKey);
        Assert.Equal(AuthoringStatusValues.Runs.Queued, replacement.Run.Status);
    }

    [Fact]
    public async Task StalePublicationReconciliationItemsAreLeftUntouched()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        DateTimeOffset frozenRevision =
            new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", frozenRevision);
        JiraAuthoringRunCreation run =
            Assert.IsType<JiraAuthoringRunCreation>(
                await fixture.Coordinator.CreateScheduledRunAsync());
        await using (Microsoft.Data.Sqlite.SqliteConnection connection =
                     fixture.SourceStoreConnection())
        {
            await using Microsoft.Data.Sqlite.SqliteCommand command =
                connection.CreateCommand();
            command.CommandText =
                """
                UPDATE authoring_runs
                SET Purpose = @purpose
                WHERE Id = @runId
                """;
            command.Parameters.AddWithValue(
                "@purpose",
                AuthoringRunPurposeValues.PublicationReconciliation);
            command.Parameters.AddWithValue("@runId", run.Run.Id);
            await command.ExecuteNonQueryAsync();
        }
        await fixture.SeedAsync(
            "FHIR-1",
            frozenRevision.AddDays(1),
            title: "Newer local source row");

        Assert.False(
            await fixture.Coordinator.SupersedeStaleItemsAsync(run.Run.Id));

        AuthoringRunItemRecord item = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(run.Run.Id));
        Assert.Equal(AuthoringStatusValues.Items.Pending, item.Status);
        Assert.Null(item.Error);
        Assert.Equal(
            AuthoringStatusValues.Runs.Queued,
            Assert.IsType<AuthoringRunRecord>(
                await fixture.AuthoringStore.GetRunAsync(run.Run.Id)).Status);
    }

    [Fact]
    public async Task StaleInitialRevalidationIsReplacedWithCurrentRevision()
    {
        using JiraAuthoringTestFixture fixture = new();
        DateTimeOffset firstRevision =
            new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", firstRevision);
        await fixture.SeedAsync("FHIR-2", firstRevision);
        AuthoringProcessorModeRecord active =
            await new AuthoringCutoverCoordinator(
                fixture.SourceStoreConnection).ActivateAsync(
                new AuthoringCutoverRequest(
                    "jira-fhir",
                    fixture.DatabasePath,
                    $"{fixture.DatabasePath}.pre-cutover"),
                new StaticCutoverParticipant(
                [
                    new("FHIR-1", "fhir", firstRevision.ToString("O")),
                    new("FHIR-2", "fhir", firstRevision.ToString("O")),
                ]));
        AuthoringRunItemRecord completed =
            (await fixture.AuthoringStore.GetRunItemsAsync(
                active.RevalidationRunId!))
            .Single(item => item.BusinessKey == "FHIR-1");
        AuthoringOperationClaim claim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    active.RevalidationRunId!,
                    completed.Id));
        AuthoringReceiptAcceptance receipt =
            await fixture.AuthoringStore.AcceptResultAsync(
                new AuthoringResultSubmission(
                    active.RevalidationRunId!,
                    completed.Id,
                    claim.OperationId,
                    completed.ExpectedSourceRevision,
                    AuthoringResultHasher.HashNormalizedUtf8("first")),
                claim.OperationToken);
        await fixture.AuthoringStore.MarkItemCompleteAsync(
            completed.Id,
            receipt.Receipt.ReceiptId);

        DateTimeOffset secondRevision = firstRevision.AddDays(1);
        await fixture.SeedAsync(
            "FHIR-2",
            secondRevision,
            title: "Updated");

        Assert.True(await fixture.Coordinator.SupersedeStaleItemsAsync(
            active.RevalidationRunId!));
        AuthoringProcessorModeRecord refreshed =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        Assert.NotEqual(
            active.RevalidationRunId,
            refreshed.RevalidationRunId);
        AuthoringRunItemRecord replacement = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(
                refreshed.RevalidationRunId!));
        Assert.Equal("FHIR-2", replacement.BusinessKey);
        Assert.Equal(secondRevision.ToString("O"), replacement.ExpectedSourceRevision);
        IReadOnlyList<AuthoringRunItemRecord> predecessor =
            await fixture.AuthoringStore.GetRunItemsAsync(
                active.RevalidationRunId!);
        Assert.Equal(
            AuthoringStatusValues.Items.Complete,
            predecessor.Single(item => item.BusinessKey == "FHIR-1").Status);
        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            predecessor.Single(item => item.BusinessKey == "FHIR-2").Status);

        DateTimeOffset thirdRevision = firstRevision.AddDays(2);
        await fixture.SeedAsync(
            "FHIR-1",
            thirdRevision,
            title: "Updated again");
        await using (Microsoft.Data.Sqlite.SqliteConnection connection =
                     fixture.SourceStoreConnection())
        {
            AuthoringConflictException staleCorpus =
                await Assert.ThrowsAsync<AuthoringConflictException>(() =>
                    JiraProcessingSourceTicketStore
                        .EnsureRunSourceRevisionsCurrentAsync(
                            connection,
                            refreshed.RevalidationRunId!,
                            CancellationToken.None));
            Assert.Equal(
                AuthoringConflictCode.SourceRevisionMismatch,
                staleCorpus.Code);
        }

        Assert.True(await fixture.Coordinator.SupersedeStaleItemsAsync(
            refreshed.RevalidationRunId!));
        AuthoringProcessorModeRecord secondRefresh =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        IReadOnlyList<AuthoringRunItemRecord> secondReplacement =
            await fixture.AuthoringStore.GetRunItemsAsync(
                secondRefresh.RevalidationRunId!);
        Assert.Equal(
            ["FHIR-1", "FHIR-2"],
            secondReplacement
                .Select(item => item.BusinessKey)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            thirdRevision.ToString("O"),
            secondReplacement
                .Single(item => item.BusinessKey == "FHIR-1")
                .ExpectedSourceRevision);
    }

    [Fact]
    public async Task StaleInitialRevalidationReplacementDerivesNewProvenance()
    {
        using JiraAuthoringTestFixture fixture = new();
        DateTimeOffset firstRevision =
            new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset firstRefresh = firstRevision.AddHours(-1);
        await fixture.SeedAsync(
            "FHIR-1",
            firstRevision,
            sourceRefresh: firstRefresh,
            contentRevision: 10);
        AuthoringProcessorModeRecord active =
            await new AuthoringCutoverCoordinator(
                fixture.SourceStoreConnection).ActivateAsync(
                new AuthoringCutoverRequest(
                    "jira-fhir",
                    fixture.DatabasePath,
                    $"{fixture.DatabasePath}.pre-cutover"),
                new StaticCutoverParticipant(
                    [new("FHIR-1", "fhir", firstRevision.ToString("O"))],
                    [new("jira", firstRefresh, 10)]));

        DateTimeOffset secondRevision = firstRevision.AddDays(1);
        DateTimeOffset secondRefresh = secondRevision.AddHours(-1);
        await fixture.SeedAsync(
            "FHIR-1",
            secondRevision,
            title: "Updated",
            sourceRefresh: secondRefresh,
            contentRevision: 11);

        Assert.True(await fixture.Coordinator.SupersedeStaleItemsAsync(
            active.RevalidationRunId!));
        AuthoringProcessorModeRecord refreshedMode =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        AuthoringRunInputProvenanceRecord original = Assert.Single(
            await fixture.AuthoringStore.GetRunInputProvenanceAsync(
                active.RevalidationRunId!));
        AuthoringRunInputProvenanceRecord replacement = Assert.Single(
            await fixture.AuthoringStore.GetRunInputProvenanceAsync(
                refreshedMode.RevalidationRunId!));

        Assert.Equal(firstRefresh, original.LatestSuccessfulRefreshAt);
        Assert.Equal(10, original.ContentRevision);
        Assert.Equal(secondRefresh, replacement.LatestSuccessfulRefreshAt);
        Assert.Equal(11, replacement.ContentRevision);
    }

    [Fact]
    public async Task InitialRevalidation_UnchangedRevisionPreservesAttemptBudgetAcrossReplacement()
    {
        using JiraAuthoringTestFixture fixture = new();
        DateTimeOffset firstRevision =
            new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", firstRevision);
        await fixture.SeedAsync("FHIR-2", firstRevision);
        AuthoringProcessorModeRecord active =
            await new AuthoringCutoverCoordinator(
                fixture.SourceStoreConnection).ActivateAsync(
                new AuthoringCutoverRequest(
                    "jira-fhir",
                    fixture.DatabasePath,
                    $"{fixture.DatabasePath}.pre-cutover"),
                new StaticCutoverParticipant(
                [
                    new("FHIR-1", "fhir", firstRevision.ToString("O")),
                    new("FHIR-2", "fhir", firstRevision.ToString("O")),
                ]));
        AuthoringRunItemRecord firstItem =
            (await fixture.AuthoringStore.GetRunItemsAsync(
                active.RevalidationRunId!))
            .Single(item => item.BusinessKey == "FHIR-1");
        DateTimeOffset now =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringOperationClaim firstClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    active.RevalidationRunId!,
                    firstItem.Id,
                    now));
        await fixture.AuthoringStore.MarkClaimErrorAsync(
            firstItem.Id,
            firstClaim.OperationId,
            "first failure",
            now.AddSeconds(1));
        await fixture.AuthoringStore.ReconcileErroredItemsAsync(
            active.RevalidationRunId!,
            now.AddMinutes(2));
        AuthoringOperationClaim secondClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    active.RevalidationRunId!,
                    firstItem.Id,
                    now.AddMinutes(2)));
        await fixture.AuthoringStore.MarkClaimErrorAsync(
            firstItem.Id,
            secondClaim.OperationId,
            "second failure",
            now.AddMinutes(2).AddSeconds(1));

        await fixture.SeedAsync(
            "FHIR-2",
            firstRevision.AddDays(1),
            title: "Updated B");
        Assert.True(await fixture.Coordinator.SupersedeStaleItemsAsync(
            active.RevalidationRunId!));
        AuthoringProcessorModeRecord firstReplacementMode =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        AuthoringRunItemRecord unchangedReplacement =
            (await fixture.AuthoringStore.GetRunItemsAsync(
                firstReplacementMode.RevalidationRunId!))
            .Single(item => item.BusinessKey == "FHIR-1");

        Assert.Equal(2, unchangedReplacement.AttemptCount);
        AuthoringOperationClaim thirdClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    unchangedReplacement.RunId,
                    unchangedReplacement.Id,
                    now.AddMinutes(3)));
        Assert.Equal(3, thirdClaim.AttemptNumber);

        DateTimeOffset newRevision = firstRevision.AddDays(2);
        await fixture.SeedAsync(
            "FHIR-1",
            newRevision,
            title: "Updated A");
        Assert.True(await fixture.Coordinator.SupersedeStaleItemsAsync(
            unchangedReplacement.RunId));
        AuthoringConflictException exhaustedCoordinate =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => fixture.AuthoringStore.ClaimItemAsync(
                    unchangedReplacement.RunId,
                    unchangedReplacement.Id,
                    now.AddMinutes(4)));
        Assert.Equal(
            AuthoringConflictCode.RunNotActive,
            exhaustedCoordinate.Code);

        AuthoringProcessorModeRecord secondReplacementMode =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        AuthoringRunItemRecord changedRevision =
            (await fixture.AuthoringStore.GetRunItemsAsync(
                secondReplacementMode.RevalidationRunId!))
            .Single(item => item.BusinessKey == "FHIR-1");
        AuthoringOperationClaim changedRevisionClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    changedRevision.RunId,
                    changedRevision.Id,
                    now.AddMinutes(5)));

        Assert.Equal(newRevision.ToString("O"), changedRevision.ExpectedSourceRevision);
        Assert.Equal(0, changedRevision.AttemptCount);
        Assert.Equal(1, changedRevisionClaim.AttemptNumber);
    }

    [Fact]
    public async Task StaleInitialRevalidationWaitsForAcceptedPostPersistenceWork()
    {
        using JiraAuthoringTestFixture fixture = new();
        DateTimeOffset firstRevision =
            new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await fixture.SeedAsync("FHIR-1", firstRevision);
        await fixture.SeedAsync("FHIR-2", firstRevision);
        AuthoringProcessorModeRecord active =
            await new AuthoringCutoverCoordinator(
                fixture.SourceStoreConnection).ActivateAsync(
                new AuthoringCutoverRequest(
                    "jira-fhir",
                    fixture.DatabasePath,
                    $"{fixture.DatabasePath}.pre-cutover"),
                new StaticCutoverParticipant(
                [
                    new("FHIR-1", "fhir", firstRevision.ToString("O")),
                    new("FHIR-2", "fhir", firstRevision.ToString("O")),
                ]));
        IReadOnlyList<AuthoringRunItemRecord> initialItems =
            await fixture.AuthoringStore.GetRunItemsAsync(
                active.RevalidationRunId!);
        AuthoringRunItemRecord persisted =
            initialItems.Single(item => item.BusinessKey == "FHIR-2");
        AuthoringOperationClaim claim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    active.RevalidationRunId!,
                    persisted.Id));
        AuthoringReceiptAcceptance receipt =
            await fixture.AuthoringStore.AcceptResultAsync(
                new AuthoringResultSubmission(
                    active.RevalidationRunId!,
                    persisted.Id,
                    claim.OperationId,
                    persisted.ExpectedSourceRevision,
                    AuthoringResultHasher.HashNormalizedUtf8("persisted")),
                claim.OperationToken);
        await fixture.SeedAsync(
            "FHIR-1",
            firstRevision.AddDays(1),
            title: "Updated");

        Assert.False(await fixture.Coordinator.SupersedeStaleItemsAsync(
            active.RevalidationRunId!));
        JiraAuthoringWorkItemStore workItems = new(
            fixture.AuthoringStore,
            fixture.SourceStore);
        JiraAuthoringWorkItem pending = Assert.Single(
            await workItems.GetPendingAsync(
                active.RevalidationRunId!,
                10,
                CancellationToken.None));
        Assert.Equal("FHIR-2", pending.RunItem.BusinessKey);
        Assert.Equal(
            AuthoringStatusValues.Items.Persisted,
            pending.RunItem.Status);

        await fixture.AuthoringStore.MarkItemCompleteAsync(
            persisted.Id,
            receipt.Receipt.ReceiptId);
        Assert.True(await fixture.Coordinator.SupersedeStaleItemsAsync(
            active.RevalidationRunId!));
        AuthoringProcessorModeRecord refreshed =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        Assert.Equal(
            "FHIR-1",
            Assert.Single(
                await fixture.AuthoringStore.GetRunItemsAsync(
                    refreshed.RevalidationRunId!)).BusinessKey);
        Assert.Equal(
            AuthoringStatusValues.Items.Complete,
            (await fixture.AuthoringStore.GetRunItemsAsync(
                active.RevalidationRunId!))
            .Single(item => item.BusinessKey == "FHIR-2")
            .Status);
    }

    private static async Task SeedCollisionCandidatesAsync(JiraAuthoringTestFixture fixture, int count)
    {
        await fixture.ActivateAsync();
        fixture.Options.Value.LabelsToInclude = ["cohort"];
        DateTimeOffset revision = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        for (int index = 1; index <= count; index++)
        {
            await fixture.SeedAsync($"FHIR-{index}", revision.AddMinutes(index));
        }
    }

    private static void QueueCollidingAdmissions(JiraAuthoringTestFixture fixture, int count)
    {
        for (int index = 0; index < count; index++)
        {
            fixture.Matcher.Enqueue(async (keys, _, ct) =>
            {
                JiraProcessingSourceTicketRecord ticket = Assert.IsType<JiraProcessingSourceTicketRecord>(
                    await fixture.SourceStore.GetByKeyAsync(keys[0], "fhir", ct));
                await fixture.Coordinator.CreateExplicitRunAsync([ticket], databaseOnly: true, ct);
                return [keys[0]];
            });
        }
    }

    private sealed class RejectingSnapshotWorkflowGuard : IAuthoringSnapshotWorkflowGuard
    {
        public Task EnsureSnapshotWorkflowAllowedAsync(
            AuthoringSnapshotWorkflowIntent intent,
            CancellationToken ct = default)
            => intent.DatabaseOnly
                ? Task.CompletedTask
                : Task.FromException(AuthoringConflictException.ForCanonicalUnpublishedRestriction());
    }

    private static async Task CompleteDatabaseOnlyRunAsync(
        JiraAuthoringTestFixture fixture,
        JiraAuthoringRunCreation creation)
    {
        Assert.True(await fixture.AuthoringStore.TryAcquireMutationFenceAsync(
            fixture.Coordinator.ProcessorKind,
            creation.Run.Id));
        AuthoringRunItemRecord item = Assert.Single(creation.Items);
        AuthoringOperationClaim claim = (await fixture.AuthoringStore.ClaimItemAsync(
            creation.Run.Id,
            item.Id))!;
        AuthoringReceiptAcceptance receipt = await fixture.AuthoringStore.AcceptResultAsync(
            new AuthoringResultSubmission(
                creation.Run.Id,
                item.Id,
                claim.OperationId,
                item.ExpectedSourceRevision,
                AuthoringResultHasher.HashNormalizedUtf8("payload")),
            claim.OperationToken);
        await fixture.AuthoringStore.MarkItemCompleteAsync(item.Id, receipt.Receipt.ReceiptId);
        await fixture.AuthoringStore.MarkRunFinalizingAsync(creation.Run.Id);
        await fixture.AuthoringStore.CompleteRunAsync(creation.Run.Id, snapshotId: null);
    }

    private sealed class StaticCutoverParticipant(
        IReadOnlyList<AuthoringRunItemDefinition> items,
        IReadOnlyList<AuthoringRunInputProvenanceDefinition>? inputProvenance = null)
        : IAuthoringCutoverParticipant
    {
        public Task<AuthoringCutoverPreparation> PrepareCutoverAsync(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            CancellationToken ct) =>
            Task.FromResult(
                new AuthoringCutoverPreparation(
                    items,
                    inputProvenance));
    }
}

internal sealed class JiraAuthoringTestFixture : IDisposable
{
    private readonly string _directory;

    public JiraAuthoringTestFixture(
        int maxActiveAuthoringRuns = int.MaxValue,
        IAuthoringSnapshotWorkflowGuard? snapshotWorkflowGuard = null)
    {
        _directory = Path.Combine(Path.GetTempPath(), $"fhir-augury-jira-authoring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "jira-processing.db");
        SourceStore = new JiraProcessingSourceTicketStore(
            DatabasePath,
            new ResolvedJiraProcessingFilters
            {
                TicketStatuses = ["Triaged"],
                SourceTicketShape = "fhir",
            });
        JiraProcessingDatabase database = new(
            DatabasePath,
            NullLogger<JiraProcessingDatabase>.Instance);
        database.Initialize();
        AuthoringStore = new AuthoringRunStore(database);
        Options = Microsoft.Extensions.Options.Options.Create(new JiraProcessingOptions
        {
            AgentCliCommand = "agent {ticketKey}",
            AuthoringAgentCliCommand = "agent {ticketKey}",
            JiraSourceAddress = "http://source",
            SourceTicketShape = "fhir",
            TicketStatusesToProcess = ["Triaged"],
        });
        Selector = new JiraConfiguredTicketSelector(SourceStore, Matcher);
        Coordinator = new JiraAuthoringRunCoordinator(
            AuthoringStore,
            SourceStore,
            Selector,
            new JiraProcessingFilterResolver(),
            Options,
            Microsoft.Extensions.Options.Options.Create(
                new ProcessingServiceOptions
                {
                    MaxActiveAuthoringRuns = maxActiveAuthoringRuns,
                }),
            snapshotWorkflowGuard);
    }

    public string DatabasePath { get; }
    public JiraProcessingSourceTicketStore SourceStore { get; }
    public AuthoringRunStore AuthoringStore { get; }
    public IOptions<JiraProcessingOptions> Options { get; }
    public TestJiraTicketLabelMatcher Matcher { get; } = new();
    public JiraConfiguredTicketSelector Selector { get; }
    public JiraAuthoringRunCoordinator Coordinator { get; }

    public Microsoft.Data.Sqlite.SqliteConnection SourceStoreConnection()
    {
        Microsoft.Data.Sqlite.SqliteConnection connection = new(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
        connection.Open();
        return connection;
    }

    public int Scalar(string sql)
    {
        using SqliteConnection connection = SourceStoreConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public async Task ActivateAsync()
    {
        await AuthoringStore.EnsureProcessorModeAsync(Coordinator.ProcessorKind);
        AuthoringProcessorModeRecord mode =
            await AuthoringStore.GetProcessorModeAsync(Coordinator.ProcessorKind);
        if (mode.Mode == AuthoringStatusValues.ProcessorModes.Legacy)
        {
            await AuthoringStore.TransitionProcessorModeAsync(
                Coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.Legacy,
                AuthoringStatusValues.ProcessorModes.CuttingOver);
            await AuthoringStore.TransitionProcessorModeAsync(
                Coordinator.ProcessorKind,
                AuthoringStatusValues.ProcessorModes.CuttingOver,
                AuthoringStatusValues.ProcessorModes.RunBacked);
        }
    }

    public Task<JiraProcessingSourceTicketRecord> SeedAsync(
        string key,
        DateTimeOffset revision,
        string title = "Title",
        DateTimeOffset? sourceRefresh = null,
        long? contentRevision = null)
        => SourceStore.UpsertAsync(
            new JiraIssueSummaryEntry
            {
                Key = key,
                ProjectKey = "FHIR",
                Title = title,
                Type = "Change Request",
                Status = "Triaged",
                WorkGroup = "FHIR-I",
                Specification = "FHIR Core",
                UpdatedAt = revision,
            },
            "fhir",
            resetProcessingStatus: false,
            sourceRefresh,
            contentRevision,
            CancellationToken.None);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
