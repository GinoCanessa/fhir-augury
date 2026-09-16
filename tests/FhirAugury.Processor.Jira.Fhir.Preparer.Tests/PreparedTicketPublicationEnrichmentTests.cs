using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FhirAugury.Common.Api;
using FhirAugury.Common.Text;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database.Records;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.Data.Sqlite;
using static FhirAugury.Processor.Jira.Fhir.Preparer.Tests.PreparedTicketPublicationTestFixture;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

[Collection(PreparedTicketPublicationTestCollection.Name)]
public sealed class PreparedTicketPublicationEnrichmentTests
{
    [Fact]
    public async Task Apply_UpdatesOnlyAllowlistedFields()
    {
        using Fixture fixture = new(richGraph: true);
        fixture.BeforeSourceSnapshot = _ => fixture.Execute(
            """
            UPDATE prepared_ticket_hydration
            SET HydrationStatus = 'unresolved', HydrationReason = 'original parent detail failure';
            """);
        string[] keys = ["FHIR-10028", "FHIR-29212", "FHIR-803", "FHIR-804", "FHIR-805", "FHIR-806"];
        SourceResult source = await fixture.CreateSourceRunAsync(keys);
        string originalHash = await SnapshotHashAsync(fixture, source.Descriptor);
        string protectedBefore = ReadProtectedState(fixture);
        int attemptsBefore = fixture.CountLive("authoring_run_attempts");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.TransformMetadata = result => result with
        {
            InPersonRequesters = [$" Requester {result.TicketKey} ", $"requester {result.TicketKey}", "private@example.org"],
        };
        StageContext stage = await CreateStageAsync(fixture, source, fetcher);
        PreparedTicketPublicationEnrichmentBatch batch = await fixture.CreateEnricher(fetcher).FetchAsync(stage.Input);

        PreparedTicketPublicationRefreshReceiptRecord receipt = await ApplyAsync(fixture, stage, batch);

        Assert.Equal(stage.Lease.StageId, receipt.StageId);
        Assert.Equal(stage.Fingerprint, receipt.InputFingerprint);
        Assert.Equal(stage.Input.CorpusFingerprint, receipt.CorpusFingerprint);
        Assert.Equal(protectedBefore, ReadProtectedState(fixture));
        Assert.Equal(keys.Length, fetcher.CallCount);
        Assert.Equal(2, fetcher.ZulipCallCount);
        Assert.Equal(["12345", "stream::topic"], fetcher.ZulipCalls.Select(call => call.Reference).Order().ToArray());
        Assert.DoesNotContain(fetcher.ZulipCalls, call => call.Reference == "unaccepted-reference");
        Assert.Equal(keys.Length * 2, batch.ZulipOutcomes.Count);
        Assert.Equal(
            stage.Input.ZulipReferences.Select(reference => (reference.AssociationId, reference.TicketKey, reference.Reference)).Order(),
            batch.ZulipOutcomes.Select(outcome =>
                (outcome.AssociationId, outcome.Hydration.TicketKey, outcome.Hydration.ZulipThreadId)).Order());
        foreach (string key in keys)
        {
            Assert.Equal($"Reporter {key}", fixture.Scalar<string>(
                $"SELECT Reporter FROM prepared_ticket_hydration WHERE TicketKey = '{key}'"));
            Assert.Equal($"Assignee {key}", fixture.Scalar<string>(
                $"SELECT Assignee FROM prepared_jira_hydration WHERE TicketKey = '{key}' AND JiraKey = TicketKey"));
            Assert.Equal("2026-09-01T00:00:00.0000000+00:00", fixture.Scalar<string>(
                $"SELECT UpdatedAt FROM prepared_jira_hydration WHERE TicketKey = '{key}' AND JiraKey = TicketKey"));
            Assert.Equal($"Requester {key}", fixture.Scalar<string>(
                $"SELECT DisplayName FROM prepared_ticket_in_person_requesters WHERE TicketKey = '{key}'"));
            Assert.Equal("original parent detail failure", fixture.Scalar<string>(
                $"SELECT HydrationReason FROM prepared_ticket_hydration WHERE TicketKey = '{key}'"));
            Assert.Equal(source.ExpectedRevisions[key], fixture.Scalar<string>(
                $"SELECT ExpectedSourceRevision FROM authoring_run_items WHERE RunId = '{stage.RunId}' AND BusinessKey = '{key}'"));
            Assert.Equal(ZulipReferenceBacking.TypedResolver, ReadZulipReason(fixture, key, "12345").Metadata!.Backing);
            Assert.EndsWith("/near/12345", fixture.Scalar<string>(
                $"SELECT Url FROM prepared_zulip_hydration WHERE TicketKey = '{key}' AND ZulipThreadId = '12345'"), StringComparison.Ordinal);
        }
        Assert.Equal(
            keys.Length,
            fixture.Scalar<int>("SELECT COUNT(*) FROM prepared_ticket_in_person_requesters"));
        PreparedTicketPublicationProtectionReader.ValidateFrozen(stage.Input, await fixture.ReadCurrentAsync(), stage.RunId);

        await fixture.Store.CompleteRunStageAsync(stage.Lease.StageId, stage.Lease.LeaseId);
        CountingGroupingDispatcher grouping = new(fixture.Database);
        AuthoringSnapshotDescriptor snapshot = Assert.IsType<AuthoringSnapshotDescriptor>(
            await fixture.CreatePostProcessor(stage.Service, grouping).FinalizeRunAsync(stage.RunId));
        Assert.Equal(0, grouping.CallCount);
        Assert.Equal(attemptsBefore, fixture.CountLive("authoring_run_attempts"));
        Assert.Equal(protectedBefore, ReadProtectedState(fixture));
        Assert.Equal(originalHash, await SnapshotHashAsync(fixture, source.Descriptor));
        Assert.NotEqual(source.Descriptor.SnapshotId, snapshot.SnapshotId);
        Assert.Equal(PreparedTicketSnapshotSchemaV3.Version, snapshot.SchemaVersion);
        Assert.Equal(PreparedTicketPublicationContract.CurrentVersion, snapshot.PublicationProof!.ContractVersion);
        Assert.Equal("jira", snapshot.PublicationProof.SourceName);
        Assert.Equal(901, snapshot.PublicationProof.SourceContentRevision);
        Assert.Equal(keys.Length, fetcher.CallCount);
        Assert.Equal(2, fetcher.ZulipCallCount);
    }

