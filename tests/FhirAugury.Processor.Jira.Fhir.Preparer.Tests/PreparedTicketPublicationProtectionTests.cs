using System.Globalization;
using System.Text.Json;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Configuration;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Configuration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static FhirAugury.Processor.Jira.Fhir.Preparer.Tests.PreparedTicketPublicationTestFixture;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketPublicationProtectionTests
{
    private static readonly string[] OriginalKeys =
        ["FHIR-701", "FHIR-702", "FHIR-703", "FHIR-704", "FHIR-705", "FHIR-706"];

    [Fact]
    public async Task Baseline_RequiresExactOriginalProtectedValues()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync(OriginalKeys);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();
        PreparedTicketPublicationProtectionReader.Compare(baseline, current);
        Assert.Equal(6, current.Corpus.Count);
        Assert.Equal(2, Assert.Single(current.Grouping).Rows.Count(row => row.Table == "prepared_ticket_topics"));
        Assert.Contains(baseline.Inventory.Rows, row => row.Table == "prepared_ticket_jira_content");
        Assert.Contains(baseline.Inventory.Rows, row => row.Table == "prepared_ticket_artifacts");
        Assert.Contains(baseline.Inventory.Rows, row => row.Table == "prepared_ticket_pages");

        foreach ((string table, string columns) in RequiredProtectedColumns)
        {
            foreach (string column in columns.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                await AssertMutationRefusedAsync(fixture, baseline, table, column);
            }
        }
        foreach (string column in new[] { "Reporter", "Assignee", "UpdatedAt", "PublicDisplayNamePolicyVersion" })
        {
            await AssertMutationRefusedAsync(
                fixture, baseline, "prepared_jira_hydration", column, "JiraKey <> TicketKey COLLATE NOCASE");
        }
        foreach (string column in new[]
        {
            "StreamId", "StreamName", "Topic", "MessageCount", "FirstMessageAt", "LastMessageAt",
            "FirstMessageExcerpt", "Url", "HydratedAt", "HydrationStatus", "HydrationReason",
        })
        {
            await AssertMutationRefusedAsync(
                fixture, baseline, "prepared_zulip_hydration", column, "ZulipThreadId = 'unaccepted-reference'");
        }
        Assert.Equal(source.Descriptor.Sha256, await SqliteReviewSnapshotWriter.ComputeSha256Async(
            Path.Combine(fixture.SnapshotDirectory, source.Descriptor.FileName)));
        Assert.Equal(current.ProtectedContentFingerprint, (await fixture.ReadCurrentAsync()).ProtectedContentFingerprint);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
    }

    [Fact]
    public async Task Baseline_RejectsReplacedReceiptWithSameCounts()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync(OriginalKeys);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        string oldReceipt = source.ReceiptIds["FHIR-701"];
        fixture.Execute(
            """
            UPDATE authoring_result_receipts SET Id = 'replacement-receipt' WHERE Id = @old;
            UPDATE authoring_run_items SET AcceptedReceiptId = 'replacement-receipt' WHERE AcceptedReceiptId = @old;
            """,
            ("@old", oldReceipt));

        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();

        Assert.Equal(baseline.Inventory.Corpus.Count, current.Corpus.Count);
        Assert.Equal(6, fixture.CountLive("authoring_result_receipts"));
        Assert.Equal(6, fixture.CountLive("prepared_tickets"));
        Assert.Equal("replacement-receipt", current.Corpus.Single(item => item.TicketKey == "FHIR-701").ReceiptId);
        PreparedTicketPublicationProtectionException error = Assert.Throws<PreparedTicketPublicationProtectionException>(
            () => PreparedTicketPublicationProtectionReader.Compare(baseline, current));
        Assert.Equal("original-output-changed", error.FailureCode);
    }

    [Fact]
    public async Task Baseline_RejectsEquivalentButRewrittenRevisionText()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAtAsync(
            new DateTimeOffset(2025, 7, 17, 16, 12, 12, TimeSpan.FromHours(-5)), OriginalKeys);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        const string rewritten = "2025-07-17T21:12:12.0000000+00:00";
        Assert.NotEqual(rewritten, baseline.Inventory.Corpus[0].ExpectedSourceRevision);
        fixture.Execute(
            """
            UPDATE authoring_run_items SET ExpectedSourceRevision = @revision WHERE BusinessKey = 'FHIR-701';
            UPDATE authoring_result_receipts
            SET ExpectedSourceRevision = @revision, ObservedSourceRevision = @revision
            WHERE BusinessKey = 'FHIR-701';
            """,
            ("@revision", rewritten));
        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();
        Assert.Equal(6, current.Corpus.Count);
        Assert.Throws<PreparedTicketPublicationProtectionException>(
            () => PreparedTicketPublicationProtectionReader.Compare(baseline, current));
    }

    [Theory]
    [InlineData("line-endings")]
    [InlineData("null-empty")]
    [InlineData("unicode-form")]
    public async Task BaselineRejectsExactValueDriftThatLegacyCorpusHashCannotDetect(string change)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync(OriginalKeys);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        PreparedTicketPublicationProtectedInventory before = await fixture.ReadCurrentAsync();
        PreparedTicketPublicationEnrichmentInput input =
            PreparedTicketPublicationProtectionReader.CreateRecipeInput(baseline, before);
        fixture.Execute(change switch
        {
            "line-endings" => "UPDATE prepared_tickets SET RequestSummary = replace(RequestSummary, char(10), char(13) || char(10)) WHERE Key = 'FHIR-701'",
            "unicode-form" => "UPDATE prepared_tickets SET RequestSummary = replace(RequestSummary, char(233), 'e' || char(769)) WHERE Key = 'FHIR-701'",
            "null-empty" => "UPDATE prepared_ticket_hydration SET HydrationReason = '' WHERE TicketKey = 'FHIR-701'",
            _ => throw new InvalidOperationException("Unknown exact-value mutation."),
        });
        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();
        Assert.Equal(before.CorpusFingerprint, current.CorpusFingerprint);
        Assert.Equal(6, current.Corpus.Count);
        Assert.NotEqual(before.ProtectedContentFingerprint, current.ProtectedContentFingerprint);
        Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
            PreparedTicketPublicationProtectionReader.Compare(baseline, current));
        Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
            PreparedTicketPublicationProtectionReader.ValidateFrozen(input, current));
    }

    [Fact]
    public async Task BaselineRejectsReplacedRunItemDespiteUnchangedReceiptAndCounts()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-701", "FHIR-702");
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        string oldItem = baseline.Inventory.Corpus.Single(item => item.TicketKey == "FHIR-701").RunItemId;
        fixture.Execute(
            """
            UPDATE authoring_run_items SET Id = 'replaced-item' WHERE Id = @old;
            UPDATE authoring_result_receipts SET RunItemId = 'replaced-item' WHERE RunItemId = @old;
            UPDATE prepared_ticket_authoring_state SET RunItemId = 'replaced-item' WHERE RunItemId = @old;
            UPDATE prepared_ticket_run_item_partitions SET RunItemId = 'replaced-item' WHERE RunItemId = @old;
            """,
            ("@old", oldItem));
        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();
        Assert.Equal(2, current.Corpus.Count);
        Assert.Equal(2, fixture.CountLive("authoring_run_items"));
        Assert.Equal(source.ReceiptIds["FHIR-701"], current.Corpus.Single(item => item.TicketKey == "FHIR-701").ReceiptId);
        Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
            PreparedTicketPublicationProtectionReader.Compare(baseline, current));
    }

    [Fact]
    public async Task BaselineUsesExportedCorpusAndAllowsHistoricalRowsOmittedBySnapshotSanitization()
    {
        using Fixture fixture = new();
        _ = await fixture.CreateSourceRunAsync("FHIR-701", "FHIR-702");
        SourceResult source = await fixture.CreateSourceRunAtAsync(
            new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), "FHIR-701");
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();
        Assert.Equal(1, source.Run.TotalItems);
        Assert.Equal(2, baseline.Source.ExportedTicketCount);
        Assert.Equal(3, fixture.CountLive("authoring_result_receipts"));
        Assert.Equal(2, baseline.Inventory.Rows.Count(row => row.Table == "authoring_result_receipts"));
        Assert.Equal(3, current.Rows.Count(row => row.Table == "authoring_result_receipts"));
        PreparedTicketPublicationPreservationComparison comparison =
            PreparedTicketPublicationProtectionReader.Compare(baseline, current);
        Assert.Equal(new AuthoringRunCorpusComparison(source.Descriptor.SnapshotId, 2, 2, 0), comparison.CorpusComparison);
        PreparedTicketPublicationEnrichmentInput input =
            PreparedTicketPublicationProtectionReader.CreateRecipeInput(baseline, current);
        Assert.Equal(3, input.ProtectedRows.Count(row => row.Table == "authoring_result_receipts"));
    }

    [Fact]
    public async Task Baseline_RejectsRemovedOriginalTicket()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-701", "FHIR-702");
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        fixture.Execute("DELETE FROM prepared_tickets WHERE Key = 'FHIR-701'");
        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();
        Assert.Single(current.Corpus);
        Assert.Throws<PreparedTicketPublicationProtectionException>(
            () => PreparedTicketPublicationProtectionReader.Compare(baseline, current));
    }

    [Theory]
    [InlineData("topic-id")]
    [InlineData("topic-row-id")]
    [InlineData("topic-order")]
    [InlineData("topic-text")]
    [InlineData("group-id")]
    [InlineData("group-order")]
    [InlineData("group-parent")]
    [InlineData("member-id")]
    [InlineData("member-order")]
    [InlineData("member-ticket")]
    [InlineData("source-receipt")]
    public async Task Baseline_RejectsChangedGroupingIdsOrderAndMembership(string change)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync(OriginalKeys);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        PreparedTicketPublicationProtectedInventory before = await fixture.ReadCurrentAsync();
        string sql = change switch
        {
            "topic-id" => "UPDATE prepared_ticket_topics SET Id = Id || '-replaced' WHERE RowId = 1",
            "topic-row-id" =>
                """
                UPDATE prepared_ticket_topics SET RowId = 101 WHERE RowId = 1;
                UPDATE prepared_ticket_topic_groups SET TopicRowId = 101 WHERE TopicRowId = 1;
                UPDATE prepared_ticket_topic_members SET TopicRowId = 101 WHERE TopicRowId = 1;
                """,
            "topic-order" => "UPDATE prepared_ticket_topics SET RenderOrderHint = 9 WHERE RowId = 1",
            "topic-text" => "UPDATE prepared_ticket_topics SET LongerDescription = ' ' || LongerDescription WHERE RowId = 1",
            "group-id" => "UPDATE prepared_ticket_topic_groups SET Id = Id || '-replaced' WHERE RowId = 1",
            "group-order" =>
                """
                UPDATE prepared_ticket_topic_groups SET OrderInTopic = OrderInTopic + 10;
                UPDATE prepared_ticket_topic_groups SET OrderInTopic = 11 - OrderInTopic;
                """,
            "group-parent" =>
                """
                UPDATE prepared_ticket_topic_groups SET TopicRowId = 2, OrderInTopic = 0 WHERE RowId = 2;
                UPDATE prepared_ticket_topic_members SET TopicRowId = 2 WHERE TopicGroupRowId = 2;
                """,
            "member-id" => "UPDATE prepared_ticket_topic_members SET Id = Id || '-replaced' WHERE RowId = 1",
            "member-order" =>
                """
                UPDATE prepared_ticket_topic_members SET OrderInContainer = OrderInContainer + 10 WHERE TopicGroupRowId = 1;
                UPDATE prepared_ticket_topic_members SET OrderInContainer = 11 - OrderInContainer WHERE TopicGroupRowId = 1;
                """,
            "member-ticket" =>
                """
                UPDATE prepared_ticket_topic_members SET TicketKey = TicketKey || '-temporary' WHERE TopicGroupRowId = 1;
                UPDATE prepared_ticket_topic_members
                SET TicketKey = CASE TicketKey WHEN 'FHIR-701-temporary' THEN 'FHIR-702' ELSE 'FHIR-701' END
                WHERE TopicGroupRowId = 1;
                """,
            "source-receipt" => "UPDATE prepared_ticket_partition_receipts SET StageId = 'replacement-stage'",
            _ => throw new InvalidOperationException("Unknown grouping mutation."),
        };
        fixture.Execute(sql);

        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();

        Assert.Equal(6, current.Corpus.Count);
        Assert.Equal(2, fixture.CountLive("prepared_ticket_topics"));
        Assert.Equal(2, fixture.CountLive("prepared_ticket_topic_groups"));
        Assert.Equal(6, fixture.CountLive("prepared_ticket_topic_members"));
        Assert.Throws<PreparedTicketPublicationProtectionException>(
            () => PreparedTicketPublicationProtectionReader.Compare(baseline, current));
        if (change is "topic-id" or "group-id" or "member-id")
        {
            Assert.Equal(Assert.Single(before.Grouping).Fingerprint.OutputFingerprint,
                Assert.Single(current.Grouping).Fingerprint.OutputFingerprint);
            Assert.NotEqual(before.RetainedGroupingFingerprint, current.RetainedGroupingFingerprint);
        }
    }

    [Fact]
    public async Task Baseline_RejectsAdditionalMembershipInOriginalPartition()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync(OriginalKeys);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        await fixture.CreateAdditionalCurrentOutputAsync("FHIR", ["FHIR-801", "FHIR-802"], groupOutput: false);
        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();
        Assert.Equal(8, current.Corpus.Count);
        Assert.Equal(6, fixture.CountLive("prepared_ticket_topic_members"));
        Assert.Throws<PreparedTicketPublicationProtectionException>(
            () => PreparedTicketPublicationProtectionReader.Compare(baseline, current));
        fixture.Execute(
            """
            INSERT INTO prepared_ticket_topic_members(Id, TopicRowId, TopicGroupRowId, TicketKey, OrderInContainer)
            VALUES('additional-member', 2, NULL, 'FHIR-801', 2)
            """);
        current = await fixture.ReadCurrentAsync();
        Assert.Equal(7, fixture.CountLive("prepared_ticket_topic_members"));
        Assert.Throws<PreparedTicketPublicationProtectionException>(
            () => PreparedTicketPublicationProtectionReader.Compare(baseline, current));
    }

    [Fact]
    public async Task Baseline_ReportsAdditionalCurrentOutput()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync(OriginalKeys);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        await fixture.CreateAdditionalCurrentOutputAsync("Additional specification", ["FHIR-801", "FHIR-802"]);
        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();

        PreparedTicketPublicationPreservationComparison comparison =
            PreparedTicketPublicationProtectionReader.Compare(baseline, current);

        Assert.Equal(new AuthoringRunCorpusComparison(source.Descriptor.SnapshotId, 6, 8, 2), comparison.CorpusComparison);
        Assert.Equal(["FHIR-801", "FHIR-802"], comparison.AdditionalTicketKeys);
        Assert.Single(comparison.AdditionalGroupingPartitions);
        Assert.Same(current, comparison.CurrentInventory);
        Assert.Equal(source.Descriptor.Sha256, await SqliteReviewSnapshotWriter.ComputeSha256Async(
            Path.Combine(fixture.SnapshotDirectory, source.Descriptor.FileName)));
        PreparedTicketPublicationEnrichmentInput input =
            PreparedTicketPublicationProtectionReader.CreateRecipeInput(baseline, current);
        Assert.Equal(8, input.Corpus.Count);
        Assert.Equal(current.ProtectedContentFingerprint, input.ProtectedContentFingerprint);
        Assert.Equal(current.RetainedGroupingFingerprint, input.RetainedGroupingFingerprint);
        Assert.Contains(input.ProtectedRows, row => row.Table == "prepared_tickets" && row.Scope == "FHIR-801");
        PreparedTicketPublicationProtectionReader.ValidateFrozen(input, current);

        RecordingLogger logger = new();
        PreparedTicketPublicationBaselineReader baselineReader = fixture.CreateBaselineReader(logger);
        AuthoringRunRecord run = await fixture.Store.CreateMaintenanceRunAsync(
            "jira-fhir", (connection, ct) => baselineReader.CreateSelectionAsync(connection, baseline, ct),
            AuthoringRunPurposeValues.PublicationRefresh, databaseOnly: false, sourceRunId: source.Run.Id);
        AuthoringRunStore restarted = new(fixture.Database);
        AuthoringRunRecord durable = Assert.IsType<AuthoringRunRecord>(await restarted.GetRunAsync(run.Id));
        AuthoringMaintenanceRunRequest request = Assert.IsType<AuthoringMaintenanceRunRequest>(
            AuthoringMaintenanceRunRequest.Parse(durable.RequestJson));
        request.EnsureRecipe(PreparedTicketPublicationEnrichmentContract.RecipeName, 1);
        PreparedTicketPublicationEnrichmentInput restored =
            PreparedTicketPublicationEnrichmentContract.ParseInput(request.RecipeInputJson);
        Assert.Equal(["FHIR-801", "FHIR-802"], restored.AdditionalTicketKeys);
        Assert.Equal(8, (await restarted.GetRunItemsAsync(run.Id)).Count);
        Assert.Empty(await restarted.GetRunStagesAsync(run.Id));
        Assert.Equal(run.Id, (await restarted.GetFencedRunAsync("jira-fhir"))!.Id);
        Assert.Contains(logger.Messages, message => message.Contains("FHIR-801, FHIR-802", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("2 additional", StringComparison.Ordinal));
        AuthoringRunControlService control = new(
            restarted, new AuthoringRetryPolicy(Options.Create(new ProcessingServiceOptions())));
        Assert.Equal(comparison.CorpusComparison, (await control.GetStatusAsync("jira-fhir", run.Id)).Run.CorpusComparison);
        string statusJson = JsonSerializer.Serialize(await control.GetStatusAsync("jira-fhir", run.Id), JsonSerializerOptions.Web);
        Assert.DoesNotContain("recipeInput", statusJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Repo reason", statusJson, StringComparison.Ordinal);
        Assert.DoesNotContain("protectedRows", statusJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AdmissionRefusalLeavesNoRunRequestOrFence()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync(OriginalKeys);
        PreparedTicketPublicationBaselineReader reader = fixture.CreateBaselineReader();
        PreparedTicketPublicationBaseline baseline = await reader.ReadAsync(source.Run.Id);
        fixture.Execute("UPDATE prepared_ticket_related_zulip SET Id = 'different-association' WHERE RowId = 1");
        int attempts = fixture.CountLive("authoring_run_attempts");
        int requests = fixture.Scalar<int>("SELECT COUNT(*) FROM authoring_runs WHERE RequestJson IS NOT NULL");

        await Assert.ThrowsAsync<PreparedTicketPublicationProtectionException>(() =>
            fixture.Store.CreateMaintenanceRunAsync(
                "jira-fhir", (connection, ct) => reader.CreateSelectionAsync(connection, baseline, ct),
                AuthoringRunPurposeValues.PublicationRefresh, databaseOnly: false, sourceRunId: source.Run.Id));

        Assert.Equal(1, fixture.CountLive("authoring_runs"));
        Assert.Equal(attempts, fixture.CountLive("authoring_run_attempts"));
        Assert.Equal(requests, fixture.Scalar<int>("SELECT COUNT(*) FROM authoring_runs WHERE RequestJson IS NOT NULL"));
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
    }

    [Fact]
    public async Task AllowlistedMetadataDoesNotChangeProtectionButExistingZulipIdsRemainProtected()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync(OriginalKeys);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        PreparedTicketPublicationProtectedInventory before = await fixture.ReadCurrentAsync();
        PreparedTicketPublicationEnrichmentInput input = PreparedTicketPublicationProtectionReader.CreateRecipeInput(baseline, before);
        fixture.Execute(
            """
            UPDATE prepared_ticket_hydration
            SET Reporter = 'New reporter', Assignee = NULL, SourceProject = 'FHIR',
                SourceLastSuccessfulRefreshAt = '2026-09-15T12:00:00.0000000+00:00',
                SourceContentRevision = 100, PublicDisplayNamePolicyVersion = 1,
                HydratedAt = '2026-09-15T12:00:00.0000000+00:00';
            UPDATE prepared_jira_hydration
            SET Reporter = 'New reporter', Assignee = NULL, PublicDisplayNamePolicyVersion = 1,
                UpdatedAt = '2026-09-15T12:00:00.0000000+00:00'
            WHERE JiraKey = TicketKey;
            DELETE FROM prepared_ticket_in_person_requesters;
            INSERT INTO prepared_ticket_in_person_requesters(TicketKey, DisplayName, PublicDisplayNamePolicyVersion)
            VALUES('FHIR-701', 'New requester', 1);
            UPDATE prepared_zulip_hydration
            SET StreamId = 9, StreamName = 'new stream', Topic = 'new topic', MessageCount = 4,
                FirstMessageAt = NULL, LastMessageAt = NULL, FirstMessageExcerpt = 'new excerpt',
                Url = 'https://chat.fhir.org/#narrow/stream/9/topic/new',
                HydratedAt = '2026-09-15T12:00:00.0000000+00:00',
                HydrationStatus = 'unresolved', HydrationReason = 'specific failure'
            WHERE ZulipThreadId = 'stream::topic';
            """);

        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();
        Assert.Equal(before.ProtectedContentFingerprint, current.ProtectedContentFingerprint);
        Assert.Equal(before.RetainedGroupingFingerprint, current.RetainedGroupingFingerprint);
        PreparedTicketPublicationProtectionReader.Compare(baseline, current);
        PreparedTicketPublicationProtectionReader.ValidateFrozen(input, current);
        fixture.Execute("UPDATE prepared_zulip_hydration SET Id = 'replaced-hydration' WHERE RowId = 1");
        current = await fixture.ReadCurrentAsync();
        Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
            PreparedTicketPublicationProtectionReader.ValidateFrozen(input, current));
        Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
            PreparedTicketPublicationProtectionReader.Compare(baseline, current));
    }

    [Theory]
    [InlineData("12345", true)]
    [InlineData("not-accepted", false)]
    public async Task MissingZulipHydrationMayBeAddedOnlyForFrozenAcceptedReferences(string reference, bool allowed)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync(OriginalKeys);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        PreparedTicketPublicationEnrichmentInput input = PreparedTicketPublicationProtectionReader.CreateRecipeInput(
            baseline, await fixture.ReadCurrentAsync());
        Assert.Null(input.ZulipReferences.Single(value => value.TicketKey == "FHIR-701" && value.Reference == "12345").HydrationId);
        fixture.Execute(
            """
            INSERT INTO prepared_zulip_hydration(Id, TicketKey, ZulipThreadId, HydratedAt, HydrationStatus)
            VALUES('new-hydration', 'FHIR-701', @reference, '2026-09-15T12:00:00.0000000+00:00', 'unresolved')
            """,
            ("@reference", reference));
        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();
        if (allowed)
        {
            PreparedTicketPublicationProtectionReader.Compare(baseline, current);
            PreparedTicketPublicationProtectionReader.ValidateFrozen(input, current);
        }
        else
        {
            Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
                PreparedTicketPublicationProtectionReader.Compare(baseline, current));
            Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
                PreparedTicketPublicationProtectionReader.ValidateFrozen(input, current));
        }
    }

    [Fact]
    public async Task FrozenInventoryAllowsNewRunProvenanceButProtectsEveryHistoricalValue()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync(OriginalKeys);
        PreparedTicketPublicationBaselineReader reader = fixture.CreateBaselineReader();
        PreparedTicketPublicationBaseline baseline = await reader.ReadAsync(source.Run.Id);
        AuthoringRunRecord run = await fixture.Store.CreateMaintenanceRunAsync(
            "jira-fhir", (connection, ct) => reader.CreateSelectionAsync(connection, baseline, ct),
            AuthoringRunPurposeValues.PublicationRefresh, databaseOnly: false, sourceRunId: source.Run.Id);
        AuthoringMaintenanceRunRequest envelope = Assert.IsType<AuthoringMaintenanceRunRequest>(
            AuthoringMaintenanceRunRequest.Parse(run.RequestJson));
        PreparedTicketPublicationEnrichmentInput input = PreparedTicketPublicationEnrichmentContract.ParseInput(envelope.RecipeInputJson);
        fixture.Execute(
            """
            INSERT INTO authoring_run_input_provenance(RunId, Source, LatestSuccessfulRefreshAt, ContentRevision, CapturedAt)
            VALUES(@runId, 'jira', '2026-09-15T12:00:00.0000000+00:00', 100, '2026-09-15T12:00:00.0000000+00:00');
            INSERT INTO prepared_ticket_publication_refresh_receipts(
                RunId, StageId, InputFingerprint, CorpusFingerprint, SourceLastSuccessfulRefreshAt,
                SourceContentRevision, PublicDisplayNamePolicyVersion, AppliedAt)
            VALUES(@runId, 'new-stage', 'input', 'corpus', '2026-09-15T12:00:00.0000000+00:00', 100, 1,
                '2026-09-15T12:00:00.0000000+00:00');
            """,
            ("@runId", run.Id));
        PreparedTicketPublicationProtectedInventory current = await fixture.ReadCurrentAsync();
        PreparedTicketPublicationProtectionReader.ValidateFrozen(input, current, run.Id);
        Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
            PreparedTicketPublicationProtectionReader.ValidateFrozen(input, current));
        fixture.Execute("UPDATE authoring_run_input_provenance SET ContentRevision = 101 WHERE RunId = @runId",
            ("@runId", source.Run.Id));
        current = await fixture.ReadCurrentAsync();
        Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
            PreparedTicketPublicationProtectionReader.ValidateFrozen(input, current, run.Id));
    }

    [Fact]
    public async Task FrozenInventoryProtectsPrivateHistoricalCoordinatesAndCompletedRunLifecycle()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-701");
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        PreparedTicketPublicationEnrichmentInput input =
            PreparedTicketPublicationProtectionReader.CreateRecipeInput(baseline, await fixture.ReadCurrentAsync());
        foreach ((string table, string column) in new (string, string)[]
        {
            ("authoring_runs", "RowId"),
            ("authoring_runs", "Status"),
            ("authoring_runs", "CompletedAt"),
            ("authoring_runs", "SnapshotId"),
            ("authoring_runs", "Purpose"),
            ("authoring_runs", "SourceRunId"),
            ("authoring_runs", "RequestJson"),
            ("authoring_run_items", "RowId"),
            ("authoring_run_items", "CurrentOperationId"),
            ("authoring_result_receipts", "RowId"),
            ("prepared_ticket_authoring_state", "UpdatedAt"),
            ("prepared_ticket_run_item_partitions", "CapturedAt"),
        })
        {
            using SqliteConnection connection = fixture.Database.OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "BEGIN IMMEDIATE";
            command.ExecuteNonQuery();
            try
            {
                command.CommandText =
                    $"""
                    UPDATE "{table}" SET "{column}" = CASE typeof("{column}")
                    WHEN 'integer' THEN "{column}" + 100 ELSE COALESCE("{column}", '') || ' [changed]' END
                    """;
                Assert.Equal(1, command.ExecuteNonQuery());
                PreparedTicketPublicationProtectedInventory current =
                    await PreparedTicketPublicationProtectionReader.ReadCurrentAsync(connection);
                Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
                    PreparedTicketPublicationProtectionReader.ValidateFrozen(input, current));
            }
            finally
            {
                command.CommandText = "ROLLBACK";
                command.ExecuteNonQuery();
            }
        }
    }

    [Fact]
    public async Task FrozenInventoryProtectsExistingInternalApplyReceiptsAndCertifications()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync(OriginalKeys);
        fixture.MakeGroupingReceiptsLegacy(source.Run.Id);
        PreparedTicketPublicationRefreshService legacyService =
            fixture.CreateRefreshService(MetadataFetcher.For(source.ExpectedRevisions));
        PreparedTicketPublicationRefreshResult refresh = await legacyService.StartAsync(source.Run.Id);
        Assert.NotNull(await fixture.CreatePostProcessor(legacyService).FinalizeRunAsync(refresh.Run.RunId));
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(refresh.Run.RunId);
        PreparedTicketPublicationEnrichmentInput input =
            PreparedTicketPublicationProtectionReader.CreateRecipeInput(baseline, await fixture.ReadCurrentAsync());
        foreach ((string table, string columns) in new Dictionary<string, string>
        {
            ["prepared_ticket_publication_refresh_receipts"] =
                "RowId RunId StageId InputFingerprint CorpusFingerprint SourceLastSuccessfulRefreshAt SourceContentRevision PublicDisplayNamePolicyVersion AppliedAt",
            ["prepared_ticket_partition_certifications"] =
                "RowId RunId StageId PartitionKey InputFingerprint SourceRunId SourceStageId SourceInputFingerprint OutputFingerprint CertifiedAt",
        })
        {
            Assert.Single(input.ProtectedRows, row => row.Table == table);
            foreach (string column in columns.Split(' '))
            {
                using SqliteConnection connection = fixture.Database.OpenConnection();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "BEGIN IMMEDIATE";
                command.ExecuteNonQuery();
                try
                {
                    command.CommandText =
                        $"""
                        UPDATE "{table}" SET "{column}" = CASE typeof("{column}")
                        WHEN 'integer' THEN "{column}" + 100 ELSE "{column}" || ' [changed]' END
                        """;
                    Assert.Equal(1, command.ExecuteNonQuery());
                    PreparedTicketPublicationProtectedInventory current =
                        await PreparedTicketPublicationProtectionReader.ReadCurrentAsync(connection);
                    Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
                        PreparedTicketPublicationProtectionReader.ValidateFrozen(input, current));
                }
                finally
                {
                    command.CommandText = "ROLLBACK";
                    command.ExecuteNonQuery();
                }
            }
        }
    }

    [Fact]
    public async Task CanonicalInventoryDoesNotDependOnSqliteEnumerationOrder()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync(OriginalKeys);
        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);
        PreparedTicketPublicationProtectedInventory forward = await fixture.ReadCurrentAsync();
        PreparedTicketPublicationProtectedInventory reverse = await fixture.ReadCurrentAsync(reverseEnumeration: true);
        Assert.Equal(forward.CorpusFingerprint, reverse.CorpusFingerprint);
        Assert.Equal(forward.ProtectedContentFingerprint, reverse.ProtectedContentFingerprint);
        Assert.Equal(forward.RetainedGroupingFingerprint, reverse.RetainedGroupingFingerprint);
        Assert.Equal(
            PreparedTicketPublicationEnrichmentContract.SerializeInput(
                PreparedTicketPublicationProtectionReader.CreateRecipeInput(baseline, forward)),
            PreparedTicketPublicationEnrichmentContract.SerializeInput(
                PreparedTicketPublicationProtectionReader.CreateRecipeInput(baseline, reverse)));
    }

    [Fact]
    public void ProtectionCatalog_RequiresEveryColumnToBeClassified()
    {
        foreach (int version in PreparedTicketSnapshotSchemaResolver.SupportedVersions)
        {
            AuthoringSnapshotSchemaCatalog catalog = PreparedTicketSnapshotSchemaResolver.Resolve(version);
            PreparedTicketPublicationProtectionReader.ValidateCatalog(catalog);
            foreach (AuthoringSnapshotTableSchema table in catalog.Tables)
            {
                Assert.All(table.Columns, column =>
                    Assert.True(Enum.IsDefined(PreparedTicketPublicationProtectionReader.ClassifyColumn(table.Name, column))));
                AuthoringSnapshotSchemaCatalog extended = new(
                    version,
                    catalog.Tables.Select(value => value.Name == table.Name
                        ? new AuthoringSnapshotTableSchema(value.Name, [.. value.Columns, "FutureUnclassifiedValue"]) : value),
                    catalog.CountedTables);
                Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
                    PreparedTicketPublicationProtectionReader.ValidateCatalog(extended));
            }
        }
        Assert.Throws<PreparedTicketPublicationProtectionException>(() =>
            PreparedTicketPublicationProtectionReader.ClassifyColumn("future_authored_table", "Id"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task BaselineValidatesSupportedCatalogsOnPrivateImmutableCopies(int schemaVersion)
    {
        using Fixture fixture = new(richGraph: true, schemaVersion: schemaVersion);
        SourceResult source = await fixture.CreateSourceRunAsync(OriginalKeys);
        string[] before = PrivateCopies();
        string path = Path.Combine(fixture.SnapshotDirectory, source.Descriptor.FileName);

        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);

        Assert.Equal(source.Run.Id, baseline.Source.RunId);
        Assert.Equal(source.Descriptor.SnapshotId, baseline.Source.SnapshotId);
        Assert.Equal(source.Descriptor.Sha256, baseline.Source.SnapshotSha256);
        Assert.Equal(source.Descriptor.AuthoringEpoch, baseline.Source.AuthoringEpoch);
        Assert.Equal(source.Descriptor.Sequence, baseline.Source.Sequence);
        Assert.Equal(source.Descriptor.SizeBytes, baseline.Source.SizeBytes);
        Assert.Equal(schemaVersion, baseline.Source.SchemaVersion);
        Assert.Equal(6, baseline.Source.ExportedTicketCount);
        Assert.Equal(before, PrivateCopies());
        Assert.Equal(source.Descriptor.Sha256, await SqliteReviewSnapshotWriter.ComputeSha256Async(path));
        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        PreparedTicketPublicationProtectionReader.Compare(baseline, await fixture.ReadCurrentAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("digest")]
    [InlineData("size")]
    [InlineData("epoch")]
    [InlineData("unknown-schema")]
    [InlineData("service")]
    [InlineData("run")]
    [InlineData("not-ready")]
    [InlineData("outside-location")]
    [InlineData("unclassified-column")]
    [InlineData("missing-column")]
    [InlineData("unexpected-view")]
    [InlineData("unexpected-table")]
    public async Task BaselineRefusesUntrustedSnapshotsWithoutReconciliationOrLeakedCopies(string damage)
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-701");
        string path = Path.Combine(fixture.SnapshotDirectory, source.Descriptor.FileName);
        string[] copiesBefore = PrivateCopies();
        switch (damage)
        {
            case "missing":
                File.Delete(path);
                break;
            case "corrupt":
                File.WriteAllText(path, "invalid SQLite bytes");
                break;
            case "digest":
                fixture.Execute("UPDATE authoring_review_snapshots SET ChecksumSha256 = @digest", ("@digest", new string('0', 64)));
                break;
            case "size":
                fixture.Execute("UPDATE authoring_review_snapshots SET SizeBytes = SizeBytes + 1");
                break;
            case "epoch":
                fixture.Execute("UPDATE authoring_review_snapshots SET AuthoringEpoch = AuthoringEpoch + 1");
                break;
            case "unknown-schema":
                fixture.Execute("UPDATE authoring_review_snapshots SET SchemaVersion = 99");
                break;
            case "service":
                fixture.Execute("UPDATE authoring_review_snapshots SET ProcessorKind = 'other-service'");
                break;
            case "run":
                fixture.Execute("UPDATE authoring_review_snapshots SET RunId = 'other-run'");
                break;
            case "not-ready":
                fixture.Execute("UPDATE authoring_review_snapshots SET Status = 'promoted'");
                break;
            case "outside-location":
                string outside = Path.Combine(Path.GetDirectoryName(fixture.SnapshotDirectory)!, "outside.db");
                File.Copy(path, outside);
                fixture.Execute("UPDATE authoring_review_snapshots SET Path = @path", ("@path", outside));
                break;
            case "unclassified-column":
                await MutateSnapshotAsync(fixture, source, "ALTER TABLE prepared_tickets ADD COLUMN UnclassifiedAuthoredValue TEXT");
                break;
            case "missing-column":
                await MutateSnapshotAsync(fixture, source, "ALTER TABLE prepared_tickets DROP COLUMN ProposalA");
                break;
            case "unexpected-view":
                await MutateSnapshotAsync(fixture, source, "CREATE VIEW unexpected_graph AS SELECT ProposalA FROM prepared_tickets");
                break;
            case "unexpected-table":
                await MutateSnapshotAsync(fixture, source, "CREATE TABLE sqlitex_unclassified(Value TEXT)");
                break;
        }
        string statusBefore = fixture.Scalar<string>("SELECT Status FROM authoring_review_snapshots");

        await Assert.ThrowsAsync<PreparedTicketPublicationProtectionException>(() =>
            fixture.CreateBaselineReader().ReadAsync(source.Run.Id));

        Assert.Equal(copiesBefore, PrivateCopies());
        Assert.Equal(statusBefore, fixture.Scalar<string>("SELECT Status FROM authoring_review_snapshots"));
        Assert.Equal(0, fixture.Scalar<int>("SELECT COUNT(*) FROM authoring_review_snapshots WHERE Error IS NOT NULL"));
        Assert.Equal(1, fixture.CountLive("authoring_runs"));
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
    }

    [Fact]
    public async Task BaselineRejectsAmbiguousAcceptedReceiptEvenWithMatchingTrustedDigestAndCounts()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-701");
        string[] copiesBefore = PrivateCopies();
        await MutateSnapshotAsync(fixture, source,
            """
            INSERT INTO authoring_runs(Id, ProcessorKind, AuthoringEpoch, Status, DatabaseOnly, TotalItems, CreatedAt, StartedAt, CompletedAt, SnapshotId)
            SELECT 'ambiguous-run', ProcessorKind, AuthoringEpoch, Status, DatabaseOnly, TotalItems, CreatedAt, StartedAt, CompletedAt, SnapshotId
            FROM authoring_runs;
            INSERT INTO authoring_run_items(Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision, Status, AcceptedReceiptId, AttemptCount, CreatedAt, StartedAt, CompletedAt)
            SELECT 'ambiguous-item', 'ambiguous-run', BusinessKey, ItemKind, ExpectedSourceRevision, Status, 'ambiguous-receipt', AttemptCount, CreatedAt, StartedAt, CompletedAt
            FROM authoring_run_items;
            INSERT INTO authoring_result_receipts(Id, OperationId, RunId, RunItemId, BusinessKey, ContentHash, ExpectedSourceRevision, ObservedSourceRevision, AuthoringEpoch, PersistedAt)
            SELECT 'ambiguous-receipt', 'ambiguous-operation', 'ambiguous-run', 'ambiguous-item', BusinessKey, ContentHash, ExpectedSourceRevision, ObservedSourceRevision, AuthoringEpoch, PersistedAt
            FROM authoring_result_receipts;
            UPDATE authoring_snapshot_provenance SET ReceiptCount = 2;
            """);
        fixture.Execute("UPDATE authoring_review_snapshots SET ReceiptCount = 2");

        PreparedTicketPublicationProtectionException error = await Assert.ThrowsAsync<PreparedTicketPublicationProtectionException>(
            () => fixture.CreateBaselineReader().ReadAsync(source.Run.Id));

        Assert.Equal("invalid-accepted-graph", error.FailureCode);
        Assert.Equal(copiesBefore, PrivateCopies());
        Assert.Equal("ready", fixture.Scalar<string>("SELECT Status FROM authoring_review_snapshots"));
    }

    [Fact]
    public async Task BaselinePreservesCancellationAndCurrentCatalogFailsClosed()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-701");
        string[] before = PrivateCopies();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.CreateBaselineReader().ReadAsync(source.Run.Id, cancellation.Token));
        Assert.Equal(before, PrivateCopies());
        fixture.Execute("ALTER TABLE prepared_ticket_related_zulip ADD COLUMN FutureJustification TEXT");
        await Assert.ThrowsAsync<PreparedTicketPublicationProtectionException>(() => fixture.ReadCurrentAsync());
        Assert.Equal("ready", fixture.Scalar<string>("SELECT Status FROM authoring_review_snapshots"));
    }

    [Fact]
    public async Task InvalidUtf8CannotBeSilentlyReplacedInProtectedText()
    {
        using Fixture fixture = new();
        _ = await fixture.CreateSourceRunAsync("FHIR-701");
        fixture.Execute("UPDATE prepared_jira_hydration SET DescriptionHtml = CAST(X'80' AS TEXT)");
        PreparedTicketPublicationProtectionException error =
            await Assert.ThrowsAsync<PreparedTicketPublicationProtectionException>(() => fixture.ReadCurrentAsync());
        Assert.Equal("invalid-accepted-graph", error.FailureCode);
        Assert.Contains("UTF-8", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BaselineUsesOnlyTheSnapshotSelectedByItsSourceRunAndDoesNotReconcileOthers()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-701");
        fixture.Execute(
            """
            INSERT INTO authoring_review_snapshots(
                Id, ProcessorKind, RunId, AuthoringEpoch, Sequence, SchemaVersion, Status,
                TempPath, Path, ChecksumSha256, SizeBytes, ItemCount, ReceiptCount, TableCountsJson, CreatedAt)
            SELECT 'unselected-snapshot', ProcessorKind, RunId, AuthoringEpoch, Sequence + 1, SchemaVersion, Status,
                TempPath || '.unselected', Path || '.unselected.db', ChecksumSha256, SizeBytes,
                ItemCount, ReceiptCount, TableCountsJson, CreatedAt
            FROM authoring_review_snapshots
            """);

        PreparedTicketPublicationBaseline baseline = await fixture.CreateBaselineReader().ReadAsync(source.Run.Id);

        Assert.Equal(source.Descriptor.SnapshotId, baseline.Source.SnapshotId);
        Assert.Equal("ready", fixture.Scalar<string>(
            "SELECT Status FROM authoring_review_snapshots WHERE Id = 'unselected-snapshot'"));
        Assert.Equal(2, fixture.CountLive("authoring_review_snapshots"));
    }

    private static async Task AssertMutationRefusedAsync(
        Fixture fixture,
        PreparedTicketPublicationBaseline baseline,
        string table,
        string column,
        string filter = "1 = 1")
    {
        using SqliteConnection connection = fixture.Database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "BEGIN IMMEDIATE";
        command.ExecuteNonQuery();
        try
        {
            command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            long count = (long)command.ExecuteScalar()!;
            command.CommandText =
                $"""
                UPDATE "{table}"
                SET "{column}" = CASE typeof("{column}")
                    WHEN 'integer' THEN "{column}" + 100
                    ELSE COALESCE("{column}", '') || ' [tampered]' END
                WHERE rowid = (SELECT rowid FROM "{table}" WHERE {filter} ORDER BY rowid LIMIT 1)
                """;
            Assert.Equal(1, command.ExecuteNonQuery());
            command.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
            Assert.Equal(count, (long)command.ExecuteScalar()!);
            Exception? error = await Record.ExceptionAsync(async () =>
            {
                PreparedTicketPublicationProtectedInventory current =
                    await PreparedTicketPublicationProtectionReader.ReadCurrentAsync(connection);
                PreparedTicketPublicationProtectionReader.Compare(baseline, current);
            });
            Assert.True(error is PreparedTicketPublicationProtectionException,
                $"Mutation of {table}.{column} was not refused by preservation: {error}");
        }
        finally
        {
            command.CommandText = "ROLLBACK";
            command.ExecuteNonQuery();
        }
    }

    private static async Task MutateSnapshotAsync(Fixture fixture, SourceResult source, string sql)
    {
        string path = Path.Combine(fixture.SnapshotDirectory, source.Descriptor.FileName);
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        fixture.Execute(
            "UPDATE authoring_review_snapshots SET ChecksumSha256 = @digest, SizeBytes = @size WHERE Id = @id",
            ("@digest", await SqliteReviewSnapshotWriter.ComputeSha256Async(path)),
            ("@size", new FileInfo(path).Length),
            ("@id", source.Descriptor.SnapshotId));
    }

    private static string[] PrivateCopies()
        => Directory.GetDirectories(Path.GetTempPath(), "fhir-augury-publication-baseline-*")
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();

    private sealed class RecordingLogger : ILogger<PreparedTicketPublicationBaselineReader>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    // Independent expected-value coverage, not a reflection of the reader's
    // classifications. Each column is changed in an actual SQLite transaction
    // without changing row counts, and preservation must refuse it.
    private static readonly IReadOnlyDictionary<string, string> RequiredProtectedColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["prepared_tickets"] = "RowId Id Key RequestSummary CommentSummary LinkedTicketSummary RelatedTicketSummary RelatedZulipSummary RelatedGitHubSummary ExistingProposed ProposalA ProposalAJustification ProposalAImpact ProposalB ProposalBJustification ProposalBImpact ProposalC ProposalCJustification Recommendation RecommendationJustification SavedAt",
            ["prepared_ticket_repos"] = "RowId Id TicketKey Repo RepoCategory Justification",
            ["prepared_ticket_related_jira"] = "RowId Id TicketKey AssociatedTicketKey LinkType Justification",
            ["prepared_ticket_related_zulip"] = "RowId Id TicketKey ZulipThreadId Justification",
            ["prepared_ticket_related_github"] = "RowId Id TicketKey GitHubItemId Justification",
            ["prepared_ticket_hydration"] = "RowId Id TicketKey Priority Resolution ResolutionDescriptionPlain Specification RaisedInVersion SelectedBallot ChangeCategory Impact Labels CommentCount DescriptionPlain DescriptionHtml ResolutionDescriptionHtml CreatedAt RelatedArtifactsRaw RelatedPagesRaw HydrationStatus HydrationReason",
            ["prepared_jira_hydration"] = "RowId Id TicketKey JiraKey Title Status Type Priority Resolution ResolutionDescriptionPlain WorkGroup WorkGroupClean Specification Url DescriptionHtml ResolutionDescriptionHtml CreatedAt RelatedArtifactsRaw RelatedPagesRaw HydratedAt HydrationStatus HydrationReason",
            ["prepared_zulip_hydration"] = "RowId Id TicketKey ZulipThreadId",
            ["prepared_github_hydration"] = "RowId Id TicketKey GitHubItemId Owner Repo Number Path Title State IsPullRequest Labels UpdatedAt Url HydratedAt HydrationStatus HydrationReason",
            ["prepared_repo_hydration"] = "RowId Id TicketKey Repo Description WorkGroup Specification CategoryDetail Url HydratedAt HydrationStatus HydrationReason",
            ["prepared_ticket_jira_xref"] = "RowId Id TicketKey JiraKey Source",
            ["prepared_ticket_jira_content"] = "RowId TicketKey DescriptionHtml ResolutionDescriptionHtml",
            ["prepared_ticket_artifacts"] = "RowId TicketKey Value",
            ["prepared_ticket_pages"] = "RowId TicketKey Value",
            ["authoring_runs"] = "Id ProcessorKind AuthoringEpoch DatabaseOnly TotalItems CreatedAt StartedAt",
            ["authoring_run_items"] = "Id RunId BusinessKey ItemKind ExpectedSourceRevision Status AcceptedReceiptId AttemptCount CreatedAt StartedAt CompletedAt",
            ["authoring_result_receipts"] = "Id OperationId RunId RunItemId BusinessKey ContentHash ExpectedSourceRevision ObservedSourceRevision AuthoringEpoch PersistedAt",
            ["authoring_run_input_provenance"] = "RowId RunId Source LatestSuccessfulRefreshAt ContentRevision CapturedAt",
            ["prepared_ticket_partition_receipts"] = "RunId StageId PartitionKey InputFingerprint TopicRows TopicGroupRows MemberRows PersistedAt",
            ["jira_review_workgroups"] = "RowId Code Name NameClean UpdatedAt",
        };
}
