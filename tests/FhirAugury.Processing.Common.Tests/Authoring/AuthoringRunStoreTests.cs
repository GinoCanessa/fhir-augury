using System.Text.Json;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processing.Common.Tests.Authoring;

public sealed class AuthoringRunStoreTests
{
    [Fact]
    public async Task CreateRun_FreezesMembershipAndRequiresRunBackedMode()
    {
        using AuthoringTestDatabase database = new();

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Store.CreateRunAsync(
                "test",
                [new("FHIR-1", "ticket", "revision-1")]));
        Assert.Equal(AuthoringConflictCode.AuthoringNotActivated, conflict.Code);

        await database.ActivateAsync();
        AuthoringRunRecord run = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-1", "ticket", "revision-1")]);
        AuthoringRunItemRecord item = Assert.Single(await database.Store.GetRunItemsAsync(run.Id));

        Assert.Equal(1, run.AuthoringEpoch);
        Assert.Equal(AuthoringRunPurposeValues.Authoring, run.Purpose);
        Assert.Null(run.SourceRunId);
        Assert.Equal(
            "'authoring'",
            database.Scalar<string>(
                """
                SELECT dflt_value
                FROM pragma_table_info('authoring_runs')
                WHERE name = 'Purpose'
                """));
        Assert.Equal("FHIR-1", item.BusinessKey);
        Assert.Equal("revision-1", item.ExpectedSourceRevision);
        Assert.Equal(AuthoringStatusValues.Items.Pending, item.Status);
    }

    [Fact]
    public async Task CreateRun_PersistsInputProvenanceInRunTransaction()
    {
        using AuthoringTestDatabase database = new();
        await database.ActivateAsync();
        DateTimeOffset createdAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset refreshedAt = createdAt.AddHours(-3);

        AuthoringRunRecord run = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-1", "ticket", "revision-1")],
            runId: "provenance-run",
            now: createdAt,
            inputProvenance:
            [
                new("jira", refreshedAt, 42),
            ]);

        AuthoringRunInputProvenanceRecord provenance = Assert.Single(
            await database.Store.GetRunInputProvenanceAsync(run.Id));
        Assert.Equal(run.Id, provenance.RunId);
        Assert.Equal("jira", provenance.Source);
        Assert.Equal(refreshedAt, provenance.LatestSuccessfulRefreshAt);
        Assert.Equal(42, provenance.ContentRevision);
        Assert.Equal(run.CreatedAt, provenance.CapturedAt);
    }

    [Fact]
    public async Task CreateRun_InvalidProvenanceRollsBackRunAndItems()
    {
        using AuthoringTestDatabase database = new();
        await database.ActivateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.CreateRunAsync(
                "test",
                [new("FHIR-1", "ticket", "revision-1")],
                runId: "invalid-provenance-run",
                inputProvenance:
                [
                    new("jira", null, null),
                    new("JIRA", null, null),
                ]));

        Assert.Null(await database.Store.GetRunAsync(
            "invalid-provenance-run"));
        Assert.Empty(await database.Store.GetRunItemsAsync(
            "invalid-provenance-run"));
        Assert.Empty(await database.Store.GetRunInputProvenanceAsync(
            "invalid-provenance-run"));
    }

    [Fact]
    public async Task ListOperatorRunsAsync_ExcludesNonOperatorRunsAndPrioritizesActive()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringRetryDelay = "00:02:00",
            AuthoringMaxAttempts = 3,
        });
        await database.ActivateAsync();
        DateTimeOffset createdAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringRunRecord active = await database.Store.CreateRunAsync(
            "test",
            [
                new("FHIR-1", "ticket", "revision-1"),
                new("FHIR-2", "ticket", "revision-2"),
                new("FHIR-3", "ticket", "revision-3"),
                new("FHIR-4", "ticket", "revision-4"),
            ],
            runId: "operator-active",
            now: createdAt,
            requestJson: "{}");
        AuthoringRunItemRecord[] activeItems =
            (await database.Store.GetRunItemsAsync(active.Id)).ToArray();
        database.Execute(
            """
            UPDATE authoring_run_items
            SET Status = @complete, CompletedAt = @completedAt
            WHERE Id = @completeId;
            UPDATE authoring_run_items
            SET Status = @error, AttemptCount = 1, CompletedAt = @completedAt,
                Error = 'retryable'
            WHERE Id = @errorId;
            UPDATE authoring_run_items
            SET Status = @superseded, CompletedAt = @completedAt,
                Error = 'operator skipped'
            WHERE Id = @supersededId;
            """,
            ("@complete", AuthoringStatusValues.Items.Complete),
            ("@error", AuthoringStatusValues.Items.Error),
            ("@superseded", AuthoringStatusValues.Items.Superseded),
            ("@completedAt", createdAt.AddMinutes(1).ToString("O")),
            ("@completeId", activeItems[0].Id),
            ("@errorId", activeItems[1].Id),
            ("@supersededId", activeItems[2].Id));

        AuthoringRunRecord terminal = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-5", "ticket", "revision-5")],
            runId: "operator-terminal",
            now: createdAt.AddHours(1),
            requestJson: "{}");
        database.Execute(
            """
            UPDATE authoring_runs
            SET Status = @status, CompletedAt = @completedAt
            WHERE Id = @runId
            """,
            ("@status", AuthoringStatusValues.Runs.Completed),
            ("@completedAt", createdAt.AddHours(1).ToString("O")),
            ("@runId", terminal.Id));

        AuthoringRunRecord legacy = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-6", "ticket", "revision-6")],
            runId: "legacy-null-marker",
            now: createdAt.AddHours(2));
        AuthoringRunRecord revalidation = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-7", "ticket", "revision-7")],
            runId: "initial-revalidation",
            now: createdAt.AddHours(3));
        AuthoringRunRecord maintenance =
            await database.Store.CreateMaintenanceRunAsync(
                "test",
                [
                    new AuthoringMaintenanceRunItem(
                        "FHIR-8",
                        "ticket",
                        "revision-8",
                        "receipt-8"),
                ],
                createdAt.AddHours(4));

        AuthoringOperatorRunList result =
            await database.Store.ListOperatorRunsAsync("test");

        Assert.False(result.Truncated);
        Assert.Equal(
            [maintenance.Id, active.Id, terminal.Id],
            result.Runs.Select(run => run.Run.Id));
        AuthoringOperatorRunSummary summary = result.Runs[1];
        Assert.Equal(1, summary.CompletedItems);
        Assert.Equal(1, summary.RetryableErrorItems);
        Assert.Equal(1, summary.SupersededItems);
        Assert.Equal(
            createdAt.AddMinutes(3),
            summary.NextAutomaticRetryAt);
        Assert.DoesNotContain(
            result.Runs,
            run => run.Run.Id is "legacy-null-marker" or "initial-revalidation");
        Assert.Equal(
            AuthoringRunPurposeValues.GroupingMaintenance,
            result.Runs[0].Run.Purpose);
        Assert.Null(legacy.RequestJson);
        Assert.Null(revalidation.RequestJson);
    }

    [Fact]
    public async Task ListOperatorRunsAsync_BoundsAndMarksTruncation()
    {
        using AuthoringTestDatabase database = new();
        await database.ActivateAsync();
        DateTimeOffset createdAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        for (int index = 0; index < 21; index++)
        {
            await database.Store.CreateRunAsync(
                "test",
                [new($"FHIR-{index}", "ticket", $"revision-{index}")],
                runId: $"run-{index:00}",
                now: createdAt.AddMinutes(index),
                requestJson: "{}");
        }
        database.Execute(
            """
            UPDATE authoring_runs
            SET Status = @status, CompletedAt = CreatedAt
            WHERE RequestJson IS NOT NULL
            """,
            ("@status", AuthoringStatusValues.Runs.Completed));

        AuthoringOperatorRunList defaultPage =
            await database.Store.ListOperatorRunsAsync("test");
        AuthoringOperatorRunList maximumPage =
            await database.Store.ListOperatorRunsAsync(
                "test",
                AuthoringRunStore.MaximumOperatorRunLimit);

        Assert.Equal(AuthoringRunStore.DefaultOperatorRunLimit, defaultPage.Runs.Count);
        Assert.True(defaultPage.Truncated);
        Assert.Equal("run-20", defaultPage.Runs[0].Run.Id);
        Assert.Equal(21, maximumPage.Runs.Count);
        Assert.False(maximumPage.Truncated);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => database.Store.ListOperatorRunsAsync("test", 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => database.Store.ListOperatorRunsAsync(
                "test",
                AuthoringRunStore.MaximumOperatorRunLimit + 1));
    }

    [Fact]
    public async Task ActiveRunCapacityConflict_ReportsActiveRunIds()
    {
        using AuthoringTestDatabase database = new();
        await database.ActivateAsync();
        AuthoringRunRecord active = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-1", "ticket", "revision-1")],
            requestJson: "{}");

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => database.Store.EnsureActiveRunCapacityAvailableAsync(
                    "test",
                    1));

        Assert.Equal(
            AuthoringConflictCode.ActiveRunCapacityReached,
            conflict.Code);
        Assert.Equal([active.Id], conflict.RelatedRunIds);
    }

    [Fact]
    public async Task RevalidationConflict_ReportsModeRunId()
    {
        using AuthoringTestDatabase database = new();
        await database.ActivateAsync();
        AuthoringRunRecord revalidation =
            await database.Store.CreateRunAsync(
                "test",
                [new("FHIR-1", "ticket", "revision-1")],
                runId: "revalidation-run");
        database.Execute(
            """
            UPDATE authoring_processor_modes
            SET RevalidationRequired = 1, RevalidationRunId = @runId
            WHERE ProcessorKind = 'test'
            """,
            ("@runId", revalidation.Id));

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => database.Store.CreateRunAsync(
                    "test",
                    [new("FHIR-2", "ticket", "revision-2")],
                    requestJson: "{}"));

        Assert.Equal(
            AuthoringConflictCode.RevalidationRequired,
            conflict.Code);
        Assert.Equal([revalidation.Id], conflict.RelatedRunIds);
    }

    [Fact]
    public async Task AcceptResult_IsAtomicAndReplaysTheOriginalReceipt()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) = await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = (await database.Store.ClaimItemAsync(run.Id, item.Id))!;
        AuthoringResultSubmission submission = new(
            run.Id,
            item.Id,
            claim.OperationId,
            item.ExpectedSourceRevision,
            AuthoringResultHasher.HashNormalizedUtf8("payload"));

        AuthoringReceiptAcceptance accepted = await database.Store.AcceptResultAsync(
            submission,
            claim.OperationToken,
            async (connection, ct) =>
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS domain_results(Id TEXT PRIMARY KEY, Value TEXT NOT NULL);
                    INSERT INTO domain_results(Id, Value) VALUES (@id, @value);
                    """;
                command.Parameters.AddWithValue("@id", item.Id);
                command.Parameters.AddWithValue("@value", "payload");
                await command.ExecuteNonQueryAsync(ct);
            });
        AuthoringReceiptAcceptance replay = await database.Store.AcceptResultAsync(
            submission,
            claim.OperationToken,
            (_, _) => throw new InvalidOperationException("replay must not rewrite domain rows"));

        Assert.False(accepted.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(accepted.Receipt, replay.Receipt);
        Assert.Equal(1, database.Scalar<int>("SELECT COUNT(*) FROM domain_results"));
        Assert.DoesNotContain(
            claim.OperationToken,
            database.Scalar<string>(
                "SELECT TokenVerifier FROM authoring_run_attempts WHERE OperationId = @operationId",
                ("@operationId", claim.OperationId)),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptResult_DifferentContentAfterAcceptanceConflicts()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) = await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = (await database.Store.ClaimItemAsync(run.Id, item.Id))!;
        AuthoringResultSubmission original = new(
            run.Id,
            item.Id,
            claim.OperationId,
            item.ExpectedSourceRevision,
            AuthoringResultHasher.HashNormalizedUtf8("one"));
        await database.Store.AcceptResultAsync(original, claim.OperationToken);

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Store.AcceptResultAsync(
                original with { ContentHash = AuthoringResultHasher.HashNormalizedUtf8("two") },
                claim.OperationToken));

        Assert.Equal(AuthoringConflictCode.ContentChanged, conflict.Code);
    }

    [Fact]
    public async Task AcceptResult_SourceRevisionMismatchRollsBackDomainMutation()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) = await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = (await database.Store.ClaimItemAsync(run.Id, item.Id))!;
        AuthoringResultSubmission submission = new(
            run.Id,
            item.Id,
            claim.OperationId,
            "newer-revision",
            AuthoringResultHasher.HashNormalizedUtf8("payload"));

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Store.AcceptResultAsync(
                submission,
                claim.OperationToken,
                async (connection, ct) =>
                {
                    await using SqliteCommand command = connection.CreateCommand();
                    command.CommandText = "CREATE TABLE should_not_exist(Id INTEGER);";
                    await command.ExecuteNonQueryAsync(ct);
                }));

        Assert.Equal(AuthoringConflictCode.SourceRevisionMismatch, conflict.Code);
        Assert.Equal(
            0,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'should_not_exist'"));
    }

    [Fact]
    public async Task FailedPersistenceCanRetryTheSameOperation()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) = await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = (await database.Store.ClaimItemAsync(run.Id, item.Id))!;
        AuthoringResultSubmission submission = new(
            run.Id,
            item.Id,
            claim.OperationId,
            item.ExpectedSourceRevision,
            AuthoringResultHasher.HashNormalizedUtf8("payload"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Store.AcceptResultAsync(
                submission,
                claim.OperationToken,
                (_, _) => throw new InvalidOperationException("write failed")));

        AuthoringReceiptAcceptance accepted = await database.Store.AcceptResultAsync(
            submission,
            claim.OperationToken);
        Assert.False(accepted.IsReplay);
    }

    [Fact]
    public async Task RetryBeforeReceiptRotatesOperationButPostReceiptRetryDoesNot()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) = await database.CreateRunningRunAsync();
        AuthoringOperationClaim first = (await database.Store.ClaimItemAsync(run.Id, item.Id))!;
        await database.Store.MarkItemErrorAsync(item.Id, "agent failed");

        AuthoringRetryResult authoringRetry = await database.Store.RetryItemAsync(item.Id);
        AuthoringOperationClaim second = (await database.Store.ClaimItemAsync(run.Id, item.Id))!;
        Assert.True(authoringRetry.RequiresAuthoring);
        Assert.NotEqual(first.OperationId, second.OperationId);

        AuthoringResultSubmission staleSubmission = new(
            run.Id,
            item.Id,
            first.OperationId,
            item.ExpectedSourceRevision,
            AuthoringResultHasher.HashNormalizedUtf8("payload"));
        AuthoringConflictException stale = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Store.AcceptResultAsync(staleSubmission, first.OperationToken));
        Assert.Equal(AuthoringConflictCode.StaleOperation, stale.Code);

        AuthoringResultSubmission acceptedSubmission = staleSubmission with { OperationId = second.OperationId };
        AuthoringReceiptAcceptance accepted = await database.Store.AcceptResultAsync(
            acceptedSubmission,
            second.OperationToken);
        await database.Store.MarkItemErrorAsync(item.Id, "hydration failed");
        AuthoringRetryResult postReceiptRetry = await database.Store.RetryItemAsync(item.Id);
        AuthoringRunItemRecord refreshed = Assert.Single(await database.Store.GetRunItemsAsync(run.Id));

        Assert.False(postReceiptRetry.RequiresAuthoring);
        Assert.Equal(second.OperationId, refreshed.CurrentOperationId);
        Assert.Equal(accepted.Receipt.ReceiptId, refreshed.AcceptedReceiptId);
        Assert.Equal(AuthoringStatusValues.Items.Persisted, refreshed.Status);
    }

    [Fact]
    public async Task MutationFenceAllowsOnlyOneActiveRun()
    {
        using AuthoringTestDatabase database = new();
        await database.ActivateAsync();
        AuthoringRunRecord first = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-1", "ticket", "revision-1")]);
        AuthoringRunRecord second = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-2", "ticket", "revision-2")]);

        Assert.True(await database.Store.TryAcquireMutationFenceAsync("test", first.Id));
        Assert.False(await database.Store.TryAcquireMutationFenceAsync("test", second.Id));
        Assert.Equal(AuthoringStatusValues.Runs.Queued, (await database.Store.GetRunAsync(second.Id))!.Status);
    }

    [Fact]
    public async Task MaintenanceRunReusesCurrentReceiptsWithoutAuthoringAttempts()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord sourceRun, AuthoringRunItemRecord sourceItem) =
            await database.CreateRunningRunAsync(databaseOnly: true);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(sourceRun.Id, sourceItem.Id));
        AuthoringReceiptAcceptance receipt =
            await database.Store.AcceptResultAsync(
                new AuthoringResultSubmission(
                    sourceRun.Id,
                    sourceItem.Id,
                    claim.OperationId,
                    sourceItem.ExpectedSourceRevision,
                    AuthoringResultHasher.HashNormalizedUtf8("payload")),
                claim.OperationToken);
        await database.Store.MarkItemCompleteAsync(
            sourceItem.Id,
            receipt.Receipt.ReceiptId);
        await database.Store.MarkRunFinalizingAsync(sourceRun.Id);
        await database.Store.CompleteRunAsync(sourceRun.Id, snapshotId: null);

        AuthoringRunRecord maintenance =
            await database.Store.CreateMaintenanceRunAsync(
                "test",
                [
                    new AuthoringMaintenanceRunItem(
                        sourceItem.BusinessKey,
                        sourceItem.ItemKind,
                        sourceItem.ExpectedSourceRevision,
                        receipt.Receipt.ReceiptId),
                ]);
        AuthoringRunItemRecord item = Assert.Single(
            await database.Store.GetRunItemsAsync(maintenance.Id));

        Assert.True(maintenance.DatabaseOnly);
        Assert.Equal(
            AuthoringRunPurposeValues.GroupingMaintenance,
            maintenance.Purpose);
        Assert.Null(maintenance.SourceRunId);
        Assert.Null(maintenance.RequestJson);
        Assert.Equal(AuthoringStatusValues.Items.Complete, item.Status);
        Assert.Equal(receipt.Receipt.ReceiptId, item.AcceptedReceiptId);
        Assert.Equal(
            0,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_run_attempts WHERE RunId = @runId",
                ("@runId", maintenance.Id)));
    }

    [Fact]
    public async Task PublicationRefreshRecordsPurposeAndSourceLineage()
    {
        using AuthoringTestDatabase database = new();
        ReadySourceRun source = await CreateReadySourceRunAsync(database);

        AuthoringRunRecord refresh =
            await database.Store.CreateMaintenanceRunAsync(
                "test",
                [
                    new AuthoringMaintenanceRunItem(
                        source.Item.BusinessKey,
                        source.Item.ItemKind,
                        source.Item.ExpectedSourceRevision,
                        source.Receipt.ReceiptId),
                ],
                AuthoringRunPurposeValues.PublicationRefresh,
                databaseOnly: false,
                sourceRunId: source.Run.Id);
        AuthoringRunItemRecord refreshItem = Assert.Single(
            await database.Store.GetRunItemsAsync(refresh.Id));

        Assert.Equal(
            AuthoringRunPurposeValues.PublicationRefresh,
            refresh.Purpose);
        Assert.Equal(source.Run.Id, refresh.SourceRunId);
        Assert.False(refresh.DatabaseOnly);
        Assert.Null(refresh.RequestJson);
        Assert.Equal(AuthoringStatusValues.Items.Complete, refreshItem.Status);
        Assert.Equal(source.Receipt.ReceiptId, refreshItem.AcceptedReceiptId);
        Assert.StartsWith(
            $"maintenance:{refresh.Id}:",
            refreshItem.ItemKind,
            StringComparison.Ordinal);
        Assert.Equal(
            0,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_run_attempts WHERE RunId = @runId",
                ("@runId", refresh.Id)));

        AuthoringOperatorRunList listed =
            await database.Store.ListOperatorRunsAsync("test");
        Assert.Contains(listed.Runs, value => value.Run.Id == refresh.Id);
        AuthoringRunControlStatus status =
            await new AuthoringRunControlService(
                database.Store,
                database.RetryPolicy).GetStatusAsync("test", refresh.Id);
        Assert.Equal(
            AuthoringRunPurposeValues.PublicationRefresh,
            status.Run.Purpose);
        Assert.Equal(source.Run.Id, status.Run.SourceRunId);
        Assert.Null(status.Run.CorpusComparison);
    }

    [Fact]
    public async Task MaintenanceSelectionPersistsRequestItemsAndFenceBeforeStageRegistration()
    {
        using AuthoringTestDatabase database = new();
        ReadySourceRun source = await CreateReadySourceRunAsync(database);
        const string privateInput = """{ "additionalTicketKeys": [], "privateAuthoredValue": "  do not project\nthis  " }""";
        AuthoringRunCorpusComparison comparison = new(source.Descriptor.SnapshotId, 1, 1, 0);
        AuthoringMaintenanceRunRequest request = new(1, "publication-enrichment", 1, comparison, privateInput);
        string json = request.Serialize();
        bool factoryCalled = false;

        AuthoringRunRecord run = await database.Store.CreateMaintenanceRunAsync(
            "test",
            async (connection, ct) =>
            {
                factoryCalled = true;
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT BusinessKey, ItemKind, ExpectedSourceRevision, AcceptedReceiptId
                    FROM authoring_run_items WHERE Id = @itemId
                    """;
                command.Parameters.AddWithValue("@itemId", source.Item.Id);
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
                Assert.True(await reader.ReadAsync(ct));
                return new AuthoringMaintenanceRunSelection(
                    [new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))],
                    json);
            },
            AuthoringRunPurposeValues.PublicationRefresh,
            databaseOnly: false,
            sourceRunId: source.Run.Id);

        Assert.True(factoryCalled);
        Assert.Equal(json, run.RequestJson);
        AuthoringRunStore restartedStore = new(database.OpenConnection);
        AuthoringRunRecord durable = Assert.IsType<AuthoringRunRecord>(
            await restartedStore.GetRunAsync(run.Id));
        Assert.Equal(json, durable.RequestJson);
        Assert.Equal(
            privateInput,
            Assert.IsType<AuthoringMaintenanceRunRequest>(
                AuthoringMaintenanceRunRequest.Parse(durable.RequestJson)).RecipeInputJson);
        Assert.Equal(run.Id, (await restartedStore.GetFencedRunAsync("test"))!.Id);
        AuthoringRunItemRecord item = Assert.Single(await restartedStore.GetRunItemsAsync(run.Id));
        Assert.Equal(source.Receipt.ReceiptId, item.AcceptedReceiptId);
        Assert.Equal(source.Item.ExpectedSourceRevision, item.ExpectedSourceRevision);
        Assert.Equal(AuthoringStatusValues.Items.Complete, item.Status);
        Assert.Equal(0, database.Scalar<int>(
            "SELECT COUNT(*) FROM authoring_run_stages WHERE RunId = @runId", ("@runId", run.Id)));
        Assert.Equal(0, database.Scalar<int>(
            "SELECT COUNT(*) FROM authoring_run_attempts WHERE RunId = @runId", ("@runId", run.Id)));

        AuthoringRunControlService control = new(restartedStore, database.RetryPolicy);
        AuthoringRunControlStatus status = await control.GetStatusAsync("test", run.Id);
        Assert.Equal(comparison, status.Run.CorpusComparison);
        AuthoringRunStatus listed = Assert.Single(
            (await control.ListAsync("test")).Runs, value => value.RunId == run.Id);
        Assert.Equal(comparison, listed.CorpusComparison);
        string responseJson = JsonSerializer.Serialize(
            new AuthoringRunResponse(status.Run, status.Items), JsonSerializerOptions.Web);
        Assert.Contains("\"corpusComparison\"", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("privateAuthoredValue", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("recipeInput", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publication-enrichment", responseJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MaintenanceSelectionFailureRollsBackFactoryWritesRequestItemsRunAndFence(bool failFactory)
    {
        using AuthoringTestDatabase database = new();
        ReadySourceRun source = await CreateReadySourceRunAsync(database);
        string json = new AuthoringMaintenanceRunRequest(
            1, "publication-enrichment", 1,
            new(source.Descriptor.SnapshotId, 1, 1, 0), "{}").Serialize();
        async Task<AuthoringMaintenanceRunSelection> SelectAsync(SqliteConnection connection, CancellationToken ct)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO authoring_run_input_provenance(RunId, Source, CapturedAt)
                VALUES('factory-side-effect', 'jira', '2026-09-15T12:00:00.0000000+00:00')
                """;
            await command.ExecuteNonQueryAsync(ct);
            if (failFactory)
            {
                throw new InvalidOperationException("Factory failed before returning selection.");
            }
            AuthoringMaintenanceRunItem item = new(
                source.Item.BusinessKey, source.Item.ItemKind,
                source.Item.ExpectedSourceRevision, source.Receipt.ReceiptId);
            return new([item, item], json);
        }

        Task<AuthoringRunRecord> CreateAsync() => database.Store.CreateMaintenanceRunAsync(
            "test", SelectAsync, AuthoringRunPurposeValues.PublicationRefresh,
            databaseOnly: false, sourceRunId: source.Run.Id);
        if (failFactory)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(CreateAsync);
        }
        else
        {
            await Assert.ThrowsAsync<SqliteException>(CreateAsync);
        }
        Assert.Equal(1, database.Scalar<int>("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(1, database.Scalar<int>("SELECT COUNT(*) FROM authoring_run_items"));
        Assert.Equal(0, database.Scalar<int>(
            "SELECT COUNT(*) FROM authoring_run_input_provenance WHERE RunId = 'factory-side-effect'"));
        Assert.Equal(0, database.Scalar<int>("SELECT COUNT(*) FROM authoring_runs WHERE RequestJson IS NOT NULL"));
        Assert.Null(await database.Store.GetFencedRunAsync("test"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("""{"contractVersion":2,"recipeName":"publication-enrichment","recipeVersion":1,"corpusComparison":null,"recipeInputJson":"{}"}""")]
    [InlineData("""{"contractVersion":1,"recipeName":"publication-enrichment","recipeVersion":1,"corpusComparison":null,"recipeInputJson":"null"}""")]
    [InlineData("""{"contractVersion":1,"contractVersion":1,"recipeName":"publication-enrichment","recipeVersion":1,"corpusComparison":null,"recipeInputJson":"{}"}""")]
    public async Task NonNullInvalidMaintenanceMetadataNeverSelectsLegacy(string json)
    {
        using AuthoringTestDatabase database = new();
        ReadySourceRun source = await CreateReadySourceRunAsync(database);
        Exception? error = await Record.ExceptionAsync(() =>
            database.Store.CreateMaintenanceRunAsync(
                "test",
                (_, _) => Task.FromResult(new AuthoringMaintenanceRunSelection(
                    [new(source.Item.BusinessKey, source.Item.ItemKind, source.Item.ExpectedSourceRevision, source.Receipt.ReceiptId)],
                    json)),
                AuthoringRunPurposeValues.PublicationRefresh,
                databaseOnly: false,
                sourceRunId: source.Run.Id));
        Assert.NotNull(error);
        Assert.True(error is JsonException or ArgumentException or NotSupportedException);
        Assert.Throws(error.GetType(), () =>
            AuthoringMaintenanceRunRequest.ReadCorpusComparison(json));
        Assert.Equal(1, database.Scalar<int>("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Null(await database.Store.GetFencedRunAsync("test"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("""{"contractVersion":2,"recipeName":"private-recipe","recipeVersion":1,"corpusComparison":{"sourceSnapshotId":"source-snapshot","sourceExportedTicketCount":1,"currentAcceptedTicketCount":1,"additionalTicketCount":0},"recipeInputJson":"{\"privateAuthoredValue\":\"do not project\"}"}""")]
    [InlineData("""{"contractVersion":1,"recipeName":"private-recipe","recipeVersion":1,"corpusComparison":{"sourceSnapshotId":"source-snapshot","sourceExportedTicketCount":1,"currentAcceptedTicketCount":2,"additionalTicketCount":0},"recipeInputJson":"{\"privateAuthoredValue\":\"do not project\"}"}""")]
    [InlineData("""{"contractVersion":1,"recipeName":"private-recipe","recipeVersion":1,"corpusComparison":null,"recipeInputJson":"{\"privateAuthoredValue\":\"do not project\"}","privateAuthoredValue":"do not project"}""")]
    public async Task MaintenanceStatusDistinguishesInvalidMetadataFromLegacyWithoutWriting(string? json)
    {
        using AuthoringTestDatabase database = new();
        ReadySourceRun source = await CreateReadySourceRunAsync(database);
        AuthoringRunRecord run = await database.Store.CreateMaintenanceRunAsync(
            "test",
            [new(source.Item.BusinessKey, source.Item.ItemKind, source.Item.ExpectedSourceRevision, source.Receipt.ReceiptId)],
            AuthoringRunPurposeValues.PublicationRefresh,
            databaseOnly: false,
            sourceRunId: source.Run.Id);
        AuthoringRunStore readOnlyStore = CreateReadOnlyRunStore(database);
        AuthoringRunControlService control = new(readOnlyStore, database.RetryPolicy);
        AuthoringRunControlStatus baseline = await control.GetStatusAsync("test", run.Id);

        foreach (string? storedError in new string?[] { null, "Existing run failure." })
        {
            database.Execute(
                "UPDATE authoring_runs SET RequestJson = @json, Error = @error WHERE Id = @runId",
                ("@json", json), ("@error", storedError), ("@runId", run.Id));
            AuthoringRunRecord stored = Assert.IsType<AuthoringRunRecord>(
                await readOnlyStore.GetRunAsync(run.Id));

            AuthoringRunControlStatus status = await control.GetStatusAsync("test", run.Id);
            AuthoringRunListResponse list = await control.ListAsync("test");
            AuthoringRunStatus listed = Assert.Single(list.Runs, value => value.RunId == run.Id);

            const string metadataError =
                "Maintenance request metadata is invalid or unsupported; corpus comparison is unavailable.";
            string? expectedError = json is null
                ? storedError
                : storedError is null ? metadataError : $"{storedError} {metadataError}";
            Assert.Equal(baseline.Run with { Error = expectedError }, status.Run);
            Assert.Null(status.Run.CorpusComparison);
            Assert.Equal(status.Run, listed);
            Assert.Equal(baseline.Items, status.Items);
            Assert.Equal(stored, await readOnlyStore.GetRunAsync(run.Id));

            foreach (string responseJson in new[]
            {
                JsonSerializer.Serialize(
                    new AuthoringRunResponse(status.Run, status.Items), JsonSerializerOptions.Web),
                JsonSerializer.Serialize(list, JsonSerializerOptions.Web),
            })
            {
                Assert.DoesNotContain("privateAuthoredValue", responseJson, StringComparison.Ordinal);
                Assert.DoesNotContain("do not project", responseJson, StringComparison.Ordinal);
                Assert.DoesNotContain("recipeInput", responseJson, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("private-recipe", responseJson, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData("publication-enrichment", 99, null)]
    [InlineData("publication-enrichment", 99, "Existing run failure.")]
    [InlineData("unknown-recipe", 1, null)]
    [InlineData("unknown-recipe", 1, "Existing run failure.")]
    public async Task UnknownRecipeRemainsInspectableButCannotExecute(
        string recipeName,
        int recipeVersion,
        string? storedError)
    {
        using AuthoringTestDatabase database = new();
        ReadySourceRun source = await CreateReadySourceRunAsync(database);
        AuthoringRunCorpusComparison comparison = new(source.Descriptor.SnapshotId, 1, 1, 0);
        string json = new AuthoringMaintenanceRunRequest(
            1, recipeName, recipeVersion, comparison,
            """{"unknownPrivateVersion":42,"privateAuthoredValue":"do not project"}""").Serialize();
        AuthoringRunRecord run = await database.Store.CreateMaintenanceRunAsync(
            "test",
            (_, _) => Task.FromResult(new AuthoringMaintenanceRunSelection(
                [new(source.Item.BusinessKey, source.Item.ItemKind, source.Item.ExpectedSourceRevision, source.Receipt.ReceiptId)],
                json)),
            AuthoringRunPurposeValues.PublicationRefresh,
            databaseOnly: false,
            sourceRunId: source.Run.Id);
        AuthoringMaintenanceRunRequest parsed = Assert.IsType<AuthoringMaintenanceRunRequest>(
            AuthoringMaintenanceRunRequest.Parse(run.RequestJson));
        Assert.Throws<NotSupportedException>(() => parsed.EnsureRecipe("publication-enrichment", 1));
        Assert.Equal(comparison, AuthoringMaintenanceRunRequest.ReadCorpusComparison(json));
        database.Execute(
            "UPDATE authoring_runs SET Error = @error WHERE Id = @runId",
            ("@error", storedError), ("@runId", run.Id));
        AuthoringRunStore readOnlyStore = CreateReadOnlyRunStore(database);
        AuthoringRunRecord stored = Assert.IsType<AuthoringRunRecord>(
            await readOnlyStore.GetRunAsync(run.Id));
        AuthoringRunControlService control = new(readOnlyStore, database.RetryPolicy);
        AuthoringRunControlStatus status = await control.GetStatusAsync("test", run.Id);
        AuthoringRunListResponse list = await control.ListAsync("test");
        AuthoringRunStatus listed = Assert.Single(list.Runs, value => value.RunId == run.Id);

        Assert.Equal(comparison, status.Run.CorpusComparison);
        Assert.Equal(storedError, status.Run.Error);
        Assert.Equal(run.Status, status.Run.Status);
        Assert.Equal(status.Run, listed);
        Assert.Equal(stored, await readOnlyStore.GetRunAsync(run.Id));
        foreach (string responseJson in new[]
        {
            JsonSerializer.Serialize(
                new AuthoringRunResponse(status.Run, status.Items), JsonSerializerOptions.Web),
            JsonSerializer.Serialize(list, JsonSerializerOptions.Web),
        })
        {
            Assert.DoesNotContain("unknownPrivateVersion", responseJson, StringComparison.Ordinal);
            Assert.DoesNotContain("privateAuthoredValue", responseJson, StringComparison.Ordinal);
            Assert.DoesNotContain("do not project", responseJson, StringComparison.Ordinal);
            Assert.DoesNotContain("recipeInput", responseJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(recipeName, responseJson, StringComparison.Ordinal);
        }
        Assert.Null(AuthoringMaintenanceRunRequest.Parse(null));
        Assert.Null(AuthoringMaintenanceRunRequest.ReadCorpusComparison(null));
    }

    [Theory]
    [InlineData(-1, 0, 1)]
    [InlineData(0, -1, 0)]
    [InlineData(1, 0, -1)]
    [InlineData(1, 3, 1)]
    [InlineData(int.MaxValue, 0, 1)]
    public void CorpusComparisonRejectsNegativeInconsistentAndOverflowingCounts(int source, int current, int additional)
    {
        AuthoringRunCorpusComparison comparison = new("snapshot", source, current, additional);
        Assert.ThrowsAny<ArgumentException>(() => comparison.Validate());
        AuthoringMaintenanceRunRequest request = new(1, "publication-enrichment", 1, comparison, "{}");
        Assert.ThrowsAny<ArgumentException>(() => request.Serialize());
        Assert.ThrowsAny<ArgumentException>(() => AuthoringMaintenanceRunRequest.ReadCorpusComparison(
            JsonSerializer.Serialize(request, JsonSerializerOptions.Web)));
    }

    [Fact]
    public async Task PublicationRefreshRejectsInvalidPurposeAndLineage()
    {
        using AuthoringTestDatabase database = new();
        await database.ActivateAsync();
        AuthoringRunRecord activeSource = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-1", "ticket", "revision-1")]);
        AuthoringMaintenanceRunItem[] items =
        [
            new("FHIR-1", "ticket", "revision-1", "receipt-1"),
        ];

        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.CreateMaintenanceRunAsync(
                "test",
                items,
                AuthoringRunPurposeValues.PublicationRefresh,
                databaseOnly: false));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            database.Store.CreateMaintenanceRunAsync(
                "test",
                items,
                AuthoringRunPurposeValues.PublicationRefresh,
                databaseOnly: false,
                sourceRunId: "missing"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.CreateMaintenanceRunAsync(
                "test",
                items,
                AuthoringRunPurposeValues.PublicationRefresh,
                databaseOnly: false,
                sourceRunId: activeSource.Id));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.CreateMaintenanceRunAsync(
                "test",
                items,
                AuthoringRunPurposeValues.GroupingMaintenance,
                databaseOnly: false));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.CreateMaintenanceRunAsync(
                "test",
                items,
                AuthoringRunPurposeValues.Authoring,
                databaseOnly: true));

        Assert.Equal(
            1,
            database.Scalar<int>("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Null(await database.Store.GetFencedRunAsync("test"));
    }

    [Fact]
    public async Task SnapshotPublicationProofRoundTripsThroughRecovery()
    {
        using AuthoringTestDatabase database = new();
        DateTimeOffset sourceRefresh =
            new(2026, 9, 14, 10, 30, 0, TimeSpan.Zero);
        DateTimeOffset capturedAt = sourceRefresh.AddMinutes(5);
        AuthoringSnapshotPublicationProof proof = new(
            1,
            AuthoringRunPurposeValues.PublicationRefresh,
            "source-run",
            "jira",
            sourceRefresh,
            42,
            1,
            "corpus-fingerprint",
            "grouping-fingerprint",
            capturedAt);
        ReadySourceRun source =
            await CreateReadySourceRunAsync(database, proof);

        Assert.Equal(proof, source.Descriptor.PublicationProof);
        AuthoringReviewSnapshotRecord record = Assert.Single(
            await database.Store.GetSnapshotRecordsAsync(),
            value => value.Id == source.Descriptor.SnapshotId);
        Assert.NotNull(record.PublicationProofJson);

        database.Execute(
            """
            UPDATE authoring_review_snapshots
            SET Status = @status
            WHERE Id = @snapshotId
            """,
            ("@status", AuthoringStatusValues.Snapshots.Promoted),
            ("@snapshotId", source.Descriptor.SnapshotId));

        SnapshotReconciliationResult result = Assert.Single(
            await new SqliteReviewSnapshotReconciler(
                database.Store).ReconcileAsync(),
            value => value.SnapshotId == source.Descriptor.SnapshotId);
        Assert.Equal(AuthoringStatusValues.Snapshots.Ready, result.Status);
        AuthoringSnapshotDescriptor recovered =
            Assert.IsType<AuthoringSnapshotDescriptor>(
                await database.Store.GetSnapshotDescriptorAsync(
                    source.Descriptor.SnapshotId));
        Assert.Equal(proof, recovered.PublicationProof);

        string snapshotPath = Path.Combine(
            Path.GetDirectoryName(database.DatabasePath)!,
            "snapshots",
            recovered.FileName);
        using SqliteConnection snapshot = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = snapshotPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        snapshot.Open();
        using SqliteCommand proofColumn = snapshot.CreateCommand();
        proofColumn.CommandText =
            """
            SELECT COUNT(*)
            FROM pragma_table_info('authoring_snapshot_provenance')
            WHERE name = 'PublicationProofJson'
            """;
        Assert.Equal(0, Convert.ToInt32(proofColumn.ExecuteScalar()));
    }

    [Fact]
    public async Task LegacyMaintenanceMigrationIsConservative()
    {
        using AuthoringTestDatabase database = new();
        await database.ActivateAsync();
        string createdAt =
            new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero)
                .ToString("O");
        DropIndexesForColumns(
            database,
            "authoring_runs",
            ["Purpose", "SourceRunId"]);
        database.Execute(
            """
            INSERT INTO authoring_runs(
                Id, ProcessorKind, AuthoringEpoch, Status, Purpose,
                DatabaseOnly, TotalItems, CreatedAt)
            VALUES
                ('legacy-maintenance', 'test', 1, 'running', 'authoring',
                 1, 1, @createdAt),
                ('ambiguous-database-only', 'test', 1, 'running', 'authoring',
                 1, 1, @createdAt),
                ('revalidation-current', 'test', 1, 'running', 'authoring',
                 0, 1, @createdAt),
                ('revalidation-previous', 'test', 1, 'superseded', 'authoring',
                 0, 1, @createdAt);

            INSERT INTO authoring_run_items(
                Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision,
                Status, AcceptedReceiptId, AttemptCount, CreatedAt, CompletedAt)
            VALUES
                ('maintenance-item', 'legacy-maintenance', 'FHIR-1',
                 'maintenance:legacy-maintenance:ticket', 'revision-1',
                 'complete', 'receipt-1', 0, @createdAt, @createdAt),
                ('ambiguous-item', 'ambiguous-database-only', 'FHIR-2',
                 'ticket', 'revision-2', 'complete', 'receipt-2', 0,
                 @createdAt, @createdAt),
                ('current-item', 'revalidation-current', 'FHIR-3',
                 'ticket', 'revision-3', 'pending', NULL, 0, @createdAt, NULL),
                ('previous-item', 'revalidation-previous', 'FHIR-4',
                 'ticket', 'revision-4', 'superseded', NULL, 0,
                 @createdAt, @createdAt);

            INSERT INTO authoring_revalidation_lineage(RunId, PreviousRunId)
            VALUES('revalidation-current', 'revalidation-previous');

            UPDATE authoring_processor_modes
            SET RevalidationRequired = 1,
                RevalidationRunId = 'revalidation-current'
            WHERE ProcessorKind = 'test';

            ALTER TABLE authoring_runs DROP COLUMN SourceRunId;
            ALTER TABLE authoring_runs DROP COLUMN Purpose;
            ALTER TABLE authoring_review_snapshots
                DROP COLUMN PublicationProofJson;
            """,
            ("@createdAt", createdAt));

        database.Store.Initialize();
        database.Store.Initialize();

        Assert.Equal(
            AuthoringRunPurposeValues.GroupingMaintenance,
            (await database.Store.GetRunAsync("legacy-maintenance"))!.Purpose);
        Assert.Equal(
            AuthoringRunPurposeValues.Authoring,
            (await database.Store.GetRunAsync(
                "ambiguous-database-only"))!.Purpose);
        Assert.Equal(
            AuthoringRunPurposeValues.InitialRevalidation,
            (await database.Store.GetRunAsync(
                "revalidation-current"))!.Purpose);
        Assert.Equal(
            AuthoringRunPurposeValues.InitialRevalidation,
            (await database.Store.GetRunAsync(
                "revalidation-previous"))!.Purpose);
        Assert.Equal(
            1,
            database.Scalar<int>(
                """
                SELECT COUNT(*)
                FROM pragma_table_info('authoring_review_snapshots')
                WHERE name = 'PublicationProofJson'
                """));
    }

    [Fact]
    public async Task RunStagesResumeAndRejectChangedFingerprint()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await database.CreateRunningRunAsync();
        AuthoringRunStageRecord stage = await database.Store.EnsureRunStageAsync(
            run.Id,
            "grouping",
            "fhir-core",
            "fingerprint-1");
        AuthoringRunStageLease firstLease = Assert.IsType<AuthoringRunStageLease>(
            await database.Store.TryStartRunStageAsync(stage.Id));
        await database.Store.FailRunStageAsync(stage.Id, firstLease.LeaseId, "failed");
        AuthoringRunStageLease secondLease = Assert.IsType<AuthoringRunStageLease>(
            await database.Store.TryStartRunStageAsync(stage.Id));
        await database.Store.CompleteRunStageAsync(stage.Id, secondLease.LeaseId);

        AuthoringRunStageRecord completed = Assert.Single(await database.Store.GetRunStagesAsync(run.Id));
        Assert.Equal(2, completed.AttemptCount);
        Assert.Equal(AuthoringStatusValues.Stages.Complete, completed.Status);

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Store.EnsureRunStageAsync(
                run.Id,
                "grouping",
                "fhir-core",
                "fingerprint-2"));
        Assert.Equal(AuthoringConflictCode.StageFingerprintMismatch, conflict.Code);
    }

    [Fact]
    public async Task RunStageLeasePreventsConcurrentCompletionAndCanBeReclaimed()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, _) = await database.CreateRunningRunAsync();
        AuthoringRunStageRecord stage = await database.Store.EnsureRunStageAsync(
            run.Id,
            "grouping",
            "fhir-core",
            "fingerprint-1");
        DateTimeOffset startedAt = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        AuthoringRunStageLease first = Assert.IsType<AuthoringRunStageLease>(
            await database.Store.TryStartRunStageAsync(
                stage.Id,
                TimeSpan.FromMinutes(10),
                startedAt));

        Assert.Null(
            await database.Store.TryStartRunStageAsync(
                stage.Id,
                TimeSpan.FromMinutes(10),
                startedAt.AddMinutes(9)));
        AuthoringRunStageLease reclaimed = Assert.IsType<AuthoringRunStageLease>(
            await database.Store.TryStartRunStageAsync(
                stage.Id,
                TimeSpan.FromMinutes(10),
                startedAt.AddMinutes(11)));
        Assert.NotEqual(first.LeaseId, reclaimed.LeaseId);
        AuthoringConflictException lost = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Store.CompleteRunStageAsync(stage.Id, first.LeaseId));
        Assert.Equal(AuthoringConflictCode.StageLeaseLost, lost.Code);
        await database.Store.CompleteRunStageAsync(stage.Id, reclaimed.LeaseId);
    }

    [Fact]
    public async Task ProcessorModeIsOneWayAndAllocatesEpochOnActivation()
    {
        using AuthoringTestDatabase database = new();
        AuthoringProcessorModeRecord initial = await database.Store.EnsureProcessorModeAsync("test");
        AuthoringProcessorModeRecord cuttingOver = await database.Store.TransitionProcessorModeAsync(
            "test",
            AuthoringStatusValues.ProcessorModes.Legacy,
            AuthoringStatusValues.ProcessorModes.CuttingOver);
        AuthoringProcessorModeRecord active = await database.Store.TransitionProcessorModeAsync(
            "test",
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            AuthoringStatusValues.ProcessorModes.RunBacked);

        Assert.Equal(0, initial.Epoch);
        Assert.Equal(0, cuttingOver.Epoch);
        Assert.Equal(1, active.Epoch);
        AuthoringProcessorModeRecord replay = await database.Store.TransitionProcessorModeAsync(
            "test",
            AuthoringStatusValues.ProcessorModes.CuttingOver,
            AuthoringStatusValues.ProcessorModes.RunBacked);
        Assert.Equal(1, replay.Epoch);
        Assert.Throws<InvalidOperationException>(
            () => AuthoringStatusValues.EnsureModeTransition(
                AuthoringStatusValues.ProcessorModes.RunBacked,
                AuthoringStatusValues.ProcessorModes.Legacy));
    }

    [Fact]
    public async Task RetryCannotReopenCompletedRun()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync(databaseOnly: true);
        AuthoringOperationClaim claim = (await database.Store.ClaimItemAsync(run.Id, item.Id))!;
        AuthoringReceiptAcceptance receipt = await database.Store.AcceptResultAsync(
            new AuthoringResultSubmission(
                run.Id,
                item.Id,
                claim.OperationId,
                item.ExpectedSourceRevision,
                AuthoringResultHasher.HashNormalizedUtf8("payload")),
            claim.OperationToken);
        await database.Store.MarkItemCompleteAsync(item.Id, receipt.Receipt.ReceiptId);
        await database.Store.MarkRunFinalizingAsync(run.Id);
        await database.Store.CompleteRunAsync(run.Id, snapshotId: null);

        AuthoringConflictException conflict = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => database.Store.RetryItemAsync(item.Id));
        Assert.Equal(AuthoringConflictCode.MutationFenceUnavailable, conflict.Code);
    }

    [Fact]
    public async Task RecordClaimFailure_ClosesAttemptAndSupersedesAtExactLimit()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringMaxAttempts = 3,
            AuthoringRetryDelay = "00:01:00",
        });
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        DateTimeOffset now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
                await database.Store.ClaimItemAsync(run.Id, item.Id, now));
            Assert.Equal(attempt, claim.AttemptNumber);
            await database.Store.MarkClaimErrorAsync(
                item.Id,
                claim.OperationId,
                $"failure {attempt}",
                now.AddSeconds(1));

            AuthoringRunItemRecord failed = Assert.Single(
                await database.Store.GetRunItemsAsync(run.Id));
            Assert.Equal(
                attempt == 3
                    ? AuthoringStatusValues.Items.Superseded
                    : AuthoringStatusValues.Items.Error,
                failed.Status);
            Assert.Equal(
                AuthoringStatusValues.Attempts.Error,
                database.Scalar<string>(
                    "SELECT Status FROM authoring_run_attempts WHERE OperationId = @operationId",
                    ("@operationId", claim.OperationId)));

            if (attempt < 3)
            {
                now = now.AddMinutes(2);
                AuthoringErrorReconciliationResult reconciliation =
                    await database.Store.ReconcileErroredItemsAsync(run.Id, now);
                Assert.Equal(1, reconciliation.RetriedItems);
            }
        }

        Assert.Equal(
            3,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_run_attempts WHERE RunItemId = @itemId",
                ("@itemId", item.Id)));
    }

    [Fact]
    public async Task ReconcileErroredItems_RepairsLegacyActiveAttemptAndPromotesWhenDue()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringRetryDelay = "00:01:00",
            AuthoringMaxAttempts = 3,
        });
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        DateTimeOffset failedAt = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id, failedAt.AddMinutes(-1)));
        database.Execute(
            """
            UPDATE authoring_run_items
            SET Status = 'error', CompletedAt = @completedAt, Error = @error
            WHERE Id = @itemId
            """,
            ("@completedAt", failedAt.ToString("O")),
            ("@error", "legacy failure"),
            ("@itemId", item.Id));

        AuthoringErrorReconciliationResult result =
            await database.Store.ReconcileErroredItemsAsync(
                run.Id,
                failedAt.AddMinutes(1));

        Assert.Equal(1, result.RetriedItems);
        AuthoringRunItemRecord reconciled = Assert.Single(
            await database.Store.GetRunItemsAsync(run.Id));
        Assert.Equal(AuthoringStatusValues.Items.Pending, reconciled.Status);
        Assert.Null(reconciled.CurrentOperationId);
        Assert.Equal(
            AuthoringStatusValues.Attempts.Error,
            database.Scalar<string>(
                "SELECT Status FROM authoring_run_attempts WHERE OperationId = @operationId",
                ("@operationId", claim.OperationId)));
        Assert.Equal(
            "legacy failure",
            database.Scalar<string>(
                "SELECT Error FROM authoring_run_attempts WHERE OperationId = @operationId",
                ("@operationId", claim.OperationId)));
    }

    [Fact]
    public async Task RetryItem_AtAttemptLimitCannotCreateFourthClaim()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringMaxAttempts = 3,
        });
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id));
        database.Execute(
            """
            UPDATE authoring_run_items
            SET Status = 'error', AttemptCount = 3, CompletedAt = @completedAt, Error = 'legacy failure'
            WHERE Id = @itemId
            """,
            ("@completedAt", DateTimeOffset.UtcNow.ToString("O")),
            ("@itemId", item.Id));

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => database.Store.RetryItemAsync(item.Id));

        Assert.Equal(AuthoringConflictCode.AttemptLimitReached, conflict.Code);
        Assert.Null(await database.Store.ClaimItemAsync(run.Id, item.Id));
        Assert.Equal(
            0,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_run_attempts WHERE RunItemId = @itemId AND AttemptNumber > 3",
                ("@itemId", item.Id)));
        Assert.Equal(claim.OperationId, (await database.Store.GetRunItemsAsync(run.Id))[0].CurrentOperationId);
    }

    [Fact]
    public async Task ManualRetryAndExhaustionRace_NeverCreatesAttemptFour()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringMaxAttempts = 3,
            AuthoringRetryDelay = "00:00:01",
        });
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        await database.Store.ClaimItemAsync(run.Id, item.Id);
        DateTimeOffset failedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        database.Execute(
            """
            UPDATE authoring_run_items
            SET Status = 'error', AttemptCount = 3, CompletedAt = @completedAt, Error = 'legacy failure'
            WHERE Id = @itemId
            """,
            ("@completedAt", failedAt.ToString("O")),
            ("@itemId", item.Id));

        Task<Exception?> retry = Record.ExceptionAsync(
            () => database.Store.RetryItemAsync(item.Id));
        Task<Exception?> reconcile = Record.ExceptionAsync(
            () => database.Store.ReconcileErroredItemsAsync(run.Id, DateTimeOffset.UtcNow));
        await Task.WhenAll(retry, reconcile);
        Exception? retryException = await retry;
        Exception? reconcileException = await reconcile;

        Assert.Null(reconcileException);
        AuthoringConflictException conflict =
            Assert.IsType<AuthoringConflictException>(retryException);
        Assert.Contains(
            conflict.Code,
            new[]
            {
                AuthoringConflictCode.AttemptLimitReached,
                AuthoringConflictCode.ItemNotClaimable,
            });
        Assert.Null(await database.Store.ClaimItemAsync(run.Id, item.Id));
        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            Assert.Single(await database.Store.GetRunItemsAsync(run.Id)).Status);
        Assert.Equal(
            0,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_run_attempts WHERE RunItemId = @itemId AND AttemptNumber > 3",
                ("@itemId", item.Id)));
    }

    [Fact]
    public async Task ReconcileErroredItems_ResumesAcceptedReceiptWithoutNewAttempt()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringRetryDelay = "00:01:00",
        });
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        DateTimeOffset now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id, now));
        AuthoringReceiptAcceptance receipt = await database.Store.AcceptResultAsync(
            new AuthoringResultSubmission(
                run.Id,
                item.Id,
                claim.OperationId,
                item.ExpectedSourceRevision,
                AuthoringResultHasher.HashNormalizedUtf8("payload")),
            claim.OperationToken,
            now: now.AddSeconds(1));
        string lease = Assert.IsType<string>(
            await database.Store.ClaimPersistedItemAsync(
                item.Id,
                receipt.Receipt.ReceiptId,
                claim.OperationId,
                now.AddSeconds(2)));
        await database.Store.MarkClaimErrorAsync(
            item.Id,
            lease,
            "post-receipt failure",
            now.AddSeconds(3));

        AuthoringErrorReconciliationResult result =
            await database.Store.ReconcileErroredItemsAsync(
                run.Id,
                now.AddMinutes(2));

        Assert.Equal(1, result.ResumedReceiptItems);
        AuthoringRunItemRecord resumed = Assert.Single(
            await database.Store.GetRunItemsAsync(run.Id));
        Assert.Equal(AuthoringStatusValues.Items.Persisted, resumed.Status);
        Assert.Equal(receipt.Receipt.ReceiptId, resumed.AcceptedReceiptId);
        Assert.Equal(1, resumed.AttemptCount);
        Assert.Equal(
            1,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_run_attempts WHERE RunItemId = @itemId",
                ("@itemId", item.Id)));
    }

    [Fact]
    public async Task SupersedeErroredItem_LeavesRunFencedAndFinalizable()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync(databaseOnly: true);
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id));
        await database.Store.MarkClaimErrorAsync(
            item.Id,
            claim.OperationId,
            "worker failure");

        AuthoringItemSupersedeResult result =
            await database.Store.SupersedeErroredItemAsync(
                run.Id,
                item.Id,
                "not actionable");

        Assert.Equal(AuthoringStatusValues.Items.Superseded, result.Status);
        Assert.Equal(run.Id, (await database.Store.GetFencedRunAsync("test"))!.Id);
        Assert.True(await database.Store.AllItemsCompleteAsync(run.Id));
        Assert.Equal(
            "worker failure",
            database.Scalar<string>(
                "SELECT Error FROM authoring_run_attempts WHERE OperationId = @operationId",
                ("@operationId", claim.OperationId)));
        Assert.Equal(
            "not actionable",
            Assert.Single(await database.Store.GetRunItemsAsync(run.Id)).Error);
    }

    [Fact]
    public async Task SupersedeErroredItem_RejectsAcceptedReceipt()
    {
        using AuthoringTestDatabase database = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync();
        AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
            await database.Store.ClaimItemAsync(run.Id, item.Id));
        AuthoringReceiptAcceptance receipt = await database.Store.AcceptResultAsync(
            new AuthoringResultSubmission(
                run.Id,
                item.Id,
                claim.OperationId,
                item.ExpectedSourceRevision,
                AuthoringResultHasher.HashNormalizedUtf8("payload")),
            claim.OperationToken);
        string lease = Assert.IsType<string>(
            await database.Store.ClaimPersistedItemAsync(
                item.Id,
                receipt.Receipt.ReceiptId,
                claim.OperationId));
        await database.Store.MarkClaimErrorAsync(
            item.Id,
            lease,
            "post-receipt failure");

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(
                () => database.Store.SupersedeErroredItemAsync(
                    run.Id,
                    item.Id,
                    "discard"));

        Assert.Equal(AuthoringConflictCode.ItemNotClaimable, conflict.Code);
        Assert.Equal(
            AuthoringStatusValues.Items.Error,
            Assert.Single(await database.Store.GetRunItemsAsync(run.Id)).Status);
    }

    [Fact]
    public async Task ReplaceRevalidationRun_UnchangedRevisionSeedsLineageAttemptCount()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringMaxAttempts = 3,
            AuthoringRetryDelay = "00:01:00",
        });
        (AuthoringRunRecord firstRun, AuthoringRunItemRecord firstItem) =
            await database.CreateRunningRunAsync();
        MarkAsInitialRevalidation(database, firstRun.Id);
        DateTimeOffset now =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringOperationClaim firstClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await database.Store.ClaimItemAsync(
                    firstRun.Id,
                    firstItem.Id,
                    now));
        await database.Store.MarkClaimErrorAsync(
            firstItem.Id,
            firstClaim.OperationId,
            "first failure",
            now.AddSeconds(1));

        AuthoringRunRecord secondRun =
            await database.Store.ReplaceRevalidationRunAsync(
                "test",
                firstRun.Id,
                [new("FHIR-1", "ticket", "revision-1")],
                now.AddMinutes(1));
        AuthoringRunItemRecord secondItem = Assert.Single(
            await database.Store.GetRunItemsAsync(secondRun.Id));
        Assert.Equal(1, secondItem.AttemptCount);
        AuthoringOperationClaim secondClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await database.Store.ClaimItemAsync(
                    secondRun.Id,
                    secondItem.Id,
                    now.AddMinutes(2)));
        Assert.Equal(2, secondClaim.AttemptNumber);
        await database.Store.MarkClaimErrorAsync(
            secondItem.Id,
            secondClaim.OperationId,
            "second failure",
            now.AddMinutes(2).AddSeconds(1));

        AuthoringRunRecord thirdRun =
            await database.Store.ReplaceRevalidationRunAsync(
                "test",
                secondRun.Id,
                [new("FHIR-1", "ticket", "revision-1")],
                now.AddMinutes(3));
        AuthoringRunItemRecord thirdItem = Assert.Single(
            await database.Store.GetRunItemsAsync(thirdRun.Id));

        Assert.Equal(2, thirdItem.AttemptCount);
        Assert.Equal(AuthoringStatusValues.Items.Pending, thirdItem.Status);
        Assert.Equal(
            [secondItem.Id, firstItem.Id],
            (await database.Store.GetRevalidationSupersededItemsAsync(
                thirdRun.Id))
            .Select(item => item.Id)
            .ToArray());
    }

    [Fact]
    public async Task ReplaceRevalidationRun_AtLineageLimitCreatesSupersededItem()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringMaxAttempts = 2,
            AuthoringRetryDelay = "00:01:00",
        });
        (AuthoringRunRecord firstRun, AuthoringRunItemRecord firstItem) =
            await database.CreateRunningRunAsync();
        MarkAsInitialRevalidation(database, firstRun.Id);
        DateTimeOffset now =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        AuthoringOperationClaim firstClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await database.Store.ClaimItemAsync(
                    firstRun.Id,
                    firstItem.Id,
                    now));
        await database.Store.MarkClaimErrorAsync(
            firstItem.Id,
            firstClaim.OperationId,
            "first failure",
            now.AddSeconds(1));
        AuthoringRunRecord secondRun =
            await database.Store.ReplaceRevalidationRunAsync(
                "test",
                firstRun.Id,
                [new("FHIR-1", "ticket", "revision-1")],
                now.AddMinutes(1));
        AuthoringRunItemRecord secondItem = Assert.Single(
            await database.Store.GetRunItemsAsync(secondRun.Id));
        AuthoringOperationClaim secondClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await database.Store.ClaimItemAsync(
                    secondRun.Id,
                    secondItem.Id,
                    now.AddMinutes(2)));
        await database.Store.MarkClaimErrorAsync(
            secondItem.Id,
            secondClaim.OperationId,
            "second failure",
            now.AddMinutes(2).AddSeconds(1));

        AuthoringRunRecord thirdRun =
            await database.Store.ReplaceRevalidationRunAsync(
                "test",
                secondRun.Id,
                [new("FHIR-1", "ticket", "revision-1")],
                now.AddMinutes(3));
        AuthoringRunItemRecord thirdItem = Assert.Single(
            await database.Store.GetRunItemsAsync(thirdRun.Id));

        Assert.Equal(2, thirdItem.AttemptCount);
        Assert.Equal(
            AuthoringStatusValues.Items.Superseded,
            thirdItem.Status);
        Assert.Equal(now.AddMinutes(3), thirdItem.CompletedAt);
        Assert.Equal(
            "Authoring attempt limit of 2 was reached.",
            thirdItem.Error);
        Assert.Null(await database.Store.ClaimItemAsync(
            thirdRun.Id,
            thirdItem.Id,
            now.AddMinutes(4)));
        Assert.Equal(
            [thirdItem.Id, secondItem.Id, firstItem.Id],
            (await database.Store.GetRevalidationSupersededItemsAsync(
                thirdRun.Id))
            .Select(item => item.Id)
            .ToArray());
    }

    [Fact]
    public async Task ReplaceRevalidationRun_ChangedRevisionStartsNewBudget()
    {
        using AuthoringTestDatabase database = new(new ProcessingServiceOptions
        {
            AuthoringMaxAttempts = 3,
            AuthoringRetryDelay = "00:01:00",
        });
        (AuthoringRunRecord firstRun, AuthoringRunItemRecord firstItem) =
            await database.CreateRunningRunAsync();
        MarkAsInitialRevalidation(database, firstRun.Id);
        AuthoringOperationClaim firstClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await database.Store.ClaimItemAsync(
                    firstRun.Id,
                    firstItem.Id));
        await database.Store.MarkClaimErrorAsync(
            firstItem.Id,
            firstClaim.OperationId,
            "first failure");

        AuthoringRunRecord replacement =
            await database.Store.ReplaceRevalidationRunAsync(
                "test",
                firstRun.Id,
                [new("FHIR-1", "ticket", "revision-2")]);
        AuthoringRunItemRecord replacementItem = Assert.Single(
            await database.Store.GetRunItemsAsync(replacement.Id));
        AuthoringOperationClaim replacementClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await database.Store.ClaimItemAsync(
                    replacement.Id,
                    replacementItem.Id));

        Assert.Equal(0, replacementItem.AttemptCount);
        Assert.Equal(1, replacementClaim.AttemptNumber);
    }

    [Fact]
    public async Task ReplaceRevalidationRun_FreezesNewProvenanceWithoutChangingPredecessor()
    {
        using AuthoringTestDatabase database = new();
        await database.ActivateAsync();
        DateTimeOffset firstCapturedAt =
            new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset secondCapturedAt = firstCapturedAt.AddHours(1);
        AuthoringRunRecord first = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-1", "ticket", "revision-1")],
            runId: "first-revalidation",
            now: firstCapturedAt,
            inputProvenance:
            [
                new("jira", firstCapturedAt.AddDays(-1), 10),
            ]);
        MarkAsInitialRevalidation(database, first.Id);

        AuthoringRunRecord replacement =
            await database.Store.ReplaceRevalidationRunAsync(
                "test",
                first.Id,
                [new("FHIR-1", "ticket", "revision-2")],
                now: secondCapturedAt,
                inputProvenance:
                [
                    new("jira", secondCapturedAt.AddDays(-1), 11),
                ]);

        AuthoringRunInputProvenanceRecord firstProvenance = Assert.Single(
            await database.Store.GetRunInputProvenanceAsync(first.Id));
        AuthoringRunInputProvenanceRecord replacementProvenance =
            Assert.Single(
                await database.Store.GetRunInputProvenanceAsync(
                    replacement.Id));
        Assert.Equal(10, firstProvenance.ContentRevision);
        Assert.Equal(firstCapturedAt, firstProvenance.CapturedAt);
        Assert.Equal(11, replacementProvenance.ContentRevision);
        Assert.Equal(secondCapturedAt, replacementProvenance.CapturedAt);
    }

    [Fact]
    public async Task ReplaceRevalidationRun_InvalidProvenanceRollsBackReplacement()
    {
        using AuthoringTestDatabase database = new();
        await database.ActivateAsync();
        AuthoringRunRecord first = await database.Store.CreateRunAsync(
            "test",
            [new("FHIR-1", "ticket", "revision-1")],
            runId: "rollback-revalidation",
            inputProvenance:
            [
                new("jira", null, 10),
            ]);
        MarkAsInitialRevalidation(database, first.Id);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Store.ReplaceRevalidationRunAsync(
                "test",
                first.Id,
                [new("FHIR-1", "ticket", "revision-2")],
                inputProvenance:
                [
                    new("jira", null, 11),
                    new("JIRA", null, 11),
                ]));

        AuthoringProcessorModeRecord mode =
            await database.Store.GetProcessorModeAsync("test");
        Assert.Equal(first.Id, mode.RevalidationRunId);
        Assert.Equal(
            AuthoringStatusValues.Runs.Queued,
            (await database.Store.GetRunAsync(first.Id))!.Status);
        Assert.Equal(
            1,
            database.Scalar<int>("SELECT COUNT(*) FROM authoring_runs"));
        Assert.Equal(
            0,
            database.Scalar<int>(
                "SELECT COUNT(*) FROM authoring_revalidation_lineage"));
        Assert.Equal(
            10,
            Assert.Single(
                await database.Store.GetRunInputProvenanceAsync(first.Id))
                .ContentRevision);
    }

    private static AuthoringRunStore CreateReadOnlyRunStore(AuthoringTestDatabase database)
        => new(() =>
        {
            SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = database.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }, retryPolicy: database.RetryPolicy);

    private static async Task<ReadySourceRun> CreateReadySourceRunAsync(
        AuthoringTestDatabase database,
        AuthoringSnapshotPublicationProof? publicationProof = null)
    {
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await database.CreateRunningRunAsync(databaseOnly: false);
        AuthoringOperationClaim claim =
            Assert.IsType<AuthoringOperationClaim>(
                await database.Store.ClaimItemAsync(run.Id, item.Id));
        AuthoringReceiptAcceptance receipt =
            await database.Store.AcceptResultAsync(
                new AuthoringResultSubmission(
                    run.Id,
                    item.Id,
                    claim.OperationId,
                    item.ExpectedSourceRevision,
                    AuthoringResultHasher.HashNormalizedUtf8("payload")),
                claim.OperationToken);
        await database.Store.MarkItemCompleteAsync(
            item.Id,
            receipt.Receipt.ReceiptId);
        await database.Store.MarkRunFinalizingAsync(run.Id);
        string outputDirectory = Path.Combine(
            Path.GetDirectoryName(database.DatabasePath)!,
            "snapshots");
        AuthoringSnapshotDescriptor descriptor =
            await new SqliteReviewSnapshotWriter(
                database.OpenConnection,
                database.Store).WriteAsync(
                new SqliteReviewSnapshotRequest(
                    "test",
                    run.Id,
                    outputDirectory,
                    1,
                    1,
                    1,
                    new Dictionary<string, long>(),
                    AuthoringSnapshotSanitizer.CreateCore(),
                    publicationProof));
        await database.Store.CompleteRunAsync(
            run.Id,
            descriptor.SnapshotId);
        return new ReadySourceRun(
            (await database.Store.GetRunAsync(run.Id))!,
            item,
            receipt.Receipt,
            descriptor);
    }

    private sealed record ReadySourceRun(
        AuthoringRunRecord Run,
        AuthoringRunItemRecord Item,
        AuthoringResultReceipt Receipt,
        AuthoringSnapshotDescriptor Descriptor);

    private static void DropIndexesForColumns(
        AuthoringTestDatabase database,
        string tableName,
        IReadOnlyCollection<string> columns)
    {
        using SqliteConnection connection = database.OpenConnection();
        List<string> tableIndexes = [];
        using (SqliteCommand list = connection.CreateCommand())
        {
            list.CommandText = $"PRAGMA index_list('{tableName}')";
            using SqliteDataReader indexReader = list.ExecuteReader();
            while (indexReader.Read())
            {
                tableIndexes.Add(indexReader.GetString(1));
            }
        }

        List<string> indexes = [];
        foreach (string indexName in tableIndexes)
        {
            using SqliteCommand info = connection.CreateCommand();
            info.CommandText =
                $"PRAGMA index_info('{indexName.Replace("'", "''", StringComparison.Ordinal)}')";
            using SqliteDataReader columnReader = info.ExecuteReader();
            while (columnReader.Read())
            {
                if (columns.Contains(
                        columnReader.GetString(2),
                        StringComparer.OrdinalIgnoreCase))
                {
                    indexes.Add(indexName);
                    break;
                }
            }
        }

        foreach (string indexName in indexes)
        {
            using SqliteCommand drop = connection.CreateCommand();
            drop.CommandText =
                $"DROP INDEX \"{indexName.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            drop.ExecuteNonQuery();
        }
    }

    private static void MarkAsInitialRevalidation(
        AuthoringTestDatabase database,
        string runId)
        => database.Execute(
            """
            UPDATE authoring_processor_modes
            SET RevalidationRequired = 1,
                RevalidationRunId = @runId
            WHERE ProcessorKind = 'test'
            """,
            ("@runId", runId));
}

internal sealed class AuthoringTestDatabase : IDisposable
{
    private readonly string _directory;
    private readonly string _connectionString;

    public AuthoringTestDatabase(ProcessingServiceOptions? options = null)
    {
        _directory = Path.Combine(Path.GetTempPath(), $"fhir-augury-authoring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        DatabasePath = Path.Combine(_directory, "authoring.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        RetryPolicy = new AuthoringRetryPolicy(
            Options.Create(options ?? new ProcessingServiceOptions()));
        Store = new AuthoringRunStore(OpenConnection, retryPolicy: RetryPolicy);
        Store.Initialize();
    }

    public string DatabasePath { get; }
    public AuthoringRetryPolicy RetryPolicy { get; }
    public AuthoringRunStore Store { get; }

    public SqliteConnection OpenConnection()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public async Task ActivateAsync()
    {
        await Store.EnsureProcessorModeAsync("test");
        AuthoringProcessorModeRecord mode = await Store.GetProcessorModeAsync("test");
        if (mode.Mode == AuthoringStatusValues.ProcessorModes.Legacy)
        {
            await Store.TransitionProcessorModeAsync(
                "test",
                AuthoringStatusValues.ProcessorModes.Legacy,
                AuthoringStatusValues.ProcessorModes.CuttingOver);
            await Store.TransitionProcessorModeAsync(
                "test",
                AuthoringStatusValues.ProcessorModes.CuttingOver,
                AuthoringStatusValues.ProcessorModes.RunBacked);
        }
    }

    public async Task<(AuthoringRunRecord Run, AuthoringRunItemRecord Item)> CreateRunningRunAsync(
        bool databaseOnly = false)
    {
        await ActivateAsync();
        AuthoringRunRecord run = await Store.CreateRunAsync(
            "test",
            [new("FHIR-1", "ticket", "revision-1")],
            databaseOnly);
        Assert.True(await Store.TryAcquireMutationFenceAsync("test", run.Id));
        AuthoringRunItemRecord item = Assert.Single(await Store.GetRunItemsAsync(run.Id));
        return (run, item);
    }

    public T Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