    [Theory]
    [InlineData("missing-jira")]
    [InlineData("extra-jira")]
    [InlineData("duplicate-jira")]
    [InlineData("wrong-jira")]
    [InlineData("missing-zulip")]
    [InlineData("extra-zulip")]
    [InlineData("duplicate-zulip")]
    [InlineData("wrong-association")]
    [InlineData("wrong-reference")]
    [InlineData("cross-ticket")]
    [InlineData("invalid-envelope")]
    [InlineData("zero-message-success")]
    public async Task Apply_RejectsMissingExtraOrDuplicateOutcomes(string change)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801", "FHIR-802");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        StageContext stage = await CreateStageAsync(fixture, source, fetcher);
        PreparedTicketPublicationEnrichmentBatch batch = await fixture.CreateEnricher(fetcher).FetchAsync(stage.Input);
        PreparedTicketPublicationZulipOutcome first = batch.ZulipOutcomes[0];
        batch = change switch
        {
            "missing-jira" => batch with { JiraMetadata = batch.JiraMetadata.Take(1).ToArray() },
            "extra-jira" => batch with { JiraMetadata = [.. batch.JiraMetadata, batch.JiraMetadata[0] with { TicketKey = "FHIR-999" }] },
            "duplicate-jira" => batch with { JiraMetadata = [batch.JiraMetadata[0], batch.JiraMetadata[0]] },
            "wrong-jira" => batch with { JiraMetadata = [batch.JiraMetadata[0], batch.JiraMetadata[1] with { TicketKey = "FHIR-999" }] },
            "missing-zulip" => batch with { ZulipOutcomes = batch.ZulipOutcomes.Skip(1).ToArray() },
            "extra-zulip" => batch with { ZulipOutcomes = [.. batch.ZulipOutcomes, first] },
            "duplicate-zulip" => batch with { ZulipOutcomes = [first, .. batch.ZulipOutcomes.Skip(1).SkipLast(1), first] },
            "wrong-association" => batch with { ZulipOutcomes = [first with { AssociationId = "extra" }, .. batch.ZulipOutcomes.Skip(1)] },
            "wrong-reference" => batch with { ZulipOutcomes = [first with { Hydration = first.Hydration with { ZulipThreadId = "12346" } }, .. batch.ZulipOutcomes.Skip(1)] },
            "cross-ticket" => batch with { ZulipOutcomes = [first with { Hydration = first.Hydration with { TicketKey = "FHIR-802" } }, .. batch.ZulipOutcomes.Skip(1)] },
            "invalid-envelope" => batch with { ZulipOutcomes = [first with { Hydration = first.Hydration with { HydrationReason = "resolved" } }, .. batch.ZulipOutcomes.Skip(1)] },
            "zero-message-success" => batch with { ZulipOutcomes = [first with { Hydration = first.Hydration with { MessageCount = 0 } }, .. batch.ZulipOutcomes.Skip(1)] },
            _ => throw new InvalidOperationException("Unknown test case."),
        };
        string before = fixture.ReadPublicationMetadataState();
        string protectedBefore = ReadProtectedState(fixture);

