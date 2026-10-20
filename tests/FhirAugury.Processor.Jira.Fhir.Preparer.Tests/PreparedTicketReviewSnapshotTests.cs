using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Common.Hosting;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Api;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Controllers;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

[Collection(PreparedTicketPublicationTestCollection.Name)]
public sealed class PreparedTicketReviewSnapshotTests
{
    [Fact]
    public async Task EnrichmentSnapshot_PreservesAuthoredRelationshipsAndPublicSchemaV3()
    {
        using PreparedTicketPublicationTestFixture.Fixture fixture = new(richGraph: true);
        PreparedTicketPublicationTestFixture.SourceResult source =
            await fixture.CreateSourceRunAsync("FHIR-10028", "FHIR-29212");
        string originalPath = Path.Combine(fixture.SnapshotDirectory, source.Descriptor.FileName);
        byte[] originalHash = SHA256.HashData(await File.ReadAllBytesAsync(originalPath));
        PreparedTicketPublicationTestFixture.MetadataFetcher fetcher =
            PreparedTicketPublicationTestFixture.MetadataFetcher.For(source.ExpectedRevisions);
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(fetcher);
        PreparedTicketPublicationRefreshResult admitted = await service.StartAsync(source.Run.Id);

        AuthoringSnapshotDescriptor descriptor = Assert.IsType<AuthoringSnapshotDescriptor>(
            await fixture.CreatePostProcessor(service).FinalizeRunAsync(admitted.Run.RunId));

        Assert.Equal(PreparedTicketSnapshotSchemaV3.Version, descriptor.SchemaVersion);
        Assert.Equal(PreparedTicketPublicationContract.CurrentVersion, descriptor.PublicationProof!.ContractVersion);
        Assert.NotEqual(source.Descriptor.SnapshotId, descriptor.SnapshotId);
        Assert.True(descriptor.Sequence > source.Descriptor.Sequence);
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(originalPath)));
        await using SqliteConnection original = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(originalPath);
        await using SqliteConnection enriched = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(
            Path.Combine(fixture.SnapshotDirectory, descriptor.FileName));
        AssertMatchesCatalog(enriched, PreparedTicketSnapshotSchemaV3.Catalog);
        foreach (string table in new[]
        {
            "prepared_tickets", "prepared_ticket_repos", "prepared_ticket_related_jira",
            "prepared_ticket_related_zulip", "prepared_ticket_related_github",
            "prepared_github_hydration", "prepared_repo_hydration", "prepared_ticket_jira_xref",
            "prepared_ticket_jira_content", "prepared_ticket_artifacts", "prepared_ticket_pages",
            "prepared_ticket_topics", "prepared_ticket_topic_groups", "prepared_ticket_topic_members",
            "authoring_result_receipts",
        })
        {
            Assert.Equal(
                ReadSnapshotRows(original, $"SELECT * FROM {table} ORDER BY RowId"),
                ReadSnapshotRows(enriched, $"SELECT * FROM {table} ORDER BY RowId"));
        }
        Assert.Equal(
            ReadSnapshotRows(original, "SELECT * FROM prepared_ticket_partition_receipts ORDER BY RunId, PartitionKey"),
            ReadSnapshotRows(enriched, "SELECT * FROM prepared_ticket_partition_receipts ORDER BY RunId, PartitionKey"));
        Assert.Equal(
            ReadSnapshotRows(original, $"SELECT * FROM authoring_run_items WHERE RunId = '{source.Run.Id}' ORDER BY RowId"),
            ReadSnapshotRows(enriched, $"SELECT * FROM authoring_run_items WHERE RunId = '{source.Run.Id}' ORDER BY RowId"));
        Assert.Equal(
            ReadSnapshotRows(original, $"SELECT * FROM authoring_run_input_provenance WHERE RunId = '{source.Run.Id}' ORDER BY RowId"),
            ReadSnapshotRows(enriched, $"SELECT * FROM authoring_run_input_provenance WHERE RunId = '{source.Run.Id}' ORDER BY RowId"));
        Assert.Equal(
            ReadSnapshotRows(original, "SELECT * FROM prepared_jira_hydration WHERE TicketKey <> JiraKey ORDER BY RowId"),
            ReadSnapshotRows(enriched, "SELECT * FROM prepared_jira_hydration WHERE TicketKey <> JiraKey ORDER BY RowId"));
        Assert.Equal(
            "2002-02-02T00:00:00.0000000+00:00",
            Scalar<string>(original, "SELECT UpdatedAt FROM prepared_jira_hydration WHERE TicketKey = 'FHIR-10028' AND JiraKey = TicketKey"));
        Assert.Equal(
            "2026-09-01T00:00:00.0000000+00:00",
            Scalar<string>(enriched, "SELECT UpdatedAt FROM prepared_jira_hydration WHERE TicketKey = 'FHIR-10028' AND JiraKey = TicketKey"));
        Assert.Equal("Reporter FHIR-10028", Scalar<string>(enriched,
            "SELECT Reporter FROM prepared_ticket_hydration WHERE TicketKey = 'FHIR-10028'"));
        Assert.Equal("Requester FHIR-10028", Scalar<string>(enriched,
            "SELECT DisplayName FROM prepared_ticket_in_person_requesters WHERE TicketKey = 'FHIR-10028'"));
        Assert.Equal("Missing hydration reason FHIR-10028", Scalar<string>(enriched,
            "SELECT Justification FROM prepared_ticket_related_zulip WHERE TicketKey = 'FHIR-10028' AND ZulipThreadId = '12345'"));
        Assert.EndsWith("/near/12345", Scalar<string>(enriched,
            "SELECT Url FROM prepared_zulip_hydration WHERE TicketKey = 'FHIR-10028' AND ZulipThreadId = '12345'"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnrichmentSnapshot_RechecksActualBackupAfterLivePreflight()
    {
        using PreparedTicketPublicationTestFixture.Fixture fixture = new(richGraph: true);
        PreparedTicketPublicationTestFixture.SourceResult source =
            await fixture.CreateSourceRunAsync("FHIR-10028", "FHIR-29212");
        string originalPath = Path.Combine(fixture.SnapshotDirectory, source.Descriptor.FileName);
        byte[] originalHash = SHA256.HashData(await File.ReadAllBytesAsync(originalPath));
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(
            PreparedTicketPublicationTestFixture.MetadataFetcher.For(source.ExpectedRevisions));
        PreparedTicketPublicationRefreshResult admitted = await service.StartAsync(source.Run.Id);
        fixture.Execute(
            $"""
            CREATE TRIGGER enrichment_snapshot_drift AFTER INSERT ON authoring_review_snapshots
            WHEN NEW.RunId = '{admitted.Run.RunId}'
            BEGIN
                UPDATE prepared_jira_hydration SET DescriptionHtml = 'changed between preflight and backup'
                WHERE TicketKey = 'FHIR-10028' AND JiraKey = TicketKey;
            END
            """);
        PreparedTicketPublicationTestFixture.CountingGroupingDispatcher grouping = new(fixture.Database);

        Assert.Null(await fixture.CreatePostProcessor(service, grouping).FinalizeRunAsync(admitted.Run.RunId));

        Assert.Equal(AuthoringStatusValues.Runs.Superseded, (await fixture.Store.GetRunAsync(admitted.Run.RunId))!.Status);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.Equal(0, grouping.CallCount);
        Assert.Equal(2, fixture.CountLive("authoring_run_attempts"));
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(originalPath)));
        AuthoringReviewSnapshotRecord rejected = Assert.Single(
            await fixture.Store.GetSnapshotRecordsAsync(), record => record.RunId == admitted.Run.RunId);
        Assert.Equal(AuthoringStatusValues.Snapshots.Error, rejected.Status);
        Assert.False(File.Exists(rejected.Path));
        Assert.False(File.Exists(rejected.TempPath));
    }

    private static string ReadSnapshotRows(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        List<object?[]> rows = [];
        while (reader.Read())
        {
            rows.Add(Enumerable.Range(0, reader.FieldCount).Select(index =>
                reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
        }
        return JsonSerializer.Serialize(rows);
    }

    [Theory]
    [InlineData(PreparedTicketSnapshotSchemaV1.Version)]
    [InlineData(PreparedTicketSnapshotSchemaV2.Version)]
    [InlineData(PreparedTicketSnapshotSchemaV3.Version)]
    public async Task EnrichmentAcceptsLegacySourceSchemasButAlwaysProducesSchemaV3(int sourceSchema)
    {
        using PreparedTicketPublicationTestFixture.Fixture fixture = new(richGraph: true, schemaVersion: sourceSchema);
        PreparedTicketPublicationTestFixture.SourceResult source =
            await fixture.CreateSourceRunAsync("FHIR-10028", "FHIR-29212");
        Assert.Equal(sourceSchema, source.Descriptor.SchemaVersion);
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(
            PreparedTicketPublicationTestFixture.MetadataFetcher.For(source.ExpectedRevisions));
        PreparedTicketPublicationRefreshResult admitted = await service.StartAsync(source.Run.Id);

        AuthoringSnapshotDescriptor result = Assert.IsType<AuthoringSnapshotDescriptor>(
            await fixture.CreatePostProcessor(service, snapshotSchemaVersion: PreparedTicketSnapshotSchemaV3.Version)
                .FinalizeRunAsync(admitted.Run.RunId));

        Assert.Equal(PreparedTicketSnapshotSchemaV3.Version, result.SchemaVersion);
        Assert.Equal(PreparedTicketPublicationContract.CurrentVersion, result.PublicationProof!.ContractVersion);
        Assert.Equal(source.Run.Id, result.PublicationProof.SourceRunId);
    }

    [Fact]
    public void PublicationContractFingerprintsAreCanonicalAndVersioned()
    {
        PreparedTicketPublicationCorpusItem[] corpus =
        [
            new(
                "FHIR-10",
                "receipt-a",
                "item-a",
                "run-a",
                "ticket",
                "rev-a"),
            new(
                "BALLOT-7",
                "receipt-b",
                "item-b",
                "run-b",
                "ticket",
                "rev-b"),
        ];
        const string expectedCorpus =
            "33d5fd635c5c11f6a09e1e42ae8bbb7cc13d2c55cf57e522d063e8078bc3eb28";
        Assert.Equal(
            expectedCorpus,
            PreparedTicketPublicationContract.ComputeCorpusFingerprint(
                corpus));
        Assert.Equal(
            expectedCorpus,
            PreparedTicketPublicationContract.ComputeCorpusFingerprint(
                corpus.Reverse()));
        const string expectedRefreshInput =
            "913e617484f73490a51c69b21db3c28a577d450ecb65884755eb85e3b1d1fe87";
        Assert.Equal(
            expectedRefreshInput,
            PreparedTicketPublicationContract
                .ComputePublicationRefreshInputFingerprint(
                    "source-run",
                    corpus));
        Assert.Equal(
            expectedRefreshInput,
            PreparedTicketPublicationContract
                .ComputePublicationRefreshInputFingerprint(
                    "source-run",
                    corpus.Reverse()));

        PreparedTicketGroupingPayload grouping = new()
        {
            WorkGroupClean = "FHIRInfrastructure",
            WorkGroupDisplay = "FHIR Infrastructure",
            Specification = "FHIR",
            Type = "Change Request",
            Topics =
            [
                new PreparedTicketTopicPayload
                {
                    ShortDescription = "Align the renderer",
                    LongerDescription =
                        "Keep one canonical grouping graph.",
                    RenderOrderHint = 3,
                    LinkedTicketGroups =
                    [
                        new PreparedTicketTopicGroupPayload
                        {
                            FirstTicketKey = "FHIR-10",
                            Rationale = "Discuss together.",
                            Members =
                            [
                                new()
                                {
                                    TicketKey = "BALLOT-7",
                                    Order = 1,
                                },
                                new()
                                {
                                    TicketKey = "FHIR-10",
                                    Order = 0,
                                },
                            ],
                        },
                    ],
                    RemainingTicketKeys = [],
                },
            ],
        };
        const string expectedOutput =
            "4d074b51c17e2a81e187ff226b637b90c908246d111f60ce8c5ec881cf8338f8";
        Assert.Equal(
            expectedOutput,
            PreparedTicketPublicationContract
                .ComputeGroupingPartitionFingerprint(grouping));

        string aggregate =
            PreparedTicketPublicationContract.ComputeGroupingFingerprint(
            [
                new PreparedTicketPublicationGroupingPartition(
                    "FHIRInfrastructure\u001fFHIR\u001fChange Request",
                    expectedOutput),
            ]);
        Assert.Equal(
            "380409171000bf647b7c626e6366c7360eae7ebe34785da77c23967afdb1852b",
            aggregate);

        Assert.Throws<NotSupportedException>(
            () => PreparedTicketPublicationContract
                .ComputeCorpusFingerprint(corpus, contractVersion: 99));
        Assert.NotEqual(
            expectedCorpus,
            PreparedTicketPublicationContract.ComputeCorpusFingerprint(
                corpus.Select(item => item.TicketKey == "FHIR-10"
                    ? item with { ExpectedSourceRevision = "rev-changed" }
                    : item)));
    }

    [Fact]
    public async Task FinalizeRun_ProducesSecretFreeCanonicalSnapshot()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, AuthoringRunItemRecord item) =
            await fixture.CreateCompletedRunAsync(databaseOnly: false);
        PreparedTicketRunPostProcessor postProcessor = fixture.CreatePostProcessor();

        AuthoringSnapshotDescriptor descriptor =
            (await postProcessor.FinalizeRunAsync(run.Id))!;

        Assert.Equal(PreparedTicketSnapshotSchemaV1.Version, descriptor.SchemaVersion);
        Assert.Equal(
            AuthoringStatusValues.Runs.Completed,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
        Assert.Equal(1, descriptor.TableCounts["prepared_ticket_partition_receipts"]);
        string snapshotPath = Path.Combine(fixture.SnapshotDirectory, descriptor.FileName);
        using SqliteConnection snapshot = OpenReadOnly(snapshotPath);
        AssertMatchesCatalog(
            snapshot,
            PreparedTicketSnapshotSchemaV1.Catalog);
        Assert.Equal("ok", Scalar<string>(snapshot, "PRAGMA integrity_check"));
        Assert.Equal(
            0,
            Scalar<int>(
                snapshot,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('authoring_run_attempts', 'authoring_processor_modes', 'prepared_ticket_authoring_state')"));
        Assert.Equal(
            "<p>request</p>",
            Scalar<string>(
                snapshot,
                "SELECT DescriptionHtml FROM prepared_ticket_jira_content WHERE TicketKey = 'FHIR-1'"));
        Assert.Equal(
            "Patient",
            Scalar<string>(
                snapshot,
                "SELECT Value FROM prepared_ticket_artifacts WHERE TicketKey = 'FHIR-1'"));
        Assert.Equal(
            "patient.html",
            Scalar<string>(
                snapshot,
                "SELECT Value FROM prepared_ticket_pages WHERE TicketKey = 'FHIR-1'"));
        Assert.Equal(1, Scalar<int>(snapshot, "SELECT COUNT(*) FROM prepared_ticket_partition_receipts"));
        Assert.Equal(1, Scalar<int>(snapshot, "SELECT COUNT(*) FROM jira_review_workgroups"));
        Assert.Equal(item.Id, Scalar<string>(snapshot, "SELECT Id FROM authoring_run_items"));
        Assert.Equal(
            0,
            Scalar<int>(
                snapshot,
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table'
                  AND name IN (
                      'authoring_run_input_provenance',
                      'prepared_ticket_in_person_requesters')
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                snapshot,
                """
                SELECT COUNT(*)
                FROM pragma_table_info('prepared_ticket_hydration')
                WHERE name IN (
                    'Assignee',
                    'SourceProject',
                    'SourceLastSuccessfulRefreshAt',
                    'SourceContentRevision',
                    'PublicDisplayNamePolicyVersion')
                """));
    }

    [Fact]
    public async Task FinalizeRun_BlocksSnapshotUntilLegacyRowsAreRevalidated()
    {
        using Fixture fixture = new(activate: false);
        await fixture.Database.SavePreparedTicketAsync(CreatePayload("FHIR-999"));
        await fixture.Database.ClassifyLegacyPreparedTicketsAsync();
        await fixture.ActivateAsync();
        (AuthoringRunRecord run, _) = await fixture.CreateCompletedRunAsync(databaseOnly: false);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.CreatePostProcessor().FinalizeRunAsync(run.Id));

        Assert.Contains("legacy revalidation", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            AuthoringStatusValues.Runs.Superseded,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task FinalizeRun_ResumesRunAlreadyMarkedFinalizing()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: true);
        await fixture.AuthoringStore.MarkRunFinalizingAsync(run.Id);

        AuthoringSnapshotDescriptor? descriptor =
            await fixture.CreatePostProcessor().FinalizeRunAsync(run.Id);

        Assert.Null(descriptor);
        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task FinalizeRun_AllSupersededItemsCompletesNormally()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateSupersededRunAsync(databaseOnly: true);

        AuthoringSnapshotDescriptor? descriptor =
            await fixture.CreatePostProcessor().FinalizeRunAsync(run.Id);

        Assert.Null(descriptor);
        Assert.Equal(
            AuthoringStatusValues.Runs.CompletedDatabaseOnly,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
        Assert.Null(await fixture.AuthoringStore.GetFencedRunAsync("jira-fhir"));
    }

    [Fact]
    public async Task FinalizeRun_InitialRevalidationRetiresSupersededLegacyRows()
    {
        using Fixture fixture = new(activate: false);
        await fixture.Database.SavePreparedTicketAsync(CreatePayload("FHIR-1"));
        await fixture.Database.ClassifyLegacyPreparedTicketsAsync();
        await fixture.ActivateAsync();
        (AuthoringRunRecord run, _) =
            await fixture.CreateSupersededRunAsync(databaseOnly: true);
        fixture.MarkAsInitialRevalidation(run.Id);

        await fixture.CreatePostProcessor().FinalizeRunAsync(run.Id);

        Assert.Equal(0, await fixture.Database.CountLegacyUnverifiedAsync());
        AuthoringProcessorModeRecord mode =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        Assert.False(mode.RevalidationRequired);
        using SqliteConnection connection = fixture.Database.OpenConnection();
        Assert.Equal(
            "superseded",
            Scalar<string>(
                connection,
                "SELECT Classification FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
    }

    [Fact]
    public async Task InitialRevalidation_SupersededPredecessorAndLaterReplacement_CompletesCutover()
    {
        using Fixture fixture = new(activate: false);
        await fixture.Database.SavePreparedTicketAsync(CreatePayload("FHIR-1"));
        await fixture.Database.SavePreparedTicketAsync(CreatePayload("FHIR-2"));
        await fixture.Database.SavePreparedTicketAsync(CreatePayload("FHIR-3"));
        Assert.Equal(
            3,
            await fixture.Database.ClassifyLegacyPreparedTicketsAsync());
        await fixture.ActivateAsync();
        DateTimeOffset firstRevision =
            new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        JiraProcessingSourceTicketRecord sourceA =
            await fixture.SeedSourceAsync("FHIR-1", firstRevision);
        JiraProcessingSourceTicketRecord sourceB =
            await fixture.SeedSourceAsync("FHIR-2", firstRevision);
        JiraProcessingSourceTicketRecord sourceC =
            await fixture.SeedSourceAsync("FHIR-3", firstRevision);
        JiraAuthoringRunCreation initial =
            await fixture.CreateRunAsync(
                [sourceA, sourceB, sourceC],
                databaseOnly: true);
        fixture.MarkAsInitialRevalidation(initial.Run.Id);
        AuthoringRunItemRecord itemA =
            initial.Items.Single(item => item.BusinessKey == "FHIR-1");
        AuthoringRunItemRecord itemC =
            initial.Items.Single(item => item.BusinessKey == "FHIR-3");
        AuthoringOperationClaim claimA =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    initial.Run.Id,
                    itemA.Id));
        await fixture.AuthoringStore.MarkClaimErrorAsync(
            itemA.Id,
            claimA.OperationId,
            "not actionable");
        await fixture.AuthoringStore.SupersedeErroredItemAsync(
            initial.Run.Id,
            itemA.Id,
            "not actionable");
        await fixture.CompleteItemAsync(
            initial.Run,
            itemC,
            CreatePayload("FHIR-3"));

        string predecessorReceiptHash;
        string predecessorOperationId;
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        {
            predecessorReceiptHash = Scalar<string>(
                connection,
                "SELECT ReceiptContentHash FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-3'");
            predecessorOperationId = Scalar<string>(
                connection,
                "SELECT OperationId FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-3'");
        }

        await fixture.SeedSourceAsync(
            "FHIR-2",
            firstRevision.AddDays(1),
            title: "Updated B");
        Assert.True(await fixture.ReplaceStaleInitialRunAsync(initial.Run.Id));
        AuthoringProcessorModeRecord replacementMode =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        AuthoringRunRecord replacement = Assert.IsType<AuthoringRunRecord>(
            await fixture.AuthoringStore.GetRunAsync(
                replacementMode.RevalidationRunId!));
        AuthoringRunItemRecord replacementB = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(replacement.Id));
        Assert.Equal("FHIR-2", replacementB.BusinessKey);
        AuthoringOperationClaim replacementClaim =
            Assert.IsType<AuthoringOperationClaim>(
                await fixture.AuthoringStore.ClaimItemAsync(
                    replacement.Id,
                    replacementB.Id));
        await fixture.AuthoringStore.MarkClaimErrorAsync(
            replacementB.Id,
            replacementClaim.OperationId,
            "not actionable");
        await fixture.AuthoringStore.SupersedeErroredItemAsync(
            replacement.Id,
            replacementB.Id,
            "not actionable");
        IReadOnlyList<AuthoringRunItemRecord> terminalLineage =
            await fixture.AuthoringStore.GetRevalidationSupersededItemsAsync(
                replacement.Id);
        string expectedFingerprint = AuthoringResultHasher.HashNormalizedUtf8(
            string.Join(
                "\n",
                terminalLineage.Select(item =>
                    $"{item.RunId}:{item.Id}:{item.BusinessKey}:{item.ItemKind}:{item.ExpectedSourceRevision}")));

        AuthoringSnapshotDescriptor? descriptor =
            await fixture.CreatePostProcessor().FinalizeRunAsync(
                replacement.Id);

        Assert.NotNull(descriptor);
        Assert.Equal(
            AuthoringStatusValues.Runs.Completed,
            (await fixture.AuthoringStore.GetRunAsync(replacement.Id))!.Status);
        Assert.Null(
            await fixture.AuthoringStore.GetFencedRunAsync("jira-fhir"));
        AuthoringProcessorModeRecord completedMode =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        Assert.False(completedMode.RevalidationRequired);
        Assert.Null(completedMode.RevalidationRunId);
        AuthoringRunStageRecord retirementStage = Assert.Single(
            await fixture.AuthoringStore.GetRunStagesAsync(replacement.Id),
            stage => stage.StageName == "revalidation-retirement");
        Assert.Equal(expectedFingerprint, retirementStage.InputFingerprint);
        Assert.Contains(
            itemA.Id,
            string.Join(
                "\n",
                terminalLineage.Select(item =>
                    $"{item.RunId}:{item.Id}:{item.BusinessKey}:{item.ItemKind}:{item.ExpectedSourceRevision}")),
            StringComparison.Ordinal);
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        {
            Assert.Equal(
                "superseded",
                Scalar<string>(
                    connection,
                    "SELECT Classification FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
            Assert.Equal(
                initial.Run.Id,
                Scalar<string>(
                    connection,
                    "SELECT RunId FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
            Assert.Equal(
                itemA.Id,
                Scalar<string>(
                    connection,
                    "SELECT RunItemId FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-1'"));
            Assert.Equal(
                "receipt-backed",
                Scalar<string>(
                    connection,
                    "SELECT Classification FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-3'"));
            Assert.Equal(
                predecessorReceiptHash,
                Scalar<string>(
                    connection,
                    "SELECT ReceiptContentHash FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-3'"));
            Assert.Equal(
                predecessorOperationId,
                Scalar<string>(
                    connection,
                    "SELECT OperationId FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-3'"));
        }
    }

    [Fact]
    public async Task FinalizeRun_ReplacesInitialRevalidationChangedDuringStages()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: false);
        fixture.MarkAsInitialRevalidation(run.Id);

        AuthoringConflictException conflict =
            await Assert.ThrowsAsync<AuthoringConflictException>(() =>
                fixture.CreatePostProcessor(
                    new SourceChangingGroupingDispatcher(
                        fixture.Database,
                        fixture.AdvanceSourceRevisionAsync))
                    .FinalizeRunAsync(run.Id));

        Assert.Equal(AuthoringConflictCode.SourceRevisionMismatch, conflict.Code);
        Assert.Equal(
            AuthoringStatusValues.Runs.Superseded,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
        AuthoringProcessorModeRecord mode =
            await fixture.AuthoringStore.GetProcessorModeAsync("jira-fhir");
        Assert.True(mode.RevalidationRequired);
        Assert.NotEqual(run.Id, mode.RevalidationRunId);
        AuthoringRunItemRecord replacement = Assert.Single(
            await fixture.AuthoringStore.GetRunItemsAsync(mode.RevalidationRunId!));
        Assert.Equal(
            new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero).ToString("O"),
            replacement.ExpectedSourceRevision);
    }

    [Fact]
    public async Task FinalizeRun_RejectsTamperedReceiptProvenance()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: false);
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE authoring_result_receipts
                SET ObservedSourceRevision = 'forged'
                WHERE RunId = @runId
                """;
            command.Parameters.AddWithValue("@runId", run.Id);
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.CreatePostProcessor().FinalizeRunAsync(run.Id));

        Assert.Contains(
            "valid current receipt provenance",
            error.Message,
            StringComparison.Ordinal);
        Assert.Equal(
            AuthoringStatusValues.Runs.Running,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
    }

    [Fact]
    public async Task ReplacementRunPartitionsIncludeRetainedPredecessorTickets()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: false);
        fixture.MarkAsInitialRevalidation(run.Id);
        AuthoringRunRecord replacement =
            await fixture.AuthoringStore.ReplaceRevalidationRunAsync(
                "jira-fhir",
                run.Id,
                [new("FHIR-2", "fhir", "revision-2")]);

        PreparedTicketRunPartition partition = Assert.Single(
            await fixture.Database.GetRunPartitionsAsync(replacement.Id));

        Assert.Equal("FHIRInfrastructure", partition.WorkGroupClean);
        Assert.Equal(["FHIR-1"], partition.TicketKeys);
    }

    [Fact]
    public async Task FinalizeRun_RefusesGroupingWithoutDurableReceipt()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: true);

        InvalidOperationException error =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.CreatePostProcessor(
                    new NoOpGroupingDispatcher()).FinalizeRunAsync(run.Id));

        Assert.Contains("durable receipt", error.Message, StringComparison.Ordinal);
        AuthoringRunStageRecord grouping = Assert.Single(
            await fixture.AuthoringStore.GetRunStagesAsync(run.Id),
            stage => stage.StageName == "grouping");
        Assert.Equal(AuthoringStatusValues.Stages.Error, grouping.Status);
    }

    [Fact]
    public void PreviewGroupingDispatcher_UsesSupportedPromptInvocation()
    {
        PreviewPreparedTicketGroupingDispatcher dispatcher = new(
            Options.Create(new PreparerServiceOptions()),
            NullLogger<PreviewPreparedTicketGroupingDispatcher>.Instance);
        PreparedTicketRunPartition partition = new(
            "FHIRInfrastructure",
            "FHIR Infrastructure",
            "FHIR",
            "Change Request",
            PreparerDatabase.GetPartitionKey(
                "FHIRInfrastructure",
                "FHIR",
                "Change Request"),
            "fingerprint",
            ["FHIR-1"]);

        System.Diagnostics.ProcessStartInfo startInfo =
            dispatcher.CreateStartInfo(
                "run-1",
                partition,
                new AuthoringRunStageLease("stage-1", "lease-1", 1));

        Assert.Equal(
            ["-p", "/topic-groupings", "--allow-all"],
            startInfo.ArgumentList);
        Assert.Equal(
            "1",
            startInfo.Environment["FHIR_AUGURY_GROUPING_WORKER"]);
        Assert.Equal(
            "http://localhost:5171",
            startInfo.Environment["FHIR_AUGURY_GROUPING_PROCESSOR_URL"]);
    }

    [Fact]
    public async Task RunScopedGroupingEndpointReturnsMatchingDurableReceipt()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: true);
        PreparedTicketRunPartition partition = Assert.Single(
            await fixture.Database.GetRunPartitionsAsync(run.Id));
        AuthoringRunStageRecord stage =
            await fixture.AuthoringStore.EnsureRunStageAsync(
                run.Id,
                "grouping",
                partition.PartitionKey,
                partition.InputFingerprint);
        AuthoringRunStageLease lease = Assert.IsType<AuthoringRunStageLease>(
            await fixture.AuthoringStore.TryStartRunStageAsync(stage.Id));
        PreparedTicketGroupingsController controller =
            new(fixture.Database, fixture.AuthoringStore);

        ActionResult<PreparedTicketGroupingSaveResultDto> result =
            await controller.PutPartition(
                partition.WorkGroupClean,
                partition.Specification,
                partition.Type,
                new PreparedTicketGroupingPutRequest(
                    partition.WorkGroupDisplay,
                    [],
                    new PreparedTicketGroupingStageContext(
                        run.Id,
                        stage.Id,
                        lease.LeaseId,
                        partition.InputFingerprint)),
                CancellationToken.None);

        PreparedTicketGroupingSaveResultDto body =
            Assert.IsType<PreparedTicketGroupingSaveResultDto>(
                Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(run.Id, body.AuthoringReceipt!.RunId);
        Assert.Equal(stage.Id, body.AuthoringReceipt.StageId);
        Assert.Equal(partition.PartitionKey, body.AuthoringReceipt.PartitionKey);
        Assert.Equal(
            partition.InputFingerprint,
            body.AuthoringReceipt.InputFingerprint);
    }

    [Fact]
    public async Task SnapshotFiltering_ReclassifiesSurvivorFromInvalidLinkedGroup()
    {
        using Fixture fixture = new(activate: false);
        await fixture.Database.SavePreparedTicketAsync(CreatePayload("FHIR-2"));
        using (SqliteConnection cleanup = fixture.Database.OpenConnection())
        using (SqliteCommand clearState = cleanup.CreateCommand())
        {
            clearState.CommandText =
                "DELETE FROM prepared_ticket_authoring_state WHERE TicketKey = 'FHIR-2'";
            clearState.ExecuteNonQuery();
        }
        await fixture.ActivateAsync();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: false);
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO prepared_ticket_topics(
                    Id, WorkGroupClean, WorkGroupDisplay, Specification, Type,
                    ShortDescription, LongerDescription, RenderOrderHint, SavedAt)
                VALUES(
                    'topic-1', 'Other', 'Other', 'Other',
                    'Other', 'Topic', 'Topic', NULL, @at);
                INSERT INTO prepared_ticket_topic_groups(
                    Id, TopicRowId, FirstTicketKey, Rationale, OrderInTopic, SavedAt)
                SELECT 'group-1', RowId, 'FHIR-2', 'linked', 0, @at
                FROM prepared_ticket_topics WHERE Id = 'topic-1';
                INSERT INTO prepared_ticket_topic_members(
                    Id, TopicRowId, TopicGroupRowId, TicketKey, OrderInContainer)
                SELECT 'member-1', t.RowId, g.RowId, 'FHIR-2', 0
                FROM prepared_ticket_topics t, prepared_ticket_topic_groups g
                WHERE t.Id = 'topic-1' AND g.Id = 'group-1';
                INSERT INTO prepared_ticket_topic_members(
                    Id, TopicRowId, TopicGroupRowId, TicketKey, OrderInContainer)
                SELECT 'member-2', t.RowId, g.RowId, 'FHIR-1', 1
                FROM prepared_ticket_topics t, prepared_ticket_topic_groups g
                WHERE t.Id = 'topic-1' AND g.Id = 'group-1';
                """;
            command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        AuthoringSnapshotDescriptor descriptor =
            (await fixture.CreatePostProcessor().FinalizeRunAsync(run.Id))!;
        using SqliteConnection snapshot = OpenReadOnly(
            Path.Combine(fixture.SnapshotDirectory, descriptor.FileName));

        Assert.Equal(1, Scalar<int>(snapshot, "SELECT COUNT(*) FROM prepared_tickets"));
        Assert.Equal(0, Scalar<int>(snapshot, "SELECT COUNT(*) FROM prepared_ticket_topic_groups"));
        Assert.Equal(0, Scalar<int>(snapshot, "SELECT COUNT(*) FROM prepared_ticket_topic_members"));
        Assert.Equal(0, Scalar<int>(snapshot, "SELECT COUNT(*) FROM prepared_ticket_topics"));
    }

    [Fact]
    public async Task FinalizeRun_EmitsV2WithEveryContributingRunProvenance()
    {
        using Fixture fixture = new();
        DateTimeOffset firstRefresh =
            new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset secondRefresh = firstRefresh.AddDays(1);
        (AuthoringRunRecord firstRun, _) =
            await fixture.CreateCompletedRunAsync(
                databaseOnly: false,
                key: "FHIR-1",
                sourceLastSuccessfulRefreshAt: firstRefresh,
                sourceContentRevision: 101);
        AuthoringSnapshotDescriptor v1Descriptor =
            (await fixture.CreatePostProcessor(
                    PreparedTicketSnapshotSchemaV1.Version)
                .FinalizeRunAsync(firstRun.Id))!;
        Assert.Equal(
            PreparedTicketSnapshotSchemaV1.Version,
            v1Descriptor.SchemaVersion);

        (AuthoringRunRecord unrelatedRun, _) =
            await fixture.CreateSupersededRunAsync(
                databaseOnly: true,
                key: "FHIR-3",
                sourceLastSuccessfulRefreshAt: secondRefresh,
                sourceContentRevision: 102);
        Assert.Null(
            await fixture.CreatePostProcessor(
                    PreparedTicketSnapshotSchemaV2.Version)
                .FinalizeRunAsync(unrelatedRun.Id));

        (AuthoringRunRecord secondRun, _) =
            await fixture.CreateCompletedRunAsync(
                databaseOnly: false,
                key: "FHIR-2",
                sourceLastSuccessfulRefreshAt: secondRefresh,
                sourceContentRevision: 102);
        AuthoringSnapshotDescriptor descriptor =
            (await fixture.CreatePostProcessor(
                    PreparedTicketSnapshotSchemaV2.Version)
                .FinalizeRunAsync(secondRun.Id))!;

        Assert.Equal(PreparedTicketSnapshotSchemaV2.Version, descriptor.SchemaVersion);
        Assert.Equal(
            2,
            descriptor.TableCounts[
                "prepared_ticket_in_person_requesters"]);
        using SqliteConnection snapshot = OpenReadOnly(
            Path.Combine(fixture.SnapshotDirectory, descriptor.FileName));
        AssertMatchesCatalog(
            snapshot,
            PreparedTicketSnapshotSchemaV2.Catalog);
        Assert.Equal(
            2,
            Scalar<int>(
                snapshot,
                "SELECT COUNT(*) FROM prepared_tickets"));
        Assert.Equal(
            2,
            Scalar<int>(
                snapshot,
                "SELECT COUNT(*) FROM prepared_ticket_in_person_requesters"));
        Assert.Equal(
            2,
            Scalar<int>(
                snapshot,
                "SELECT COUNT(*) FROM authoring_run_input_provenance"));
        Assert.Equal(
            1,
            Scalar<int>(
                snapshot,
                $"""
                SELECT COUNT(*)
                FROM authoring_run_input_provenance
                WHERE RunId = '{firstRun.Id}'
                  AND Source = 'jira'
                  AND LatestSuccessfulRefreshAt = '{firstRefresh:O}'
                  AND ContentRevision = 101
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                snapshot,
                $"""
                SELECT COUNT(*)
                FROM authoring_run_input_provenance
                WHERE RunId = '{secondRun.Id}'
                  AND Source = 'jira'
                  AND LatestSuccessfulRefreshAt = '{secondRefresh:O}'
                  AND ContentRevision = 102
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                snapshot,
                $"""
                SELECT COUNT(*)
                FROM authoring_run_input_provenance
                WHERE RunId = '{unrelatedRun.Id}'
                """));
        Assert.Equal(
            "Grace Example",
            Scalar<string>(
                snapshot,
                """
                SELECT Assignee
                FROM prepared_ticket_hydration
                WHERE TicketKey = 'FHIR-1'
                """));
        Assert.Equal(
            "FHIR",
            Scalar<string>(
                snapshot,
                """
                SELECT SourceProject
                FROM prepared_ticket_hydration
                WHERE TicketKey = 'FHIR-1'
                """));
        Assert.Equal(
            101L,
            Scalar<long>(
                snapshot,
                """
                SELECT SourceContentRevision
                FROM prepared_ticket_hydration
                WHERE TicketKey = 'FHIR-1'
                """));
        Assert.Equal(
            0,
            Scalar<int>(
                snapshot,
                """
                SELECT COUNT(*)
                FROM (
                    SELECT name
                    FROM pragma_table_info('prepared_ticket_hydration')
                    UNION ALL
                    SELECT name
                    FROM pragma_table_info('prepared_jira_hydration')
                    UNION ALL
                    SELECT name
                    FROM pragma_table_info(
                        'prepared_ticket_in_person_requesters')
                )
                WHERE name = 'PublicDisplayNamePolicyVersion'
                """));
    }

    [Fact]
    public async Task FinalizeRun_EmitsV3WithOnlyCurrentPolicyPeople()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord firstRun, _) =
            await fixture.CreateCompletedRunAsync(
                databaseOnly: false,
                key: "FHIR-1");
        _ = await fixture.CreatePostProcessor(
                PreparedTicketSnapshotSchemaV3.Version)
            .FinalizeRunAsync(firstRun.Id);

        (AuthoringRunRecord secondRun, _) =
            await fixture.CreateCompletedRunAsync(
                databaseOnly: false,
                key: "FHIR-2");
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE prepared_ticket_hydration
                SET PublicDisplayNamePolicyVersion = @oldPolicy
                WHERE TicketKey = 'FHIR-2';
                UPDATE prepared_jira_hydration
                SET PublicDisplayNamePolicyVersion = @oldPolicy
                WHERE TicketKey = 'FHIR-2';
                UPDATE prepared_ticket_in_person_requesters
                SET PublicDisplayNamePolicyVersion = @oldPolicy
                WHERE TicketKey = 'FHIR-2';

                UPDATE prepared_ticket_hydration
                SET Assignee = 'unsafe@example.org'
                WHERE TicketKey = 'FHIR-1';
                UPDATE prepared_jira_hydration
                SET Reporter = 'Related Reporter',
                    Assignee = 'Related <related@example.org>'
                WHERE TicketKey = 'FHIR-1';
                INSERT INTO prepared_ticket_in_person_requesters(
                    TicketKey,
                    DisplayName,
                    PublicDisplayNamePolicyVersion)
                VALUES(
                    'FHIR-1',
                    'Unsafe <unsafe@example.org>',
                    @currentPolicy);
                """;
            command.Parameters.AddWithValue(
                "@oldPolicy",
                PublicDisplayNamePolicy.CurrentVersion - 1);
            command.Parameters.AddWithValue(
                "@currentPolicy",
                PublicDisplayNamePolicy.CurrentVersion);
            command.ExecuteNonQuery();
        }

        AuthoringSnapshotDescriptor descriptor =
            (await fixture.CreatePostProcessor(
                    PreparedTicketSnapshotSchemaV3.Version)
                .FinalizeRunAsync(secondRun.Id))!;

        Assert.Equal(
            PreparedTicketSnapshotSchemaV3.Version,
            descriptor.SchemaVersion);
        Assert.Equal(
            PreparedTicketSnapshotSchemaV3.CountedTables.Order(),
            descriptor.TableCounts.Keys.Order());
        Assert.Equal(
            2,
            descriptor.TableCounts["prepared_ticket_hydration"]);
        Assert.Equal(
            2,
            descriptor.TableCounts["prepared_jira_hydration"]);
        Assert.Equal(
            1,
            descriptor.TableCounts[
                "prepared_ticket_in_person_requesters"]);

        using SqliteConnection snapshot = OpenReadOnly(
            Path.Combine(fixture.SnapshotDirectory, descriptor.FileName));
        AssertMatchesCatalog(
            snapshot,
            PreparedTicketSnapshotSchemaV3.Catalog);
        Assert.Equal(
            "Ada",
            Scalar<string>(
                snapshot,
                """
                SELECT Reporter
                FROM prepared_ticket_hydration
                WHERE TicketKey = 'FHIR-1'
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                snapshot,
                $"""
                SELECT COUNT(*)
                FROM prepared_ticket_hydration
                WHERE TicketKey = 'FHIR-1'
                  AND Assignee IS NULL
                  AND PublicDisplayNamePolicyVersion = {PublicDisplayNamePolicy.CurrentVersion}
                """));
        Assert.Equal(
            "Related Reporter",
            Scalar<string>(
                snapshot,
                """
                SELECT Reporter
                FROM prepared_jira_hydration
                WHERE TicketKey = 'FHIR-1'
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                snapshot,
                $"""
                SELECT COUNT(*)
                FROM prepared_jira_hydration
                WHERE TicketKey = 'FHIR-1'
                  AND Assignee IS NULL
                  AND PublicDisplayNamePolicyVersion = {PublicDisplayNamePolicy.CurrentVersion}
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                snapshot,
                """
                SELECT COUNT(*)
                FROM prepared_ticket_hydration
                WHERE TicketKey = 'FHIR-2'
                  AND Reporter IS NULL
                  AND Assignee IS NULL
                """));
        Assert.Equal(
            1,
            Scalar<int>(
                snapshot,
                """
                SELECT COUNT(*)
                FROM prepared_jira_hydration
                WHERE TicketKey = 'FHIR-2'
                  AND Reporter IS NULL
                  AND Assignee IS NULL
                """));
        Assert.Equal(
            "Lin Example",
            Scalar<string>(
                snapshot,
                """
                SELECT DisplayName
                FROM prepared_ticket_in_person_requesters
                """));
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            Scalar<int>(
                snapshot,
                """
                SELECT PublicDisplayNamePolicyVersion
                FROM prepared_ticket_in_person_requesters
                """));
    }

    [Fact]
    public async Task FinalizeLegacyRun_SeedsNullProvenanceWithoutFabrication()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(
                databaseOnly: false,
                sourceLastSuccessfulRefreshAt:
                    new DateTimeOffset(
                        2026,
                        9,
                        8,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                sourceContentRevision: 88);
        using (SqliteConnection connection = fixture.Database.OpenConnection())
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                DELETE FROM authoring_run_input_provenance
                WHERE RunId = @runId
                """;
            command.Parameters.AddWithValue("@runId", run.Id);
            Assert.Equal(1, command.ExecuteNonQuery());
            PreparerDatabase.EnsureSchema(connection);
        }

        AuthoringSnapshotDescriptor descriptor =
            (await fixture.CreatePostProcessor(
                    PreparedTicketSnapshotSchemaV2.Version)
                .FinalizeRunAsync(run.Id))!;
        using SqliteConnection snapshot = OpenReadOnly(
            Path.Combine(fixture.SnapshotDirectory, descriptor.FileName));

        Assert.Equal(
            1,
            Scalar<int>(
                snapshot,
                $"""
                SELECT COUNT(*)
                FROM authoring_run_input_provenance
                WHERE RunId = '{run.Id}'
                  AND Source = 'jira'
                  AND LatestSuccessfulRefreshAt IS NULL
                  AND ContentRevision IS NULL
                """));
    }

    [Fact]
    public async Task FinalizeRun_RejectsUnsupportedSnapshotSchemaBeforeWriting()
    {
        using Fixture fixture = new();
        (AuthoringRunRecord run, _) =
            await fixture.CreateCompletedRunAsync(databaseOnly: false);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => fixture.CreatePostProcessor(99).FinalizeRunAsync(run.Id));

        Assert.False(Directory.Exists(fixture.SnapshotDirectory));
        Assert.Equal(
            AuthoringStatusValues.Runs.Running,
            (await fixture.AuthoringStore.GetRunAsync(run.Id))!.Status);
    }

    private static PreparedTicketPayload CreatePayload(string key)
        => new()
        {
            Key = key,
            RequestSummary = "Request",
            ProposalA = "A",
            ProposalAImpact = PreparedTicketImpactValues.NonSubstantive,
            ProposalB = "B",
            ProposalBImpact = PreparedTicketImpactValues.NonSubstantive,
            ProposalC = "C",
            Recommendation = PreparedTicketRecommendationValues.ProposalA,
            RecommendationJustification = "Because",
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

    private static void AssertMatchesCatalog(
        SqliteConnection connection,
        AuthoringSnapshotSchemaCatalog catalog)
    {
        using SqliteCommand tablesCommand = connection.CreateCommand();
        tablesCommand.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;
        using SqliteDataReader tablesReader = tablesCommand.ExecuteReader();
        List<string> actualTables = [];
        while (tablesReader.Read())
        {
            actualTables.Add(tablesReader.GetString(0));
        }
        tablesReader.Close();

        Assert.Equal(
            catalog.Tables
                .Select(table => table.Name)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            actualTables);

        foreach (AuthoringSnapshotTableSchema table in catalog.Tables)
        {
            using SqliteCommand columnsCommand = connection.CreateCommand();
            columnsCommand.CommandText =
                """
                SELECT name
                FROM pragma_table_info(@tableName)
                ORDER BY cid
                """;
            columnsCommand.Parameters.AddWithValue(
                "@tableName",
                table.Name);
            using SqliteDataReader columnsReader =
                columnsCommand.ExecuteReader();
            List<string> actualColumns = [];
            while (columnsReader.Read())
            {
                actualColumns.Add(columnsReader.GetString(0));
            }

            Assert.Equal(table.Columns.ToArray(), actualColumns);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly JiraProcessingSourceTicketStore _sourceStore;
        private readonly JiraAuthoringRunCoordinator _coordinator;

        public Fixture(bool activate = true)
        {
            _directory = Path.Combine(
                Path.GetTempPath(),
                $"fhir-augury-preparer-snapshot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_directory);
            string databasePath = Path.Combine(_directory, "preparer.db");
            SnapshotDirectory = Path.Combine(_directory, "snapshots");
            Database = new PreparerDatabase(
                databasePath,
                NullLogger<PreparerDatabase>.Instance);
            Database.Initialize();
            AuthoringStore = new AuthoringRunStore(Database);
            _sourceStore = new JiraProcessingSourceTicketStore(databasePath);
            IOptions<JiraProcessingOptions> jiraOptions = Options.Create(new JiraProcessingOptions
            {
                AgentCliCommand = "agent {ticketKey}",
                JiraSourceAddress = "http://source",
                SourceTicketShape = "fhir",
                TicketStatusesToProcess = ["Triaged"],
            });
            _coordinator = new JiraAuthoringRunCoordinator(
                AuthoringStore,
                _sourceStore,
                new JiraConfiguredTicketSelector(_sourceStore, new TestJiraTicketLabelMatcher()),
                new JiraProcessingFilterResolver(),
                jiraOptions);
            if (activate)
            {
                ActivateAsync().GetAwaiter().GetResult();
            }
        }

        public PreparerDatabase Database { get; }
        public AuthoringRunStore AuthoringStore { get; }
        public string SnapshotDirectory { get; }

        public async Task ActivateAsync()
        {
            await AuthoringStore.EnsureProcessorModeAsync(
                _coordinator.ProcessorKind);
            AuthoringProcessorModeRecord mode =
                await AuthoringStore.GetProcessorModeAsync(
                    _coordinator.ProcessorKind);
            if (mode.Mode == AuthoringStatusValues.ProcessorModes.Legacy)
            {
                await AuthoringStore.TransitionProcessorModeAsync(
                    _coordinator.ProcessorKind,
                    AuthoringStatusValues.ProcessorModes.Legacy,
                    AuthoringStatusValues.ProcessorModes.CuttingOver);
                await AuthoringStore.TransitionProcessorModeAsync(
                    _coordinator.ProcessorKind,
                    AuthoringStatusValues.ProcessorModes.CuttingOver,
                    AuthoringStatusValues.ProcessorModes.RunBacked);
            }
        }

        public Task<JiraProcessingSourceTicketRecord> SeedSourceAsync(
            string key,
            DateTimeOffset revision,
            string title = "Title")
            => _sourceStore.UpsertAsync(
                new JiraIssueSummaryEntry
                {
                    Key = key,
                    ProjectKey = "FHIR",
                    Title = title,
                    Type = "Change Request",
                    Status = "Triaged",
                    WorkGroup = "FHIR-I",
                    Specification = "FHIR",
                    UpdatedAt = revision,
                },
                "fhir",
                false,
                CancellationToken.None);

        public async Task<JiraAuthoringRunCreation> CreateRunAsync(
            IReadOnlyCollection<JiraProcessingSourceTicketRecord> sources,
            bool databaseOnly)
        {
            JiraAuthoringRunCreation creation =
                await _coordinator.CreateExplicitRunAsync(
                    sources,
                    databaseOnly);
            Assert.True(await AuthoringStore.TryAcquireMutationFenceAsync(
                _coordinator.ProcessorKind,
                creation.Run.Id));
            return creation;
        }

        public Task<bool> ReplaceStaleInitialRunAsync(string runId)
            => _coordinator.SupersedeStaleItemsAsync(runId);

        public async Task CompleteItemAsync(
            AuthoringRunRecord run,
            AuthoringRunItemRecord item,
            PreparedTicketPayload payload)
        {
            AuthoringOperationClaim claim =
                Assert.IsType<AuthoringOperationClaim>(
                    await AuthoringStore.ClaimItemAsync(
                        run.Id,
                        item.Id));
            string hash =
                PreparedTicketAuthoringDtos.ComputeContentHash(payload);
            AuthoringReceiptAcceptance receipt =
                await AuthoringStore.AcceptResultAsync(
                    new AuthoringResultSubmission(
                        run.Id,
                        item.Id,
                        claim.OperationId,
                        item.ExpectedSourceRevision,
                        hash),
                    claim.OperationToken,
                    (connection, ct) =>
                        Database.SavePreparedTicketForAuthoringAsync(
                            connection,
                            payload,
                            hash,
                            run.Id,
                            item.Id,
                            claim.OperationId,
                            ct));
            await AuthoringStore.MarkItemCompleteAsync(
                item.Id,
                receipt.Receipt.ReceiptId);
        }

        public async Task<(AuthoringRunRecord Run, AuthoringRunItemRecord Item)> CreateCompletedRunAsync(
            bool databaseOnly,
            string key = "FHIR-1",
            DateTimeOffset? sourceLastSuccessfulRefreshAt = null,
            long? sourceContentRevision = null,
            int? publicDisplayNamePolicyVersion =
                PublicDisplayNamePolicy.CurrentVersion)
        {
            JiraProcessingSourceTicketRecord source = await _sourceStore.UpsertAsync(
                new JiraIssueSummaryEntry
                {
                    Key = key,
                    ProjectKey = "FHIR",
                    Title = "Title",
                    Type = "Change Request",
                    Status = "Triaged",
                    WorkGroup = "FHIR-I",
                    Specification = "FHIR",
                    UpdatedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                },
                "fhir",
                false,
                sourceLastSuccessfulRefreshAt,
                sourceContentRevision,
                CancellationToken.None);
            JiraAuthoringRunCreation creation =
                await _coordinator.CreateOneItemRunAsync(source, databaseOnly);
            Assert.True(await AuthoringStore.TryAcquireMutationFenceAsync(
                _coordinator.ProcessorKind,
                creation.Run.Id));
            AuthoringRunItemRecord item = Assert.Single(creation.Items);
            AuthoringOperationClaim claim =
                (await AuthoringStore.ClaimItemAsync(creation.Run.Id, item.Id))!;
            PreparedTicketPayload payload = CreatePayload(key);
            string hash = FhirAugury.Processor.Jira.Fhir.Preparer.Api.PreparedTicketAuthoringDtos
                .ComputeContentHash(payload);
            AuthoringReceiptAcceptance receipt = await AuthoringStore.AcceptResultAsync(
                new AuthoringResultSubmission(
                    creation.Run.Id,
                    item.Id,
                    claim.OperationId,
                    item.ExpectedSourceRevision,
                    hash),
                claim.OperationToken,
                (connection, ct) => Database.SavePreparedTicketForAuthoringAsync(
                    connection,
                    payload,
                    hash,
                    creation.Run.Id,
                    item.Id,
                    claim.OperationId,
                    ct));

            DateTimeOffset hydratedAt = DateTimeOffset.UtcNow;
            await Database.SaveHydrationAsync(
                new PreparedTicketHydrationBatch(
                    key,
                    new PreparedTicketHydrationRow(
                        key,
                        "Major",
                        "Persuasive",
                        "resolution",
                        "FHIR",
                        null,
                        null,
                        null,
                        null,
                        null,
                        0,
                        "request",
                        hydratedAt,
                        "resolved",
                        null,
                        "<p>request</p>",
                        "<p>resolution</p>",
                        "Ada",
                        hydratedAt.AddDays(-1),
                        "Patient",
                        "patient.html",
                        Assignee: "Grace Example",
                        SourceProject: sourceContentRevision is null
                            ? null
                            : "FHIR",
                        SourceLastSuccessfulRefreshAt:
                            sourceLastSuccessfulRefreshAt,
                        SourceContentRevision: sourceContentRevision,
                        PublicDisplayNamePolicyVersion:
                            publicDisplayNamePolicyVersion),
                    [
                        new PreparedJiraHydrationRow(
                            key,
                            key,
                            "Title",
                            "Triaged",
                            "Change Request",
                            "Major",
                            "Persuasive",
                            "resolution",
                            "FHIR Infrastructure",
                            "FHIR",
                            hydratedAt,
                            "https://jira/FHIR-1",
                            hydratedAt,
                            "resolved",
                            null,
                            Assignee: "Grace Example",
                            PublicDisplayNamePolicyVersion:
                                publicDisplayNamePolicyVersion),
                    ],
                    [],
                    [],
                    [],
                    [],
                    [new PreparedTicketInPersonRequesterRow(
                        key,
                        "Lin Example",
                        publicDisplayNamePolicyVersion)]));
            await AuthoringStore.MarkItemCompleteAsync(item.Id, receipt.Receipt.ReceiptId);
            return (creation.Run, item);
        }

        public async Task<(AuthoringRunRecord Run, AuthoringRunItemRecord Item)> CreateSupersededRunAsync(
            bool databaseOnly,
            string key = "FHIR-1",
            DateTimeOffset? sourceLastSuccessfulRefreshAt = null,
            long? sourceContentRevision = null)
        {
            JiraProcessingSourceTicketRecord source = await _sourceStore.UpsertAsync(
                new JiraIssueSummaryEntry
                {
                    Key = key,
                    ProjectKey = "FHIR",
                    Title = "Title",
                    Type = "Change Request",
                    Status = "Triaged",
                    WorkGroup = "FHIR-I",
                    Specification = "FHIR",
                    UpdatedAt = new DateTimeOffset(
                        2026,
                        9,
                        1,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                },
                "fhir",
                false,
                sourceLastSuccessfulRefreshAt,
                sourceContentRevision,
                CancellationToken.None);
            JiraAuthoringRunCreation creation =
                await _coordinator.CreateOneItemRunAsync(source, databaseOnly);
            Assert.True(await AuthoringStore.TryAcquireMutationFenceAsync(
                _coordinator.ProcessorKind,
                creation.Run.Id));
            AuthoringRunItemRecord item = Assert.Single(creation.Items);
            AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
                await AuthoringStore.ClaimItemAsync(creation.Run.Id, item.Id));
            await AuthoringStore.MarkClaimErrorAsync(
                item.Id,
                claim.OperationId,
                "worker failure");
            await AuthoringStore.SupersedeErroredItemAsync(
                creation.Run.Id,
                item.Id,
                "not actionable");
            return (creation.Run, item);
        }

        public void MarkAsInitialRevalidation(string runId)
        {
            using SqliteConnection connection = Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE authoring_processor_modes
                SET RevalidationRequired = 1,
                    RevalidationRunId = @runId
                WHERE ProcessorKind = 'jira-fhir'
                ;
                UPDATE authoring_runs
                SET Purpose = 'initial-revalidation'
                WHERE Id = @runId
                """;
            command.Parameters.AddWithValue("@runId", runId);
            Assert.Equal(2, command.ExecuteNonQuery());
        }

        public async Task AdvanceSourceRevisionAsync(CancellationToken ct)
        {
            await _sourceStore.UpsertAsync(
                new JiraIssueSummaryEntry
                {
                    Key = "FHIR-1",
                    ProjectKey = "FHIR",
                    Title = "Updated title",
                    Type = "Change Request",
                    Status = "Triaged",
                    WorkGroup = "FHIR-I",
                    Specification = "FHIR",
                    UpdatedAt = new DateTimeOffset(
                        2026,
                        9,
                        2,
                        0,
                        0,
                        0,
                        TimeSpan.Zero),
                },
                "fhir",
                false,
                ct);
        }

        public PreparedTicketRunPostProcessor CreatePostProcessor(
            IPreparedTicketGroupingDispatcher groupingDispatcher)
            => CreatePostProcessor(
                PreparedTicketSnapshotSchemaV1.Version,
                groupingDispatcher);

        public PreparedTicketRunPostProcessor CreatePostProcessor(
            int schemaVersion = PreparedTicketSnapshotSchemaV1.Version,
            IPreparedTicketGroupingDispatcher? groupingDispatcher = null)
        {
            HttpClient client = new(new WorkGroupHandler())
            {
                BaseAddress = new Uri("http://localhost/"),
            };
            PreparerServiceOptions options = new()
            {
                SnapshotDirectory = SnapshotDirectory,
                SnapshotSchemaVersion = schemaVersion,
                ReconcileSnapshotsOnStartup = true,
            };
            return new PreparedTicketRunPostProcessor(
                Database,
                AuthoringStore,
                new AuthoringRunFinalizer(AuthoringStore),
                new SqliteReviewSnapshotReconciler(AuthoringStore),
                _coordinator,
                new OrchestratorWorkGroupCatalogFetcher(client),
                groupingDispatcher ?? new EmptyGroupingDispatcher(Database),
                Options.Create(options));
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

    private sealed class EmptyGroupingDispatcher(PreparerDatabase database)
        : IPreparedTicketGroupingDispatcher
    {
        public async Task ReplaceGroupingAsync(
            string runId,
            PreparedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct)
        {
            _ = await database.SaveGroupingForRunAsync(
                    new PreparedTicketGroupingPayload
                    {
                        WorkGroupClean = partition.WorkGroupClean,
                        WorkGroupDisplay = partition.WorkGroupDisplay,
                        Specification = partition.Specification,
                        Type = partition.Type,
                        Topics = [],
                    },
                    runId,
                    lease.StageId,
                    lease.LeaseId,
                    partition.InputFingerprint,
                    ct);
        }
    }

    private sealed class SourceChangingGroupingDispatcher(
        PreparerDatabase database,
        Func<CancellationToken, Task> changeSourceRevision)
        : IPreparedTicketGroupingDispatcher
    {
        private readonly EmptyGroupingDispatcher _inner = new(database);
        private bool _changed;

        public async Task ReplaceGroupingAsync(
            string runId,
            PreparedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct)
        {
            if (!_changed)
            {
                await changeSourceRevision(ct);
                _changed = true;
            }
            await _inner.ReplaceGroupingAsync(runId, partition, lease, ct);
        }
    }

    private sealed class NoOpGroupingDispatcher
        : IPreparedTicketGroupingDispatcher
    {
        public Task ReplaceGroupingAsync(
            string runId,
            PreparedTicketRunPartition partition,
            AuthoringRunStageLease lease,
            CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class WorkGroupHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string json =
                """{"workGroups":[{"code":"fhir-i","name":"FHIR Infrastructure","retired":false,"totalFileCount":1,"totalArtifactCount":1,"repos":[]}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