        if (change.EndsWith("jira", StringComparison.Ordinal))
        {
            AuthoringConflictException error = await Assert.ThrowsAsync<AuthoringConflictException>(
                () => ApplyAsync(fixture, stage, batch));
            Assert.Equal(AuthoringConflictCode.StageFingerprintMismatch, error.Code);
        }
        else
        {
            PreparedTicketPublicationProtectionException error = await Assert.ThrowsAsync<PreparedTicketPublicationProtectionException>(
                () => ApplyAsync(fixture, stage, batch));
            Assert.Equal(PreparedTicketPublicationRefreshFailureCodes.InvalidEnrichmentBatch, error.FailureCode);
        }
        Assert.Equal(before, fixture.ReadPublicationMetadataState());
        Assert.Equal(protectedBefore, ReadProtectedState(fixture));
        Assert.Equal(0, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));
        Assert.Equal(2, fixture.CountLive("authoring_run_attempts"));
    }

    [Fact]
    public async Task FailedZulipLookup_RetainsQualifiedLastKnownContext()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        string contextBefore = ReadZulipContext(fixture);
        string protectedBefore = ReadProtectedState(fixture);
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.ZulipResults["stream::topic"] = MetadataFetcher.ZulipResult(
            "stream::topic", ZulipReferenceLookupOutcome.NotFound);

        _ = await RefreshAsync(fixture, source, fetcher);

        Assert.Equal(contextBefore, ReadZulipContext(fixture));
        Assert.Equal(protectedBefore, ReadProtectedState(fixture));
        ZulipReferenceHydrationReasonReadResult reason = ReadZulipReason(fixture);
        Assert.True(reason.HasSourceBacking);
        Assert.Equal(ZulipReferenceBacking.LegacyIndexedContext, reason.Metadata!.Backing);
        Assert.Equal(ZulipReferenceLookupOutcome.NotFound, reason.Metadata.LatestOutcome);
        Assert.Equal("unresolved", fixture.Scalar<string>(
            "SELECT HydrationStatus FROM prepared_zulip_hydration WHERE ZulipThreadId = 'stream::topic'"));
        Assert.Equal("2002-02-02T00:00:00.0000000+00:00", fixture.Scalar<string>(
            "SELECT HydratedAt FROM prepared_zulip_hydration WHERE ZulipThreadId = 'stream::topic'"));
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("missing-count")]
    [InlineData("missing-stream")]
    [InlineData("missing-topic")]
    [InlineData("url-only")]
    public async Task LegacyZeroMessageUrl_IsNotQualifiedAsBacked(string evidence)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        string assignment = evidence switch
        {
            "zero" => "MessageCount = 0",
            "missing-count" => "MessageCount = NULL",
            "missing-stream" => "StreamName = ' '",
            "missing-topic" => "Topic = NULL",
            "url-only" => "MessageCount = NULL, StreamName = NULL, Topic = NULL",
            _ => throw new InvalidOperationException("Unknown test case."),
        };
        fixture.Execute($"UPDATE prepared_zulip_hydration SET {assignment}, HydrationStatus = 'resolved' WHERE ZulipThreadId = 'stream::topic'");
        string context = ReadZulipContext(fixture);
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.ZulipResults["stream::topic"] = MetadataFetcher.ZulipResult(
            "stream::topic", ZulipReferenceLookupOutcome.NotFound);

        _ = await RefreshAsync(fixture, source, fetcher);

        Assert.Equal(context, ReadZulipContext(fixture));
        ZulipReferenceHydrationReasonReadResult reason = ReadZulipReason(fixture);
        Assert.False(reason.HasSourceBacking);
        Assert.Equal(ZulipReferenceBacking.Unverified, reason.Metadata!.Backing);
        Assert.Equal(ZulipReferenceLookupOutcome.NotFound, reason.Metadata.LatestOutcome);
        Assert.Equal("unresolved", fixture.Scalar<string>(
            "SELECT HydrationStatus FROM prepared_zulip_hydration WHERE ZulipThreadId = 'stream::topic'"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task SuccessiveLookupFailures_PreserveBackingEvidence(bool typed, bool optionalDetails)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801", "FHIR-802");
        string originalHash = await SnapshotHashAsync(fixture, source.Descriptor);
        if (typed)
        {
            MetadataFetcher success = MetadataFetcher.For(source.ExpectedRevisions);
            success.ZulipResults["stream::topic"] = MetadataFetcher.ZulipResult("stream::topic", optionalDetails: optionalDetails);
            _ = await RefreshAsync(fixture, source, success);
        }
        string context = ReadZulipContext(fixture);
        AuthoringSnapshotDescriptor? previous = null;
        string? previousHash = null;
        foreach (ZulipReferenceLookupOutcome failure in new[]
        {
            ZulipReferenceLookupOutcome.NotFound, ZulipReferenceLookupOutcome.TransientFailure,
        })
        {
            MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
            fetcher.ZulipResults["stream::topic"] = MetadataFetcher.ZulipResult("stream::topic", failure);
            AuthoringSnapshotDescriptor snapshot = await RefreshAsync(fixture, source, fetcher);
            Assert.Equal(context, ReadZulipContext(fixture));
            ZulipReferenceHydrationReasonReadResult reason = ReadZulipReason(fixture);
            Assert.True(reason.HasSourceBacking);
            Assert.Equal(typed ? ZulipReferenceBacking.TypedResolver : ZulipReferenceBacking.LegacyIndexedContext, reason.Metadata!.Backing);
            Assert.Equal(failure, reason.Metadata.LatestOutcome);
            Assert.Equal(originalHash, await SnapshotHashAsync(fixture, source.Descriptor));
            if (previous is not null)
            {
                Assert.Equal(previousHash, await SnapshotHashAsync(fixture, previous));
            }
            previous = snapshot;
            previousHash = await SnapshotHashAsync(fixture, snapshot);
        }
    }

    [Theory]
    [InlineData("zulip-reference-v2:{}")]
    [InlineData("zulip-reference-v1:{broken")]
    public async Task MalformedPriorBacking_RemainsUnverifiedWithDiagnostic(string priorReason)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        fixture.Execute("UPDATE prepared_zulip_hydration SET HydrationReason = @reason WHERE ZulipThreadId = 'stream::topic'",
            ("@reason", priorReason));
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.ZulipResults["stream::topic"] = MetadataFetcher.ZulipResult("stream::topic", ZulipReferenceLookupOutcome.Timeout);

        _ = await RefreshAsync(fixture, source, fetcher);

        ZulipReferenceHydrationReasonReadResult reason = ReadZulipReason(fixture);
        Assert.False(reason.HasSourceBacking);
        Assert.Equal(ZulipReferenceBacking.Unverified, reason.Metadata!.Backing);
        Assert.Equal(ZulipReferenceLookupOutcome.Timeout, reason.Metadata.LatestOutcome);
        Assert.Contains(
            priorReason.StartsWith("zulip-reference-v2:", StringComparison.Ordinal)
                ? ZulipReferenceDiagnosticCode.UnknownOutcomeMetadataVersion
                : ZulipReferenceDiagnosticCode.MalformedOutcomeMetadata,
            reason.Metadata.Diagnostics);
    }

    [Fact]
    public async Task FailedLookup_DropsUnsafeUrlWithoutClaimingBacking()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        fixture.Execute("UPDATE prepared_zulip_hydration SET Url = 'javascript:alert(1)' WHERE ZulipThreadId = 'stream::topic'");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.ZulipResults["stream::topic"] = MetadataFetcher.ZulipResult("stream::topic", ZulipReferenceLookupOutcome.InvalidEnvelope);
        _ = await RefreshAsync(fixture, source, fetcher);
        Assert.False(ReadZulipReason(fixture).HasSourceBacking);
        Assert.Equal(ZulipReferenceBacking.None, ReadZulipReason(fixture).Metadata!.Backing);
        Assert.Equal(1, fixture.Scalar<int>(
            "SELECT Url IS NULL FROM prepared_zulip_hydration WHERE ZulipThreadId = 'stream::topic'"));
    }

    [Fact]
    public async Task OptionalDetailFailure_KeepsResolvedLinkAndExplicitDiagnostics()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        HydrationZulipRow resolved = MetadataFetcher.ZulipResult("12345", optionalDetails: false);
        fetcher.ZulipResults["12345"] = resolved with
        {
            HydrationReason = ZulipReferenceHydrationReason.Serialize(new()
            {
                Backing = ZulipReferenceBacking.TypedResolver,
                LatestOutcome = ZulipReferenceLookupOutcome.Resolved,
                Diagnostics = [ZulipReferenceDiagnosticCode.InvalidTimestamp],
            }),
        };

        _ = await RefreshAsync(fixture, source, fetcher);

        ZulipReferenceHydrationReasonReadResult reason = ReadZulipReason(fixture, reference: "12345");
        Assert.True(reason.HasSourceBacking);
        Assert.Equal(ZulipReferenceLookupOutcome.Resolved, reason.Metadata!.LatestOutcome);
        Assert.Equal([ZulipReferenceDiagnosticCode.InvalidTimestamp], reason.Metadata.Diagnostics);
        Assert.Equal(resolved.Url, fixture.Scalar<string>("SELECT Url FROM prepared_zulip_hydration WHERE ZulipThreadId = '12345'"));
        Assert.Equal("resolved", fixture.Scalar<string>("SELECT HydrationStatus FROM prepared_zulip_hydration WHERE ZulipThreadId = '12345'"));
        Assert.Equal(1, fixture.Scalar<int>(
            "SELECT MessageCount IS NULL AND FirstMessageAt IS NULL AND LastMessageAt IS NULL FROM prepared_zulip_hydration WHERE ZulipThreadId = '12345'"));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("generation")]
    [InlineData("people-generation")]
    [InlineData("typed-date-mismatch")]
    public async Task ChangedJiraRevisionOrGeneration_RefusesEntireBatch(string change)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801", "FHIR-802");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        switch (change)
        {
            case "revision":
                fetcher.ObservedRevisions["FHIR-802"] = "2026-09-02T00:00:00.0000000+00:00";
                break;
            case "generation":
                fetcher.ContentRevisions["FHIR-802"]++;
                break;
            case "people-generation":
                fetcher.OnJiraFetch = key =>
                {
                    if (key == "FHIR-802") fetcher.ContentRevisions[key]++;
                };
                fetcher.TransformMetadata = result => result with { Reporter = "New public person" };
                break;
            case "typed-date-mismatch":
                fetcher.UpdatedAtByTicket["FHIR-802"] = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
                break;
        }
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(fetcher);
        PreparedTicketPublicationRefreshResult run = await service.StartAsync(source.Run.Id);
        string before = fixture.ReadPublicationMetadataState();
        string protectedBefore = ReadProtectedState(fixture);
        string originalHash = await SnapshotHashAsync(fixture, source.Descriptor);
        CountingGroupingDispatcher grouping = new(fixture.Database);

        Assert.Null(await fixture.CreatePostProcessor(service, grouping).FinalizeRunAsync(run.Run.RunId));

        Assert.Equal(before, fixture.ReadPublicationMetadataState());
        Assert.Equal(protectedBefore, ReadProtectedState(fixture));
        Assert.Equal(originalHash, await SnapshotHashAsync(fixture, source.Descriptor));
        await AssertTerminalRefusalAsync(fixture, run.Run.RunId);
        Assert.Equal(0, grouping.CallCount);
        Assert.Equal(2, fixture.CountLive("authoring_run_attempts"));
        Assert.Equal(0, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));
        Assert.DoesNotContain(await fixture.Store.GetSnapshotRecordsAsync(), snapshot => snapshot.RunId == run.Run.RunId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingExplicitJiraDate_PreservesStoredDateWithoutUsingHash(bool storedDateMissing)
    {
        using Fixture fixture = new();
        fixture.BeforeSourceSnapshot = _ => fixture.Execute(
            "UPDATE prepared_jira_hydration SET UpdatedAt = @date",
            ("@date", storedDateMissing ? null : "2025-07-17T16:12:12.0000000-05:00"));
        SourceResult source = await fixture.CreateSourceRunAtAsync(null, "FHIR-801");
        Assert.Equal(64, source.ExpectedRevisions["FHIR-801"].Length);
        string before = ReadRows(fixture, "SELECT UpdatedAt FROM prepared_jira_hydration");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        Assert.Null(fetcher.UpdatedAtByTicket["FHIR-801"]);

        AuthoringSnapshotDescriptor snapshot = await RefreshAsync(fixture, source, fetcher);

        Assert.Equal(before, ReadRows(fixture, "SELECT UpdatedAt FROM prepared_jira_hydration"));
        await using SqliteConnection exported = await SqliteReviewSnapshotValidator.OpenReadOnlyAsync(
            Path.Combine(fixture.SnapshotDirectory, snapshot.FileName));
        using SqliteCommand command = exported.CreateCommand();
        command.CommandText = "SELECT UpdatedAt FROM prepared_jira_hydration WHERE TicketKey = JiraKey";
        Assert.Equal(
            storedDateMissing ? DBNull.Value : "2025-07-17T16:12:12.0000000-05:00",
            command.ExecuteScalar());
    }

    [Fact]
    public async Task EquivalentOffset_UpdatesCanonicalDateWithoutRewritingAcceptedRevisions()
    {
        using Fixture fixture = new();
        DateTimeOffset date = new(2025, 7, 17, 23, 12, 12, TimeSpan.FromHours(-5));
        SourceResult source = await fixture.CreateSourceRunAtAsync(date, "FHIR-801");
        string receipts = fixture.DumpTables("authoring_result_receipts");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.ObservedRevisions["FHIR-801"] = date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        fetcher.UpdatedAtByTicket["FHIR-801"] = date.ToUniversalTime();

        _ = await RefreshAsync(fixture, source, fetcher);

        Assert.Equal("2025-07-18T04:12:12.0000000+00:00",
            fixture.Scalar<string>("SELECT UpdatedAt FROM prepared_jira_hydration WHERE TicketKey = JiraKey"));
        Assert.Equal(receipts, fixture.DumpTables("authoring_result_receipts"));
        Assert.Equal(date.ToString("O", CultureInfo.InvariantCulture), source.ExpectedRevisions["FHIR-801"]);
    }

    [Fact]
    public async Task CrashAfterCommit_ReusesAllMetadataWithoutRefetch()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801", "FHIR-802");
        MetadataFetcher first = MetadataFetcher.For(source.ExpectedRevisions);
        first.ZulipResults["stream::topic"] = MetadataFetcher.ZulipResult("stream::topic", ZulipReferenceLookupOutcome.NotFound);
        PreparedTicketPublicationRefreshService interrupted = fixture.CreateRefreshService(first, new InterruptAfterCommitHook());
        PreparedTicketPublicationRefreshResult run = await interrupted.StartAsync(source.Run.Id);
        CountingGroupingDispatcher grouping = new(fixture.Database);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.CreatePostProcessor(interrupted, grouping).FinalizeRunAsync(run.Run.RunId));

        Assert.Equal(2, first.CallCount);
        Assert.Equal(2, first.ZulipCallCount);
        Assert.Equal(1, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));
        string metadata = fixture.ReadPublicationMetadataState();
        string protectedState = ReadProtectedState(fixture);
        string receipt = fixture.DumpTables("prepared_ticket_publication_refresh_receipts");
        MetadataFetcher restarted = MetadataFetcher.For(source.ExpectedRevisions);
        restarted.ThrowWhenCalled = true;
        AuthoringSnapshotDescriptor snapshot = Assert.IsType<AuthoringSnapshotDescriptor>(
            await fixture.CreatePostProcessor(fixture.CreateRefreshService(restarted), grouping).FinalizeRunAsync(run.Run.RunId));

        Assert.Equal(0, restarted.CallCount);
        Assert.Equal(0, restarted.ZulipCallCount);
        Assert.Equal(0, grouping.CallCount);
        Assert.Equal(metadata, fixture.ReadPublicationMetadataState());
        Assert.Equal(protectedState, ReadProtectedState(fixture));
        Assert.Equal(receipt, fixture.DumpTables("prepared_ticket_publication_refresh_receipts"));
        Assert.Equal(2, fixture.CountLive("authoring_run_attempts"));
        Assert.Equal(901, snapshot.PublicationProof!.SourceContentRevision);
        Assert.Equal(ZulipReferenceLookupOutcome.NotFound, ReadZulipReason(fixture).Metadata!.LatestOutcome);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
    }

    [Fact]
    public async Task LegacyReceipt_CannotSatisfyEnrichmentStage()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        StageContext stage = await CreateStageAsync(fixture, source, fetcher);
        string oldFingerprint = PreparedTicketPublicationContract.ComputePublicationRefreshInputFingerprint(
            source.Run.Id, stage.Input.Corpus);
        AuthoringRunStageRecord oldStage = await fixture.Store.EnsureRunStageAsync(
            stage.RunId, PreparerDatabase.PublicationMetadataStageName, string.Empty, oldFingerprint);
        AuthoringRunStageLease oldLease = Assert.IsType<AuthoringRunStageLease>(
            await fixture.Store.TryStartRunStageAsync(oldStage.Id));
        fixture.Execute(
            """
            INSERT INTO prepared_ticket_publication_refresh_receipts(
                RunId, StageId, InputFingerprint, CorpusFingerprint,
                SourceLastSuccessfulRefreshAt, SourceContentRevision, PublicDisplayNamePolicyVersion, AppliedAt)
            VALUES(@run, @stage, @input, @corpus, '2001-01-01T00:00:00+00:00', 77, @policy, '2001-01-01T00:00:00+00:00')
            """,
            ("@run", stage.RunId), ("@stage", oldStage.Id), ("@input", oldFingerprint),
            ("@corpus", stage.Input.CorpusFingerprint), ("@policy", PublicDisplayNamePolicy.CurrentVersion));
        await fixture.Store.CompleteRunStageAsync(oldStage.Id, oldLease.LeaseId);

        Assert.Null(await fixture.Database.GetCommittedPublicationEnrichmentReceiptAsync(
            stage.RunId, stage.Lease, stage.Fingerprint, stage.Input));
        PreparedTicketPublicationEnrichmentBatch batch = await fixture.CreateEnricher(fetcher).FetchAsync(stage.Input);
        PreparedTicketPublicationRefreshReceiptRecord receipt = await ApplyAsync(fixture, stage, batch);

        Assert.Equal(stage.Lease.StageId, receipt.StageId);
        Assert.Equal(stage.Fingerprint, receipt.InputFingerprint);
        Assert.NotEqual(oldFingerprint, receipt.InputFingerprint);
        Assert.Equal(901, receipt.SourceContentRevision);
        Assert.Equal(1, fetcher.CallCount);
        Assert.Equal(2, fetcher.ZulipCallCount);
        Assert.Equal(2, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));
    }

    [Fact]
    public async Task RestartBeforeStages_UsesPersistedRecipe()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801", "FHIR-802");
        MetadataFetcher initial = MetadataFetcher.For(source.ExpectedRevisions);
        PreparedTicketPublicationRefreshResult admitted = await fixture.CreateRefreshService(initial).StartAsync(source.Run.Id);
        AuthoringRunRecord persisted = (await fixture.Store.GetRunAsync(admitted.Run.RunId))!;
        Assert.NotNull(PreparerDatabase.ReadPublicationEnrichmentInput(persisted));
        Assert.Empty(await fixture.Store.GetRunStagesAsync(persisted.Id));
        Assert.Equal(0, initial.CallCount);
        Assert.Equal(0, initial.ZulipCallCount);
        MetadataFetcher restarted = MetadataFetcher.For(source.ExpectedRevisions);

        Assert.NotNull(await fixture.CreatePostProcessor(fixture.CreateRefreshService(restarted)).FinalizeRunAsync(persisted.Id));

        Assert.Equal(2, restarted.CallCount);
        Assert.Equal(2, restarted.ZulipCallCount);
        IReadOnlyList<AuthoringRunStageRecord> stages = await fixture.Store.GetRunStagesAsync(persisted.Id);
        Assert.Single(stages, stage => stage.StageName == PreparedTicketPublicationEnrichmentContract.StageName);
        Assert.DoesNotContain(stages, stage => stage.StageName == PreparerDatabase.PublicationMetadataStageName);
        Assert.Equal(persisted.RequestJson, (await fixture.Store.GetRunAsync(persisted.Id))!.RequestJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyNullRequest_RecoversJiraOnlyWithoutEnrichment(bool crashAfterCommit)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        PreparedTicketPublicationRefreshResult admitted = await fixture.CreateLegacyRefreshRunAsync(source.Run.Id);
        string zulip = fixture.DumpTables("prepared_zulip_hydration");
        string dates = ReadRows(fixture, "SELECT UpdatedAt FROM prepared_jira_hydration ORDER BY RowId");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.ThrowWhenZulipCalled = true;
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(
            fetcher, crashAfterCommit ? new InterruptAfterCommitHook() : null);
        if (crashAfterCommit)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.CreatePostProcessor(service).FinalizeRunAsync(admitted.Run.RunId));
            fetcher = MetadataFetcher.For(source.ExpectedRevisions);
            fetcher.ThrowWhenCalled = true;
            service = fixture.CreateRefreshService(fetcher);
        }

        Assert.NotNull(await fixture.CreatePostProcessor(service).FinalizeRunAsync(admitted.Run.RunId));

        Assert.Equal(crashAfterCommit ? 0 : 1, fetcher.CallCount);
        Assert.Equal(0, fetcher.ZulipCallCount);
        Assert.Equal(zulip, fixture.DumpTables("prepared_zulip_hydration"));
        Assert.Equal(dates, ReadRows(fixture, "SELECT UpdatedAt FROM prepared_jira_hydration ORDER BY RowId"));
        Assert.Null((await fixture.Store.GetRunAsync(admitted.Run.RunId))!.RequestJson);
        Assert.Single(await fixture.Store.GetRunStagesAsync(admitted.Run.RunId),
            stage => stage.StageName == PreparerDatabase.PublicationMetadataStageName);
    }

    [Fact]
    public async Task ProtectionDrift_PreventsSnapshotWithoutAuthoring()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801", "FHIR-802");
        string originalHash = await SnapshotHashAsync(fixture, source.Descriptor);
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(
            MetadataFetcher.For(source.ExpectedRevisions),
            new AfterCommitHook(() => fixture.Execute(
                "UPDATE prepared_jira_hydration SET DescriptionHtml = 'drift after commit' WHERE TicketKey = 'FHIR-801' AND JiraKey = TicketKey")));
        PreparedTicketPublicationRefreshResult admitted = await service.StartAsync(source.Run.Id);
        CountingGroupingDispatcher grouping = new(fixture.Database);

        Assert.Null(await fixture.CreatePostProcessor(service, grouping).FinalizeRunAsync(admitted.Run.RunId));

        await AssertTerminalRefusalAsync(fixture, admitted.Run.RunId);
        Assert.Equal(0, grouping.CallCount);
        Assert.Equal(2, fixture.CountLive("authoring_run_attempts"));
        Assert.Equal(1, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));
        Assert.DoesNotContain(await fixture.Store.GetSnapshotRecordsAsync(), record => record.RunId == admitted.Run.RunId);
        Assert.Equal(originalHash, await SnapshotHashAsync(fixture, source.Descriptor));
        Assert.Contains(PreparedTicketPublicationRefreshFailureCodes.FrozenProtectionDrift,
            (await fixture.Store.GetRunAsync(admitted.Run.RunId))!.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("running")]
    [InlineData("finalizing")]
    [InlineData("error")]
    public async Task ProtectionDriftBeforeStages_IsTerminalAndReleasesFenceWithoutFetching(string status)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.ThrowWhenCalled = true;
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(fetcher);
        PreparedTicketPublicationRefreshResult admitted = await service.StartAsync(source.Run.Id);
        if (status == AuthoringStatusValues.Runs.Finalizing)
        {
            await fixture.Store.MarkRunFinalizingAsync(admitted.Run.RunId);
        }
        else if (status == AuthoringStatusValues.Runs.Error)
        {
            await fixture.Store.MarkRunErrorAsync(admitted.Run.RunId, "Interrupted before registration");
        }
        fixture.Execute("UPDATE prepared_jira_hydration SET DescriptionHtml = 'pre-stage drift' WHERE JiraKey = TicketKey");
        string before = fixture.ReadPublicationMetadataState();

        Assert.Null(await fixture.CreatePostProcessor(service).FinalizeRunAsync(admitted.Run.RunId));

        await AssertTerminalRefusalAsync(fixture, admitted.Run.RunId);
        Assert.Equal(before, fixture.ReadPublicationMetadataState());
        Assert.Equal(0, fetcher.CallCount);
        Assert.Equal(0, fetcher.ZulipCallCount);
        Assert.Empty(await fixture.Store.GetRunStagesAsync(admitted.Run.RunId));
        Assert.Equal(0, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));
        Assert.Equal(1, fixture.CountLive("authoring_run_attempts"));
        Assert.All(await fixture.Store.GetRunItemsAsync(source.Run.Id), item => Assert.Equal("complete", item.Status));
    }

    [Theory]
    [InlineData("empty", "invalid-publication-recipe")]
    [InlineData("json-null", "invalid-publication-recipe")]
    [InlineData("malformed", "invalid-publication-recipe")]
    [InlineData("outer-version", "unsupported-publication-recipe")]
    [InlineData("recipe-name", "unsupported-publication-recipe")]
    [InlineData("recipe-version", "unsupported-publication-recipe")]
    [InlineData("inner-name", "unsupported-publication-recipe")]
    [InlineData("inner-version", "unsupported-publication-recipe")]
    [InlineData("fingerprint", "invalid-publication-recipe")]
    [InlineData("source-run", "invalid-publication-recipe")]
    public async Task MalformedOrUnknownRecipe_RefusesBeforeStageRegistration(string change, string expectedCode)
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.ThrowWhenCalled = true;
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(fetcher);
        PreparedTicketPublicationRefreshResult admitted = await service.StartAsync(source.Run.Id);
        AuthoringRunRecord run = (await fixture.Store.GetRunAsync(admitted.Run.RunId))!;
        JsonNode envelope = JsonNode.Parse(run.RequestJson!)!;
        JsonNode input = JsonNode.Parse(envelope["recipeInputJson"]!.GetValue<string>())!;
        switch (change)
        {
            case "outer-version": envelope["contractVersion"] = 99; break;
            case "recipe-name": envelope["recipeName"] = "different-recipe"; break;
            case "recipe-version": envelope["recipeVersion"] = 99; break;
            case "inner-name": input["recipeName"] = "different-recipe"; break;
            case "inner-version": input["recipeVersion"] = 99; break;
            case "fingerprint": input["protectedContentFingerprint"] = new string('0', 64); break;
            case "source-run": input["source"]!["runId"] = "different-source"; break;
        }
        envelope["recipeInputJson"] = input.ToJsonString();
        string request = change switch
        {
            "empty" => string.Empty,
            "json-null" => "null",
            "malformed" => "{broken",
            _ => envelope.ToJsonString(),
        };
        fixture.Execute("UPDATE authoring_runs SET RequestJson = @request WHERE Id = @run",
            ("@request", request), ("@run", run.Id));
        string before = fixture.ReadPublicationMetadataState();

        Assert.Null(await fixture.CreatePostProcessor(service).FinalizeRunAsync(run.Id));

        await AssertTerminalRefusalAsync(fixture, run.Id);
        Assert.Contains(expectedCode, (await fixture.Store.GetRunAsync(run.Id))!.Error, StringComparison.Ordinal);
        Assert.Equal(before, fixture.ReadPublicationMetadataState());
        Assert.Empty(await fixture.Store.GetRunStagesAsync(run.Id));
        Assert.Equal(0, fetcher.CallCount);
        Assert.Equal(0, fetcher.ZulipCallCount);
        Assert.Equal(0, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));
        Assert.DoesNotContain(await fixture.Store.GetSnapshotRecordsAsync(), record => record.RunId == run.Id);
    }

    [Fact]
    public async Task Apply_RejectsStaleLeaseEvenWhenAnotherLeaseCommittedTheReceipt()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        StageContext stage = await CreateStageAsync(fixture, source, fetcher);
        PreparedTicketPublicationEnrichmentBatch batch = await fixture.CreateEnricher(fetcher).FetchAsync(stage.Input);
        AuthoringRunStageLease replacement = Assert.IsType<AuthoringRunStageLease>(
            await fixture.Store.TryStartRunStageAsync(
                stage.Lease.StageId, TimeSpan.FromTicks(1), now: DateTimeOffset.UtcNow.AddMinutes(1)));
        string before = fixture.ReadPublicationMetadataState();
        AuthoringConflictException stale = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => ApplyAsync(fixture, stage, batch));
        Assert.Equal(AuthoringConflictCode.StageLeaseLost, stale.Code);
        Assert.Equal(before, fixture.ReadPublicationMetadataState());

        _ = await ApplyAsync(fixture, stage with { Lease = replacement }, batch);
        string committed = fixture.ReadPublicationMetadataState();
        stale = await Assert.ThrowsAsync<AuthoringConflictException>(() => ApplyAsync(fixture, stage, batch));

        Assert.Equal(AuthoringConflictCode.StageLeaseLost, stale.Code);
        Assert.Equal(committed, fixture.ReadPublicationMetadataState());
        Assert.Equal(1, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));
    }

    [Theory]
    [InlineData("stage-name")]
    [InlineData("stage-id")]
    [InlineData("fingerprint")]
    [InlineData("fence")]
    public async Task Apply_RejectsWrongStageCoordinatesOrMissingFence(string change)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        StageContext stage = await CreateStageAsync(fixture, source, fetcher);
        PreparedTicketPublicationEnrichmentBatch batch = await fixture.CreateEnricher(fetcher).FetchAsync(stage.Input);
        switch (change)
        {
            case "stage-name":
                fixture.Execute("UPDATE authoring_run_stages SET StageName = @name WHERE Id = @stage",
                    ("@name", PreparerDatabase.PublicationMetadataStageName), ("@stage", stage.Lease.StageId));
                break;
            case "stage-id":
                stage = stage with { Lease = stage.Lease with { StageId = "different-stage" } };
                break;
            case "fingerprint":
                fixture.Execute("UPDATE authoring_run_stages SET InputFingerprint = 'different-input' WHERE Id = @stage",
                    ("@stage", stage.Lease.StageId));
                break;
            case "fence":
                fixture.Execute("DELETE FROM authoring_mutation_fences WHERE RunId = @run", ("@run", stage.RunId));
                break;
        }
        string before = fixture.ReadPublicationMetadataState();

        AuthoringConflictException error = await Assert.ThrowsAsync<AuthoringConflictException>(
            () => ApplyAsync(fixture, stage, batch));

        Assert.Equal(AuthoringConflictCode.StageLeaseLost, error.Code);
        Assert.Equal(before, fixture.ReadPublicationMetadataState());
        Assert.Equal(0, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));
    }

    [Theory]
    [InlineData("recipe")]
    [InlineData("maintenance-item")]
    [InlineData("protected-context")]
    public async Task Apply_RechecksStoredRecipeItemsAndProtectionAfterFetching(string change)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        StageContext stage = await CreateStageAsync(fixture, source, fetcher);
        fetcher.OnJiraFetch = _ =>
        {
            switch (change)
            {
                case "recipe":
                    fixture.Execute("UPDATE authoring_runs SET RequestJson = '{}' WHERE Id = @run", ("@run", stage.RunId));
                    break;
                case "maintenance-item":
                    fixture.Execute("UPDATE authoring_run_items SET ExpectedSourceRevision = 'changed' WHERE RunId = @run",
                        ("@run", stage.RunId));
                    break;
                case "protected-context":
                    fixture.Execute("UPDATE prepared_jira_hydration SET DescriptionHtml = 'changed during fetch' WHERE JiraKey = TicketKey");
                    break;
            }
        };
        PreparedTicketPublicationEnrichmentBatch batch = await fixture.CreateEnricher(fetcher).FetchAsync(stage.Input);
        string before = fixture.ReadPublicationMetadataState();
        string protectedBefore = ReadProtectedState(fixture);

        if (change == "maintenance-item")
        {
            AuthoringConflictException error = await Assert.ThrowsAsync<AuthoringConflictException>(
                () => ApplyAsync(fixture, stage, batch));
            Assert.Equal(AuthoringConflictCode.StageFingerprintMismatch, error.Code);
        }
        else
        {
            PreparedTicketPublicationProtectionException error = await Assert.ThrowsAsync<PreparedTicketPublicationProtectionException>(
                () => ApplyAsync(fixture, stage, batch));
            Assert.Equal(change == "recipe"
                ? PreparedTicketPublicationRefreshFailureCodes.InvalidRecipe
                : PreparedTicketPublicationRefreshFailureCodes.FrozenProtectionDrift, error.FailureCode);
        }
        Assert.Equal(before, fixture.ReadPublicationMetadataState());
        Assert.Equal(protectedBefore, ReadProtectedState(fixture));
        Assert.Equal(0, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));
    }

    [Fact]
    public async Task UnsupportedOutputSchema_RefusesBeforeStagesAndReleasesFence()
    {
        using Fixture fixture = new();
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        fetcher.ThrowWhenCalled = true;
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(fetcher);
        PreparedTicketPublicationRefreshResult admitted = await service.StartAsync(source.Run.Id);

        Assert.Null(await fixture.CreatePostProcessor(service, snapshotSchemaVersion: PreparedTicketSnapshotSchemaV1.Version)
            .FinalizeRunAsync(admitted.Run.RunId));

        await AssertTerminalRefusalAsync(fixture, admitted.Run.RunId);
        Assert.Contains(PreparedTicketPublicationRefreshFailureCodes.UnsupportedSnapshotSchema,
            (await fixture.Store.GetRunAsync(admitted.Run.RunId))!.Error, StringComparison.Ordinal);
        Assert.Empty(await fixture.Store.GetRunStagesAsync(admitted.Run.RunId));
        Assert.Equal(0, fetcher.CallCount);
        Assert.Equal(0, fetcher.ZulipCallCount);
    }

    [Theory]
    [InlineData("prose")]
    [InlineData("reference")]
    [InlineData("parent-ignore")]
    [InlineData("requester-ignore")]
    [InlineData("zulip-ignore")]
    [InlineData("provenance-ignore")]
    [InlineData("receipt-abort")]
    public async Task Apply_RollsBackEntireBatchOnProtectedDriftOrWriteFailure(string failure)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801", "FHIR-802");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        StageContext stage = await CreateStageAsync(fixture, source, fetcher);
        PreparedTicketPublicationEnrichmentBatch batch = await fixture.CreateEnricher(fetcher).FetchAsync(stage.Input);
        string trigger = failure switch
        {
            "prose" => """
                CREATE TRIGGER enrichment_test_failure AFTER UPDATE OF Reporter ON prepared_ticket_hydration
                WHEN NEW.TicketKey = 'FHIR-802'
                BEGIN UPDATE prepared_tickets SET ProposalA = 'changed' WHERE Key = NEW.TicketKey; END
                """,
            "reference" => """
                CREATE TRIGGER enrichment_test_failure AFTER UPDATE OF Reporter ON prepared_ticket_hydration
                WHEN NEW.TicketKey = 'FHIR-802'
                BEGIN UPDATE prepared_ticket_related_zulip SET Justification = 'changed' WHERE TicketKey = NEW.TicketKey; END
                """,
            "parent-ignore" => """
                CREATE TRIGGER enrichment_test_failure BEFORE UPDATE OF Reporter ON prepared_ticket_hydration
                WHEN NEW.TicketKey = 'FHIR-802' BEGIN SELECT RAISE(IGNORE); END
                """,
            "requester-ignore" => """
                CREATE TRIGGER enrichment_test_failure BEFORE INSERT ON prepared_ticket_in_person_requesters
                WHEN NEW.TicketKey = 'FHIR-802' BEGIN SELECT RAISE(IGNORE); END
                """,
            "zulip-ignore" => """
                CREATE TRIGGER enrichment_test_failure BEFORE INSERT ON prepared_zulip_hydration
                WHEN NEW.TicketKey = 'FHIR-802' BEGIN SELECT RAISE(IGNORE); END
                """,
            "provenance-ignore" => """
                CREATE TRIGGER enrichment_test_failure BEFORE INSERT ON authoring_run_input_provenance
                BEGIN SELECT RAISE(IGNORE); END
                """,
            "receipt-abort" => """
                CREATE TRIGGER enrichment_test_failure BEFORE INSERT ON prepared_ticket_publication_refresh_receipts
                BEGIN SELECT RAISE(ABORT, 'simulated receipt failure'); END
                """,
            _ => throw new InvalidOperationException("Unknown test failure."),
        };
        fixture.Execute(trigger);
        string before = fixture.ReadPublicationMetadataState();
        string protectedBefore = ReadProtectedState(fixture);

        Exception error = Assert.IsAssignableFrom<Exception>(
            await Record.ExceptionAsync(() => ApplyAsync(fixture, stage, batch)));

        Assert.True(error is PreparedTicketPublicationProtectionException or SqliteException or InvalidOperationException);
        Assert.Equal(before, fixture.ReadPublicationMetadataState());
        Assert.Equal(protectedBefore, ReadProtectedState(fixture));
        Assert.Equal(0, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));
        Assert.Equal(2, fixture.CountLive("authoring_run_attempts"));
    }

    [Theory]
    [InlineData(PublicationMetadataFetchFailureReason.SourceUnavailable)]
    [InlineData(PublicationMetadataFetchFailureReason.UnstableSource)]
    [InlineData(PublicationMetadataFetchFailureReason.PeoplePolicyNotCurrent)]
    public async Task SourceMetadataFailure_RemainsRecoverableWithoutWeakeningChecks(PublicationMetadataFetchFailureReason failure)
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        MetadataFetcher unavailable = MetadataFetcher.For(source.ExpectedRevisions);
        unavailable.TransformMetadata = result => result with
        {
            Failure = new(failure, "Explicit source diagnostic"),
        };
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(unavailable);
        PreparedTicketPublicationRefreshResult admitted = await service.StartAsync(source.Run.Id);
        string before = fixture.ReadPublicationMetadataState();

        await Assert.ThrowsAsync<PreparedTicketPublicationRefreshStageException>(
            () => fixture.CreatePostProcessor(service).FinalizeRunAsync(admitted.Run.RunId));

        Assert.Equal(before, fixture.ReadPublicationMetadataState());
        Assert.Equal(AuthoringStatusValues.Runs.Error, (await fixture.Store.GetRunAsync(admitted.Run.RunId))!.Status);
        Assert.Equal(admitted.Run.RunId, (await fixture.Store.GetFencedRunAsync("jira-fhir"))!.Id);
        Assert.Equal(0, fixture.CountLive("prepared_ticket_publication_refresh_receipts"));
        MetadataFetcher recovered = MetadataFetcher.For(source.ExpectedRevisions);
        Assert.NotNull(await fixture.CreatePostProcessor(fixture.CreateRefreshService(recovered)).FinalizeRunAsync(admitted.Run.RunId));
        Assert.Equal(1, recovered.CallCount);
        Assert.Equal(2, recovered.ZulipCallCount);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
    }

    [Fact]
    public async Task InterruptedPromotion_ReusesSnapshotAndNeverRefetchesCommittedSources()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801", "FHIR-802");
        MetadataFetcher fetcher = MetadataFetcher.For(source.ExpectedRevisions);
        AuthoringSnapshotDescriptor first = await RefreshAsync(fixture, source, fetcher);
        string firstHash = await SnapshotHashAsync(fixture, first);
        fixture.SimulateReadySnapshotBeforeRunCompletion(first.RunId);
        MetadataFetcher restarted = MetadataFetcher.For(source.ExpectedRevisions);
        restarted.ThrowWhenCalled = true;

        AuthoringSnapshotDescriptor recovered = Assert.IsType<AuthoringSnapshotDescriptor>(
            await fixture.CreatePostProcessor(fixture.CreateRefreshService(restarted)).FinalizeRunAsync(first.RunId));

        Assert.Equal(first with { TableCounts = recovered.TableCounts }, recovered);
        Assert.Equal(
            first.TableCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            recovered.TableCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        Assert.Equal(firstHash, await SnapshotHashAsync(fixture, recovered));
        Assert.Equal(0, restarted.CallCount);
        Assert.Equal(0, restarted.ZulipCallCount);
        Assert.Single(await fixture.Store.GetSnapshotRecordsAsync(), record => record.RunId == first.RunId);
    }

    [Fact]
    public async Task SnapshotRecovery_RejectsFrozenDriftBeforeReusingReadyOutput()
    {
        using Fixture fixture = new(richGraph: true);
        SourceResult source = await fixture.CreateSourceRunAsync("FHIR-801");
        AuthoringSnapshotDescriptor first = await RefreshAsync(fixture, source, MetadataFetcher.For(source.ExpectedRevisions));
        string firstHash = await SnapshotHashAsync(fixture, first);
        fixture.SimulateReadySnapshotBeforeRunCompletion(first.RunId);
        fixture.Execute("UPDATE prepared_jira_hydration SET Title = 'changed after promotion' WHERE JiraKey = TicketKey");
        MetadataFetcher restarted = MetadataFetcher.For(source.ExpectedRevisions);
        restarted.ThrowWhenCalled = true;

        Assert.Null(await fixture.CreatePostProcessor(fixture.CreateRefreshService(restarted)).FinalizeRunAsync(first.RunId));

        await AssertTerminalRefusalAsync(fixture, first.RunId);
        Assert.Equal(firstHash, await SnapshotHashAsync(fixture, first));
        Assert.Single(await fixture.Store.GetSnapshotRecordsAsync(), record => record.RunId == first.RunId);
        Assert.Equal(0, restarted.CallCount);
        Assert.Equal(0, restarted.ZulipCallCount);
    }

    private sealed record StageContext(
        string RunId,
        PreparedTicketPublicationEnrichmentInput Input,
        string Fingerprint,
        AuthoringRunStageLease Lease,
        PreparedTicketPublicationRefreshService Service);

    private static async Task<StageContext> CreateStageAsync(Fixture fixture, SourceResult source, MetadataFetcher fetcher)
    {
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(fetcher);
        PreparedTicketPublicationRefreshResult admitted = await service.StartAsync(source.Run.Id);
        AuthoringRunRecord run = (await fixture.Store.GetRunAsync(admitted.Run.RunId))!;
        PreparedTicketPublicationEnrichmentInput input = Assert.IsType<PreparedTicketPublicationEnrichmentInput>(
            PreparerDatabase.ReadPublicationEnrichmentInput(run));
        string fingerprint = PreparedTicketPublicationEnrichmentContract.ComputeInputFingerprint(input);
        await fixture.Store.MarkRunFinalizingAsync(run.Id);
        AuthoringRunStageRecord stage = await fixture.Store.EnsureRunStageAsync(
            run.Id, PreparedTicketPublicationEnrichmentContract.StageName, string.Empty, fingerprint);
        AuthoringRunStageLease lease = Assert.IsType<AuthoringRunStageLease>(await fixture.Store.TryStartRunStageAsync(stage.Id));
        return new(run.Id, input, fingerprint, lease, service);
    }

    private static Task<PreparedTicketPublicationRefreshReceiptRecord> ApplyAsync(
        Fixture fixture, StageContext stage, PreparedTicketPublicationEnrichmentBatch batch)
        => fixture.Database.ApplyPublicationEnrichmentAsync(
            stage.RunId, stage.Lease, stage.Fingerprint, stage.Input, batch);

    private static async Task<AuthoringSnapshotDescriptor> RefreshAsync(Fixture fixture, SourceResult source, MetadataFetcher fetcher)
    {
        PreparedTicketPublicationRefreshService service = fixture.CreateRefreshService(fetcher);
        PreparedTicketPublicationRefreshResult run = await service.StartAsync(source.Run.Id);
        return Assert.IsType<AuthoringSnapshotDescriptor>(
            await fixture.CreatePostProcessor(service).FinalizeRunAsync(run.Run.RunId));
    }

    private static async Task AssertTerminalRefusalAsync(Fixture fixture, string runId)
    {
        AuthoringRunRecord run = Assert.IsType<AuthoringRunRecord>(await fixture.Store.GetRunAsync(runId));
        Assert.Equal(AuthoringStatusValues.Runs.Superseded, run.Status);
        Assert.Contains("Retain the existing publication and inspect the conflict", run.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Ordinary re-authoring is required", run.Error, StringComparison.Ordinal);
        Assert.Null(await fixture.Store.GetFencedRunAsync("jira-fhir"));
        Assert.All(await fixture.Store.GetRunItemsAsync(runId), item => Assert.Equal(AuthoringStatusValues.Items.Complete, item.Status));
    }

    private static async Task<string> SnapshotHashAsync(Fixture fixture, AuthoringSnapshotDescriptor snapshot)
        => Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(fixture.SnapshotDirectory, snapshot.FileName)))).ToLowerInvariant();

    private static ZulipReferenceHydrationReasonReadResult ReadZulipReason(
        Fixture fixture, string ticket = "FHIR-801", string reference = "stream::topic")
        => ZulipReferenceHydrationReason.Read(fixture.Scalar<string>(
            $"SELECT HydrationReason FROM prepared_zulip_hydration WHERE TicketKey = '{ticket}' AND ZulipThreadId = '{reference}'"));

    private static string ReadZulipContext(Fixture fixture)
        => ReadRows(fixture,
            """
            SELECT RowId, Id, TicketKey, ZulipThreadId, StreamId, StreamName, Topic, MessageCount,
                   FirstMessageAt, LastMessageAt, FirstMessageExcerpt, Url, HydratedAt
            FROM prepared_zulip_hydration WHERE ZulipThreadId = 'stream::topic' ORDER BY RowId
            """);

    private static string ReadProtectedState(Fixture fixture)
        => fixture.DumpTables(
            "prepared_tickets", "prepared_ticket_repos", "prepared_ticket_related_jira",
            "prepared_ticket_related_zulip", "prepared_ticket_related_github", "prepared_github_hydration",
            "prepared_repo_hydration", "prepared_ticket_jira_xref", "prepared_ticket_jira_content",
            "prepared_ticket_artifacts", "prepared_ticket_pages", "prepared_ticket_topics",
            "prepared_ticket_topic_groups", "prepared_ticket_topic_members", "prepared_ticket_partition_receipts",
            "authoring_result_receipts", "prepared_ticket_authoring_state", "prepared_ticket_run_item_partitions",
            "jira_review_workgroups")
        + ReadRows(fixture,
            """
            SELECT RowId, Id, TicketKey, Priority, Resolution, ResolutionDescriptionPlain, Specification,
                   RaisedInVersion, SelectedBallot, ChangeCategory, Impact, Labels, CommentCount,
                   DescriptionPlain, DescriptionHtml, ResolutionDescriptionHtml, CreatedAt,
                   RelatedArtifactsRaw, RelatedPagesRaw, HydrationStatus, HydrationReason
            FROM prepared_ticket_hydration ORDER BY RowId
            """)
        + ReadRows(fixture,
            """
            SELECT RowId, Id, TicketKey, JiraKey, Title, Status, Type, Priority, Resolution,
                   ResolutionDescriptionPlain, WorkGroup, WorkGroupClean, Specification, Url,
                   DescriptionHtml, ResolutionDescriptionHtml, CreatedAt, RelatedArtifactsRaw,
                   RelatedPagesRaw, HydratedAt, HydrationStatus, HydrationReason
            FROM prepared_jira_hydration ORDER BY RowId
            """)
        + ReadRows(fixture, "SELECT * FROM prepared_jira_hydration WHERE TicketKey <> JiraKey ORDER BY RowId")
        + ReadRows(fixture, "SELECT * FROM prepared_zulip_hydration WHERE ZulipThreadId = 'unaccepted-reference' ORDER BY RowId");

    private static string ReadRows(Fixture fixture, string sql)
    {
        using SqliteConnection connection = fixture.Database.OpenConnection();
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

    private sealed class InterruptAfterCommitHook : IPreparedTicketPublicationRefreshInterruptionHook
    {
        public Task AfterMetadataCommitAsync(string runId, PreparedTicketPublicationRefreshReceiptRecord receipt, CancellationToken ct)
            => throw new InvalidOperationException("Simulated interruption after commit.");
    }

    private sealed class AfterCommitHook(Action action) : IPreparedTicketPublicationRefreshInterruptionHook
    {
        public Task AfterMetadataCommitAsync(string runId, PreparedTicketPublicationRefreshReceiptRecord receipt, CancellationToken ct)
        {
            action();
            return Task.CompletedTask;
        }
    }
}
