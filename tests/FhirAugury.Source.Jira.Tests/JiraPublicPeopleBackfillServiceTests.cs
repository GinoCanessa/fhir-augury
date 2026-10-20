using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Api;
using FhirAugury.Common.Caching;
using FhirAugury.Common.Indexing;
using FhirAugury.Source.Jira.Api;
using FhirAugury.Source.Jira.Configuration;
using FhirAugury.Source.Jira.Controllers;
using FhirAugury.Source.Jira.Database;
using FhirAugury.Source.Jira.Database.Records;
using FhirAugury.Source.Jira.Ingestion;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Source.Jira.Tests;

public class JiraPublicPeopleBackfillServiceTests
{
    [Fact]
    public async Task Preview_DoesNotWriteOrExposePersonalEvidence()
    {
        using JiraPeopleTestFixture fixture = new();
        JiraUserRecord user = fixture.AddUser("private-account", "migrated private value");
        fixture.AddIssue("FHIR-1", reporter: user.Id);
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("private-account", "Explicit Public Name"));
        string before = fixture.Snapshot();

        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();

        Assert.True(preview.CanApply);
        Assert.Equal(before, fixture.Snapshot());
        Assert.Empty(fixture.Cache.EnumerateKeys("jira"));
        Assert.Equal(1, preview.Changes.UsersUpdated);
        Assert.Equal(0, preview.Changes.IssueRowsUpdated);
        string json = JsonSerializer.Serialize(preview);
        foreach (string forbidden in new[] { "private-account", "migrated private value", "Explicit Public Name", fixture.Root, "Digest", "Username", "UserId" })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-account", string.Join("\n", fixture.Logs));
    }

    [Fact]
    public async Task Apply_DisplayNameCollisionNeverRebindsIdentity()
    {
        using JiraPeopleTestFixture fixture = new();
        JiraUserRecord bound = fixture.AddUser("bound-private-account", "Same Public Name");
        JiraUserRecord other = fixture.AddUser("other-private-account", "Same Public Name", explicitName: true);
        fixture.AddIssue("FHIR-1", reporter: bound.Id);
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person(other.Username, other.DisplayName));
        string before = fixture.Snapshot();

        JiraPublicPeoplePreviewResponse refused = await fixture.Preview();
        Assert.Equal(JiraPublicPeopleCodes.IdentityConflict, refused.Code);
        Assert.Null(refused.PreviewToken);
        Assert.Equal(before, fixture.Snapshot());

        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person(bound.Username, bound.DisplayName));
        JiraPublicPeopleApplyResponse applied = await fixture.Apply(await fixture.Preview());
        Assert.Equal(JiraPublicPeopleCodes.Applied, applied.Code);
        using SqliteConnection connection = fixture.Db.OpenConnection();
        Assert.Equal(bound.Id, JiraIssueRecord.SelectSingle(connection, Key: "FHIR-1")!.ReporterUserId);
        Assert.True(JiraUserRecord.SelectSingle(connection, Id: bound.Id)!.HasExplicitDisplayName);
        Assert.Equal(other, JiraUserRecord.SelectSingle(connection, Id: other.Id));
    }

    [Fact]
    public async Task Apply_SharedUserImpactMustMatchPreview()
    {
        using JiraPeopleTestFixture fixture = new();
        JiraUserRecord user = fixture.AddUser("shared-private-account", "Old Name");
        fixture.AddIssue("FHIR-1", reporter: user.Id);
        fixture.AddIssue("PSS-2", "jira_pss", assignee: user.Id);
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person(user.Username, "New Name"));
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        Assert.True(preview.RequiresSharedUserImpactAcknowledgement);
        Assert.Equal(["FHIR-1", "PSS-2"], preview.AffectedTickets.Select(ticket => ticket.Key).Order().ToArray());

        JiraPublicPeopleApplyResponse unacknowledged = await fixture.Apply(preview, acknowledge: false);
        Assert.Equal(JiraPublicPeopleCodes.SharedImpactAcknowledgementRequired, unacknowledged.Code);
        // An unfenced change to reference impact must still be detected inside
        // the writer, even though its author did not advance ContentRevision.
        fixture.AddIssue("BALDEF-3", "jira_baldef", reporter: user.Id);
        string before = fixture.Snapshot();
        JiraPublicPeopleApplyResponse changed = await fixture.Apply(preview);
        Assert.Equal(JiraPublicPeopleCodes.SharedImpactChanged, changed.Code);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task Apply_LocalProcessingCommitInvalidatesPreview()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"));
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        Assert.IsType<OkObjectResult>(fixture.LocalProcessing.SetProcessed(new() { Key = "FHIR-1", ProcessedLocally = true }));
        string before = fixture.Snapshot();

        JiraPublicPeopleApplyResponse response = await fixture.Apply(preview);

        Assert.Equal(JiraPublicPeopleCodes.StalePreview, response.Code);
        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal(1, fixture.Revision);
    }

    [Fact]
    public async Task Apply_ExistingMutationIsRejectedWithoutClearingMarker()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"));
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        JiraSourceStateRecord marker = fixture.Db.BeginContentMutation();
        string before = fixture.Snapshot();

        Assert.Equal(JiraPublicPeopleCodes.SourceBusy, (await fixture.Apply(preview)).Code);
        Assert.Equal(before, fixture.Snapshot());
        using SqliteConnection connection = fixture.Db.OpenConnection();
        Assert.Equal(marker, JiraDatabase.ReadSourceState(connection));
        Assert.True(marker.MutationInProgress);
        Assert.Equal(JiraPublicPeopleCodes.SourceBusy, (await fixture.Preview()).Code);
    }

    [Fact]
    public async Task Apply_ConcurrentIngestionIsExcluded()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"));
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        using ManualResetEventSlim insideWriter = new();
        using ManualResetEventSlim releaseWriter = new();
        fixture.Builder.AfterRebuild = _ =>
        {
            insideWriter.Set();
            Assert.True(releaseWriter.Wait(TimeSpan.FromSeconds(20)));
        };
        Task<JiraPublicPeopleApplyResponse> apply = Task.Run(() => fixture.Apply(preview));
        try
        {
            Assert.True(insideWriter.Wait(TimeSpan.FromSeconds(20)));
            Assert.True(fixture.Pipeline.IsRunning);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Pipeline.RunFullIngestionAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Pipeline.RunIncrementalIngestionAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Pipeline.RebuildFromCacheAsync());
        }
        finally
        {
            releaseWriter.Set();
        }
        Assert.Equal(JiraPublicPeopleCodes.Applied, (await apply).Code);
        Assert.False(fixture.Pipeline.IsRunning);
        Assert.Equal(1, fixture.Source.ClearCount);
        Assert.True(fixture.Source.ClearedWhileGateHeld);

        preview = await fixture.Preview();
        using IDisposable? lease = await fixture.Pipeline.TryAcquirePublicPeopleMaintenanceAsync(default);
        Assert.NotNull(lease);
        Assert.Equal(JiraPublicPeopleCodes.SourceBusy, (await fixture.Apply(preview)).Code);
        Assert.Equal(1, fixture.Revision);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("lookup")]
    [InlineData("row-count")]
    [InlineData("generation")]
    public async Task Apply_CancellationOrLookupFailureRollsBackDataAndRevision(string failure)
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"),
            requesters: """[{"name":"requester-account","displayName":"Requester Name"}]""");
        fixture.RebuildLookups();
        using CancellationTokenSource cancellation = new();
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        if (failure == "row-count")
            fixture.Execute("CREATE TRIGGER reject_people BEFORE UPDATE ON jira_issues BEGIN SELECT RAISE(IGNORE); END;");
        if (failure == "generation")
            fixture.Execute("CREATE TRIGGER reject_generation BEFORE UPDATE ON jira_source_state BEGIN SELECT RAISE(IGNORE); END;");
        if (failure is "cancel" or "lookup")
        {
            fixture.Builder.AfterRebuild = connection =>
            {
                if (failure == "cancel") cancellation.Cancel();
                else JiraPeopleTestFixture.Execute(connection, "SELECT * FROM missing_people_lookup;");
            };
        }
        string before = fixture.Snapshot();

        JiraPublicPeopleApplyResponse response = await fixture.Apply(preview, ct: cancellation.Token);

        Assert.Equal(failure switch
        {
            "cancel" => JiraPublicPeopleCodes.Cancelled,
            "lookup" => JiraPublicPeopleCodes.InternalError,
            _ => JiraPublicPeopleCodes.WriteFailed,
        }, response.Code);
        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal(0, fixture.Revision);
        Assert.Equal(0, fixture.Source.ClearCount);
        Assert.False(fixture.Pipeline.IsRunning);
        Assert.DoesNotContain("private-account", string.Join("\n", fixture.Logs));
        Assert.DoesNotContain("upstream body", JsonSerializer.Serialize(response));
    }

    [Fact]
    public async Task Apply_ProgrammingFailureRollsBackAndPropagates()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"));
        fixture.RebuildLookups();
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        InvalidOperationException failure = new(JiraPeopleTestFixture.FailureDetails);
        fixture.Builder.AfterRebuild = _ => throw failure;
        string before = fixture.Snapshot();

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Apply(preview)));

        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal(0, fixture.Revision);
        Assert.Equal(0, fixture.Source.ClearCount);
        Assert.False(fixture.Pipeline.IsRunning);
        Assert.DoesNotContain(JiraPublicPeopleCodes.WriteFailed, string.Join("\n", fixture.Logs));
        fixture.AssertSafeDiagnostics();
        fixture.Builder.AfterRebuild = null;
        Assert.Equal(JiraPublicPeopleCodes.Applied, (await fixture.Apply(await fixture.Preview())).Code);
    }

    [Fact]
    public async Task Apply_CommitPublishesPeopleAndLookupsTogether()
    {
        using JiraPeopleTestFixture fixture = new();
        JiraUserRecord user = fixture.AddUser("exact-account", "Old Name");
        fixture.AddIssue("FHIR-1", reporter: user.Id, legacyReporter: user.Username);
        fixture.AddRequester("FHIR-1", user.Id);
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person(user.Username, "New Public Name"),
            requesters: $$"""[{{JiraPeopleTestFixture.Person(user.Username, "New Public Name")}}]""");
        fixture.RebuildLookups();
        using SqliteConnection oldReader = fixture.Db.OpenConnection();
        JiraPeopleTestFixture.Execute(oldReader, "BEGIN DEFERRED;");
        Assert.Equal("Old Name", fixture.Scalar(oldReader, "SELECT Name FROM jira_index_inpersons"));
        fixture.Builder.AfterRebuild = writer =>
        {
            ItemResponse old = fixture.ReadItem("FHIR-1");
            Assert.Null(old.People!.Reporter);
            Assert.Equal(0, old.Provenance!.ContentRevision);
            Assert.Equal("Old Name", fixture.Scalar(oldReader, "SELECT Name FROM jira_index_users"));
            Assert.Equal("New Public Name", fixture.Scalar(writer, "SELECT Name FROM jira_index_users"));
        };

        JiraPublicPeopleApplyResponse response = await fixture.Apply(await fixture.Preview());

        Assert.Equal(JiraPublicPeopleCodes.Applied, response.Code);
        Assert.Equal("Old Name", fixture.Scalar(oldReader, "SELECT Name FROM jira_index_inpersons"));
        Assert.Equal("0", fixture.Scalar(oldReader, "SELECT ContentRevision FROM jira_source_state"));
        JiraPeopleTestFixture.Execute(oldReader, "COMMIT;");
        ItemResponse item = fixture.ReadItem("FHIR-1");
        Assert.Equal("New Public Name", item.People!.Reporter);
        Assert.Equal(["New Public Name"], item.People.InPersonRequesters);
        Assert.Equal(1, item.Provenance!.ContentRevision);
        Assert.True(item.Provenance.IsStable);
        using SqliteConnection fresh = fixture.Db.OpenConnection();
        Assert.Equal("New Public Name", fixture.Scalar(fresh, "SELECT Name FROM jira_index_users"));
        Assert.Equal("New Public Name", fixture.Scalar(fresh, "SELECT Name FROM jira_index_inpersons"));
    }

    [Fact]
    public async Task Apply_PreservesNonPeopleValuesAndExistingBindings()
    {
        using JiraPeopleTestFixture fixture = new();
        JiraUserRecord reporter = fixture.AddUser("reporter-account", "Migrated Reporter");
        JiraUserRecord assignee = fixture.AddUser("assignee-account", "Existing Assignee", explicitName: true);
        JiraIssueRecord issue = (JiraIssueRecord)fixture.AddIssue("FHIR-1", reporter: reporter.Id, assignee: assignee.Id);
        fixture.AddRequester(issue.Key, reporter.Id);
        fixture.AddIssue("PSS-2", "jira_pss", reporter: reporter.Id, assignee: assignee.Id);
        fixture.AddIssue("BALDEF-3", "jira_baldef", reporter: reporter.Id, assignee: assignee.Id);
        fixture.AddIssue("BALLOT-4", "jira_ballot", reporter: reporter.Id, assignee: assignee.Id);
        fixture.Execute("""
            INSERT INTO jira_comments (Id, IssueKey, Author, Body, BodyPlain, CreatedAt) VALUES (9001, 'FHIR-1', 'Original author', 'Expensive comment', 'Expensive comment', '2024-01-01T00:00:00Z');
            INSERT INTO jira_issue_links (Id, SourceKey, TargetKey, LinkType) VALUES (9001, 'FHIR-1', 'PSS-2', 'relates to');
            INSERT INTO jira_issue_related (Id, IssueKey, RelatedIssueKey) VALUES (9001, 'FHIR-1', 'FHIR-9');
            """);
        using (SqliteConnection connection = fixture.Db.OpenConnection())
        {
            JiraProjectRecord.Insert(connection, new JiraProjectRecord
            {
                Id = JiraProjectRecord.GetIndex(), Key = "FHIR", Enabled = true, BaselineValue = 7, IssueCount = 4,
                LastSyncAt = DateTimeOffset.Parse("2026-09-14T00:00:00Z"),
            });
            JiraSyncStateRecord.Insert(connection, new JiraSyncStateRecord
            {
                Id = JiraSyncStateRecord.GetIndex(), SourceName = "jira", SubSource = "FHIR:full",
                LastSyncAt = DateTimeOffset.Parse("2026-09-14T00:00:00Z"),
                LastSuccessfulSyncAt = DateTimeOffset.Parse("2026-09-14T00:00:00Z"),
                LastCursor = "unchanged", ItemsIngested = 4, SyncSchedule = "original", NextScheduledAt = null,
                Status = "success", LastError = null,
            });
            JiraKeywordRecord.Insert(connection, new JiraKeywordRecord
            {
                Id = JiraKeywordRecord.GetIndex(), ContentType = "issue", SourceId = issue.Key,
                Keyword = "expensive", Count = 2, KeywordType = "word", Bm25Score = 2.5,
            });
        }
        fixture.RebuildLookups();
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person(reporter.Username, "Public Reporter"),
            assignee: JiraPeopleTestFixture.Person(assignee.Username, assignee.DisplayName),
            requesters: $$"""[{{JiraPeopleTestFixture.Person(reporter.Username, "Public Reporter")}}]""");
        string before = fixture.Snapshot(protectedOnly: true);
        string bindingsBefore = fixture.BindingSnapshot();
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        Assert.Equal(4, preview.AffectedTickets.Count);
        Assert.Equal(0, preview.Changes.IssueRowsUpdated);

        Assert.Equal(JiraPublicPeopleCodes.Applied, (await fixture.Apply(preview)).Code);

        Assert.Equal(before, fixture.Snapshot(protectedOnly: true));
        Assert.Equal(bindingsBefore, fixture.BindingSnapshot());
        using SqliteConnection after = fixture.Db.OpenConnection();
        Assert.Equal(issue, JiraIssueRecord.SelectSingle(after, Key: issue.Key));
        Assert.Equal(assignee, JiraUserRecord.SelectSingle(after, Id: assignee.Id));
        Assert.Single(JiraIssueInPersonRecord.SelectList(after));
        Assert.Equal(reporter with { DisplayName = "Public Reporter", HasExplicitDisplayName = true },
            JiraUserRecord.SelectSingle(after, Id: reporter.Id));
        Assert.Equal("1", fixture.Scalar(after, "SELECT COUNT(*) FROM jira_issues_fts WHERE jira_issues_fts MATCH 'expensive'"));
        Assert.Equal("1", fixture.Scalar(after, "SELECT COUNT(*) FROM jira_comments_fts WHERE jira_comments_fts MATCH 'expensive'"));
        Assert.False(JiraDatabase.ReadSourceState(after).MutationInProgress);
    }

    [Fact]
    public async Task Apply_LostResponseRequiresFreshPreview()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"));
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        _ = await fixture.Apply(preview); // Simulate losing the committed response.
        long revision = fixture.Revision;
        int reads = fixture.Http.RequestCount;

        Assert.Equal(JiraPublicPeopleCodes.PreviewExpired, (await fixture.Apply(preview)).Code);
        Assert.Equal(revision, fixture.Revision);
        Assert.Equal(reads, fixture.Http.RequestCount);
        Assert.Equal(JiraPublicPeopleCodes.PreviewExpired,
            (await fixture.NewService().ApplyAsync(new() { PreviewToken = preview.PreviewToken! })).Code);
        JiraPublicPeoplePreviewResponse inspected = await fixture.Preview();
        Assert.Equal(JiraPublicPeopleCodes.NoChange, (await fixture.Apply(inspected)).Code);
        Assert.Equal(revision, fixture.Revision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_CapturedEvidenceIsNotRefetchedAndLateCancellationCannotUndoCommit(bool throwAfterClear)
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Captured Name"));
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("different-account", "Later Name"));
        using CancellationTokenSource cancellation = new();
        fixture.Source.AfterClear = () =>
        {
            cancellation.Cancel();
            if (throwAfterClear) cancellation.Token.ThrowIfCancellationRequested();
        };
        int reads = fixture.Http.RequestCount;

        JiraPublicPeopleApplyResponse response = await fixture.Apply(preview, ct: cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(JiraPublicPeopleCodes.Applied, response.Code);
        Assert.Equal(1, response.ContentRevision);
        Assert.Equal(reads, fixture.Http.RequestCount);
        Assert.Equal("Captured Name", fixture.ReadItem("FHIR-1").People!.Reporter);
        Assert.True(fixture.Source.ClearedWhileGateHeld);
    }

    [Fact]
    public async Task Apply_PostCommitProgrammingFailureIsNotReportedAsCancellationOrRollback()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"));
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        InvalidOperationException failure = new(JiraPeopleTestFixture.FailureDetails);
        using CancellationTokenSource cancellation = new();
        fixture.Source.AfterClear = () =>
        {
            cancellation.Cancel();
            throw failure;
        };

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Apply(preview, ct: cancellation.Token)));

        Assert.Equal(1, fixture.Revision);
        Assert.Equal("Public Name", fixture.ReadItem("FHIR-1").People!.Reporter);
        Assert.True(fixture.Source.ClearedWhileGateHeld);
        Assert.False(fixture.Pipeline.IsRunning);
        Assert.DoesNotContain(JiraPublicPeopleCodes.Cancelled, string.Join("\n", fixture.Logs));
        Assert.DoesNotContain(JiraPublicPeopleCodes.WriteFailed, string.Join("\n", fixture.Logs));
        fixture.AssertSafeDiagnostics();
        Assert.Equal(JiraPublicPeopleCodes.PreviewExpired, (await fixture.Apply(preview)).Code);
    }

    [Fact]
    public async Task Apply_MapperStateIsInvalidatedBeforeThePipelineGateIsReleased()
    {
        using JiraPeopleTestFixture fixture = new();
        JiraUserRecord user = fixture.AddUser("exact-account", "Old Name");
        fixture.AddIssue("FHIR-1", reporter: user.Id);
        using (SqliteConnection connection = fixture.Db.OpenConnection())
            Assert.Equal(user.Id, fixture.Mapper.ResolveUser(connection, user.Username, user.DisplayName));
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person(user.Username, "New Name"));
        Assert.Equal(JiraPublicPeopleCodes.Applied, (await fixture.Apply(await fixture.Preview())).Code);
        using (SqliteConnection connection = fixture.Db.OpenConnection())
        {
            int? oldDisplayIdentity = fixture.Mapper.ResolveByDisplayName(connection, "Old Name");
            Assert.NotEqual(user.Id, oldDisplayIdentity);
            Assert.Equal("New Name", JiraUserRecord.SelectSingle(connection, Id: user.Id)!.DisplayName);
        }
        Assert.True(fixture.Source.ClearedWhileGateHeld);
    }

    [Fact]
    public async Task Apply_LaterLocalProcessingCommitGetsALaterGeneration()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"));
        using ManualResetEventSlim writerReady = new();
        using ManualResetEventSlim releaseWriter = new();
        using ManualResetEventSlim localStarted = new();
        fixture.Builder.AfterRebuild = _ =>
        {
            writerReady.Set();
            Assert.True(releaseWriter.Wait(TimeSpan.FromSeconds(20)));
        };
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        Task<JiraPublicPeopleApplyResponse> apply = Task.Run(() => fixture.Apply(preview));
        Task<IActionResult>? local = null;
        try
        {
            Assert.True(writerReady.Wait(TimeSpan.FromSeconds(20)));
            local = Task.Run(() =>
            {
                localStarted.Set();
                return fixture.LocalProcessing.SetProcessed(new() { Key = "FHIR-1", ProcessedLocally = true });
            });
            Assert.True(localStarted.Wait(TimeSpan.FromSeconds(20)));
            Assert.False(local.IsCompleted);
        }
        finally
        {
            releaseWriter.Set();
        }
        Assert.Equal(1, (await apply).ContentRevision);
        Assert.IsType<OkObjectResult>(await local!);
        Assert.Equal(2, fixture.Revision);
        Assert.NotNull(fixture.ReadIssue("FHIR-1").ProcessedLocallyAt);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("binding")]
    [InlineData("user")]
    [InlineData("requester")]
    [InlineData("source-marker")]
    [InlineData("new-identity")]
    public async Task Apply_RechecksEveryRelevantBeforeImageWithoutRelyingOnlyOnGeneration(string changed)
    {
        using JiraPeopleTestFixture fixture = new();
        JiraUserRecord user = fixture.AddUser("exact-account", "Old Name");
        fixture.AddIssue("FHIR-1", reporter: changed == "new-identity" ? null : user.Id);
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person(
            changed == "new-identity" ? "new-account" : user.Username, "Public Name"));
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        switch (changed)
        {
            case "revision": fixture.Execute("UPDATE jira_issues SET UpdatedAt = '2026-09-16T17:00:00Z'"); break;
            case "binding": fixture.Execute("UPDATE jira_issues SET ReporterUserId = NULL"); break;
            case "user": fixture.Execute("UPDATE jira_users SET HasExplicitDisplayName = 1"); break;
            case "requester": fixture.AddRequester("FHIR-1", user.Id); break;
            case "source-marker": fixture.Execute("UPDATE jira_source_state SET UpdatedAt = '2020-01-01T00:00:00Z'"); break;
            case "new-identity": fixture.AddUser("new-account", "Public Name"); break;
        }
        string before = fixture.Snapshot();
        JiraPublicPeopleApplyResponse result = await fixture.Apply(preview);
        Assert.Contains(result.Code, new[] { JiraPublicPeopleCodes.StalePreview, JiraPublicPeopleCodes.SharedImpactChanged });
        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal(0, fixture.Revision);
    }

    [Fact]
    public async Task Apply_AddsOnlyProvenMissingBindingsAcrossAllShapesAndRequesters()
    {
        using JiraPeopleTestFixture fixture = new();
        string[] keys = ["FHIR-1", "PSS-2", "BALDEF-3", "BALLOT-4"];
        string[] tables = ["jira_issues", "jira_pss", "jira_baldef", "jira_ballot"];
        for (int index = 0; index < keys.Length; index++)
        {
            fixture.AddIssue(keys[index], tables[index]);
            fixture.SetEvidence(keys[index], reporter: JiraPeopleTestFixture.Person("reporter-account", "Public Reporter"),
                assignee: "null", requesters: """[{"key":"requester-account","displayName":"Public Requester"}]""");
        }
        string before = fixture.Snapshot(protectedOnly: true);
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview(keys);
        Assert.Equal(2, preview.Changes.UsersCreated);
        Assert.Equal(4, preview.Changes.IssueRowsUpdated);
        Assert.Equal(4, preview.Changes.RequesterAssociationsAdded);

        Assert.Equal(JiraPublicPeopleCodes.Applied, (await fixture.Apply(preview)).Code);

        Assert.Equal(before, fixture.Snapshot(protectedOnly: true));
        using SqliteConnection connection = fixture.Db.OpenConnection();
        int reporter = JiraUserRecord.SelectSingle(connection, Username: "reporter-account")!.Id;
        foreach (string table in tables)
        {
            Assert.Equal(reporter.ToString(), fixture.Scalar(connection, $"SELECT ReporterUserId FROM {table}"));
            Assert.Null(fixture.Scalar(connection, $"SELECT AssigneeUserId FROM {table}"));
        }
        Assert.Equal(4, JiraIssueInPersonRecord.SelectCount(connection));
        Assert.Equal(1, fixture.Revision);
    }

    [Fact]
    public async Task Preview_IncludesLegacyLookupCollisionsVoteRolesAndNonFhirRequesters()
    {
        using JiraPeopleTestFixture fixture = new();
        JiraUserRecord changed = fixture.AddUser("account-one", "Old Name");
        JiraUserRecord peer = fixture.AddUser("peer-account", "New Name", explicitName: true);
        fixture.AddIssue("FHIR-1", reporter: changed.Id);
        JiraIssueRecord vote = (JiraIssueRecord)fixture.AddIssue("FHIR-2");
        using (SqliteConnection connection = fixture.Db.OpenConnection())
        {
            vote.VoteMover = "Old Name";
            vote.VoteSeconder = "New Name";
            JiraIssueRecord.Update(connection, vote);
        }
        fixture.AddIssue("PSS-3", "jira_pss", legacyReporter: peer.Username);
        fixture.AddIssue("BALDEF-4", "jira_baldef", assignee: changed.Id);
        fixture.AddIssue("BALLOT-5", "jira_ballot");
        fixture.AddRequester("BALLOT-5", peer.Id);
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person(changed.Username, "New Name"));

        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();

        Assert.Equal(5, preview.AffectedTickets.Count);
        Assert.Equal(1, preview.AffectedTickets.Single(ticket => ticket.Key == "FHIR-2").VoteMoverRoles);
        Assert.Equal(1, preview.AffectedTickets.Single(ticket => ticket.Key == "FHIR-2").VoteSeconderRoles);
        Assert.Equal(1, preview.AffectedTickets.Single(ticket => ticket.Key == "BALLOT-5").InPersonRequesterRoles);
        Assert.Equal(1, preview.AffectedTickets.Single(ticket => ticket.Key == "PSS-3").ReporterRoles);
        Assert.Equal(0, preview.Changes.IssueRowsUpdated);
        Assert.True(preview.RequiresSharedUserImpactAcknowledgement);
    }

    [Theory]
    [InlineData("same-user", "SAME-USER", JiraPublicPeopleCodes.PolicyRejected)]
    [InlineData("real-account", "Name <private@example.test>", JiraPublicPeopleCodes.PolicyRejected)]
    [InlineData("real-account", null, JiraPublicPeopleCodes.MissingExplicitNameEvidence)]
    public async Task Apply_InsufficientOrRejectedNamesAreNoOps(string identity, string? name, string reason)
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person(identity, name));
        string before = fixture.Snapshot();
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        Assert.Contains(reason, preview.Tickets[0].Roles[0].Reasons);
        Assert.Equal(JiraPublicPeopleCodes.NoChange, (await fixture.Apply(preview)).Code);
        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal(0, fixture.Builder.PeopleRebuilds);
    }

    [Fact]
    public async Task Preview_SyntheticOrCaseDifferentIdentityIsNotAnExactAccountBinding()
    {
        using JiraPeopleTestFixture fixture = new();
        JiraUserRecord synthetic = fixture.AddUser("Exact Name", "Exact Name", account: false);
        fixture.AddIssue("FHIR-1", reporter: synthetic.Id);
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person(synthetic.Username, "Other Public Name"));
        Assert.Equal(JiraPublicPeopleCodes.IdentityConflict, (await fixture.Preview()).Code);

        using (SqliteConnection connection = fixture.Db.OpenConnection())
        {
            synthetic.HasAccountUsername = true;
            JiraUserRecord.Update(connection, synthetic);
        }
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact name", "Other Public Name"));
        Assert.Equal(JiraPublicPeopleCodes.IdentityConflict, (await fixture.Preview()).Code);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""[{"name":"different-account","displayName":"Different Person"}]""")]
    [InlineData("""[{"displayName":"Name Without Identity"}]""")]
    [InlineData("""[{"name":"bound-account","displayName":"First"},{"name":"bound-account","displayName":"Second"}]""")]
    public async Task Preview_RequesterEvidenceCannotDeleteReplaceOrGuessExistingAssociations(string requesters)
    {
        using JiraPeopleTestFixture fixture = new();
        JiraUserRecord user = fixture.AddUser("bound-account", "Old Name");
        fixture.AddIssue("FHIR-1");
        fixture.AddRequester("FHIR-1", user.Id);
        fixture.SetEvidence("FHIR-1", requesters: requesters);
        string before = fixture.Snapshot();
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        Assert.False(preview.CanApply);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task Preview_MissingRequesterFieldPreservesAssociationsButIsNotAbsent()
    {
        using JiraPeopleTestFixture fixture = new();
        JiraUserRecord user = fixture.AddUser("bound-account", "Migrated Name");
        fixture.AddIssue("FHIR-1", reporter: user.Id);
        fixture.AddRequester("FHIR-1", user.Id);
        fixture.SetEvidence("FHIR-1");
        string before = fixture.Snapshot();

        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();

        Assert.True(preview.CanApply);
        Assert.Contains(JiraPublicPeopleCodes.MissingField, preview.Tickets[0].Roles[0].Reasons);
        Assert.Contains(JiraPublicPeopleCodes.MissingField, preview.Tickets[0].Roles[2].Reasons);
        Assert.Equal(JiraPublicPeopleCodes.NoChange, (await fixture.Apply(preview)).Code);
        Assert.Equal(before, fixture.Snapshot());
    }

    [Fact]
    public async Task Preview_SharedIdentityWithConflictingNamesRefusesTheWholeBatch()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.AddIssue("FHIR-2");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("same-account", "First Name"));
        fixture.SetEvidence("FHIR-2", reporter: JiraPeopleTestFixture.Person("same-account", "Second Name"));
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview(["FHIR-1", "FHIR-2"]);
        Assert.Equal(JiraPublicPeopleCodes.ConflictingObservations, preview.Code);
        Assert.Null(preview.PreviewToken);
        Assert.Equal(0, fixture.Revision);
    }

    [Fact]
    public async Task Preview_ExplicitAbsenceCannotEraseAnExistingRoleBinding()
    {
        using JiraPeopleTestFixture fixture = new();
        JiraUserRecord user = fixture.AddUser("bound-account", "Old Name");
        fixture.AddIssue("FHIR-1", assignee: user.Id);
        fixture.SetEvidence("FHIR-1", assignee: "null");
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        Assert.Equal(JiraPublicPeopleCodes.IdentityConflict, preview.Code);
        Assert.Contains(JiraPublicPeopleCodes.IdentityConflict, preview.Tickets[0].Roles[1].Reasons);
    }

    [Fact]
    public async Task Preview_SharedImpactLimitRefusesRatherThanTruncatingApplyScope()
    {
        using JiraPeopleTestFixture fixture = new();
        JiraUserRecord user = fixture.AddUser("shared-account", "Old Name");
        fixture.AddIssue("FHIR-1", reporter: user.Id);
        using (SqliteConnection connection = fixture.Db.OpenConnection())
        {
            JiraPeopleTestFixture.Execute(connection, $"""
                WITH RECURSIVE numbers(n) AS (
                    SELECT 2 UNION ALL SELECT n+1 FROM numbers WHERE n <= {JiraPublicPeopleBackfillService.MaximumAffectedTickets}
                )
                INSERT INTO jira_issues (Id, Key, ProjectKey, Title, Type, Priority, Status, CreatedAt, UpdatedAt, CommentCount, ReporterUserId)
                SELECT n + 1000000, 'FHIR-' || n, 'FHIR', 'Fixture', 'Bug', 'Major', 'Open',
                    '{JiraPeopleTestFixture.RevisionText}', '{JiraPeopleTestFixture.RevisionText}', 0, {user.Id}
                FROM numbers;
                """);
        }
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person(user.Username, "Public Name"));
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview();
        Assert.Equal(JiraPublicPeopleCodes.SharedImpactTooLarge, preview.Code);
        Assert.Empty(preview.AffectedTickets);
        Assert.Null(preview.PreviewToken);
        Assert.Contains(JiraPublicPeopleCodes.SharedImpactTooLarge, preview.Tickets[0].Roles[0].Reasons);
        Assert.Equal(0, fixture.Revision);
    }

    [Fact]
    public async Task Preview_ReservationsAreBoundedAndLifetimeStartsAfterCompletion()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"));
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Http.BeforeResponse = async ct => await release.Task.WaitAsync(ct);
        Task<JiraPublicPeoplePreviewResponse>[] pending = Enumerable.Range(0, 8).Select(_ => fixture.Preview()).ToArray();
        Assert.Equal(8, fixture.Http.RequestCount);
        fixture.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(JiraPublicPeopleCodes.PreviewCapacity, (await fixture.Preview()).Code);
        release.SetResult(true);
        JiraPublicPeoplePreviewResponse[] previews = await Task.WhenAll(pending);
        Assert.All(previews, preview => Assert.True(preview.CanApply));
        Assert.All(previews, preview => Assert.Equal(fixture.Clock.GetUtcNow() + TimeSpan.FromMinutes(15), preview.ExpiresAt));
        Assert.Equal(JiraPublicPeopleCodes.PreviewCapacity, (await fixture.Preview()).Code);
        fixture.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(JiraPublicPeopleCodes.PreviewExpired, (await fixture.Apply(previews[0])).Code);
        Assert.True((await fixture.Preview()).CanApply);
    }

    [Fact]
    public async Task Preview_CancelledAcquisitionReleasesItsSlotAndNeverCreatesAJob()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.Http.BeforeResponse = ct => Task.Delay(Timeout.Infinite, ct);
        using CancellationTokenSource cancellation = new();
        Task<JiraPublicPeoplePreviewResponse> preview = fixture.Preview(ct: cancellation.Token);
        cancellation.Cancel();
        Assert.Equal(JiraPublicPeopleCodes.Cancelled, (await preview).Code);
        fixture.Http.BeforeResponse = null;
        fixture.SetEvidence("FHIR-1");
        for (int index = 0; index < 8; index++)
            Assert.True((await fixture.Preview()).CanApply);
    }

    [Theory]
    [InlineData("upstream")]
    [InlineData("cache-read")]
    [InlineData("cache-enumeration")]
    public async Task Preview_ProgrammingFailuresPropagateAndReleaseReservations(string failurePoint)
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        fixture.SetEvidence("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"));
        await fixture.SeedCache(fixture.Http.Issues["FHIR-1"], trusted: true);
        InvalidOperationException failure = new(JiraPeopleTestFixture.FailureDetails);
        switch (failurePoint)
        {
            case "upstream": fixture.Http.BeforeResponse = _ => throw failure; break;
            case "cache-read": fixture.SourceCache.BeforeRead = _ => throw failure; break;
            case "cache-enumeration": fixture.SourceCache.BeforeEnumerate = () => throw failure; break;
        }
        string mode = failurePoint == "upstream" ? JiraPublicPeopleEvidenceModes.Upstream : JiraPublicPeopleEvidenceModes.CacheOnly;
        string before = fixture.Snapshot();

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Preview(mode: mode)));

        Assert.Equal(before, fixture.Snapshot());
        Assert.DoesNotContain(JiraPublicPeopleCodes.UpstreamUnavailable, string.Join("\n", fixture.Logs));
        Assert.DoesNotContain(JiraPublicPeopleCodes.UnknownCacheOrigin, string.Join("\n", fixture.Logs));
        fixture.AssertSafeDiagnostics();
        fixture.Http.BeforeResponse = null;
        fixture.SourceCache.BeforeRead = null;
        fixture.SourceCache.BeforeEnumerate = null;
        for (int index = 0; index < JiraPublicPeopleBackfillService.MaximumOutstandingPreviews; index++)
            Assert.True((await fixture.Preview(mode: mode)).CanApply);
    }

    [Theory]
    [InlineData("enumeration", "io", "cache-io-failure")]
    [InlineData("enumeration", "access", "cache-access-denied")]
    [InlineData("receipt", "io", "cache-io-failure")]
    [InlineData("receipt", "access", "cache-access-denied")]
    [InlineData("receipt", "json", "malformed-cache-receipt")]
    [InlineData("raw", "io", "cache-io-failure")]
    [InlineData("raw", "access", "cache-access-denied")]
    public async Task Preview_CacheOperationalFailuresAreVisibleWithoutTrustingUnreadableEvidence(
        string failurePoint, string kind, string classification)
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        await fixture.SeedCache(JiraPeopleTestFixture.Json("FHIR-1",
            reporter: JiraPeopleTestFixture.Person("private-account", "Raw Name")), trusted: true);
        Exception failure = kind == "access"
            ? new UnauthorizedAccessException(JiraPeopleTestFixture.FailureDetails)
            : new IOException(JiraPeopleTestFixture.FailureDetails);
        if (kind == "json")
        {
            using MemoryStream stream = new(Encoding.UTF8.GetBytes(JiraPeopleTestFixture.FailureDetails));
            await fixture.Cache.PutAsync("jira",
                $"_support/public-people-origins/_meta_{JiraPublicPeopleObservationReader.Digest("FHIR/json/observation.json")}.json",
                stream, default);
        }
        else if (failurePoint == "enumeration")
        {
            fixture.SourceCache.BeforeEnumerate = () => throw failure;
        }
        else
        {
            fixture.SourceCache.BeforeRead = key =>
            {
                if (failurePoint == "receipt" ? key.StartsWith("_support/", StringComparison.Ordinal) : key == "FHIR/json/observation.json")
                    throw failure;
            };
        }
        string before = fixture.Snapshot();

        JiraPublicPeoplePreviewResponse preview = await fixture.Preview(mode: JiraPublicPeopleEvidenceModes.CacheOnly);

        Assert.Equal(JiraPublicPeopleCodes.UnknownCacheOrigin, preview.Code);
        Assert.False(preview.CanApply);
        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal(0, fixture.Http.RequestCount);
        Assert.Contains(fixture.Logs, line => line.Contains($"failure: {classification}", StringComparison.Ordinal));
        fixture.AssertSafeDiagnostics(preview);
    }

    [Fact]
    public async Task Preview_UsableCacheEvidenceDoesNotSilenceOtherCacheReadFailures()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        await fixture.SeedCache(JiraPeopleTestFixture.Json("FHIR-1",
            reporter: JiraPeopleTestFixture.Person("private-account", "Raw Name")), trusted: true);
        await fixture.SeedCache(JiraPeopleTestFixture.Json("FHIR-2"), trusted: true, file: "unreadable.json");
        fixture.SourceCache.BeforeRead = key =>
        {
            if (key == "FHIR/json/unreadable.json")
                throw new IOException(JiraPeopleTestFixture.FailureDetails);
        };

        JiraPublicPeoplePreviewResponse preview = await fixture.Preview(mode: JiraPublicPeopleEvidenceModes.CacheOnly);

        Assert.True(preview.CanApply);
        Assert.Contains(fixture.Logs, line => line.Contains("failure: cache-io-failure", StringComparison.Ordinal));
        fixture.AssertSafeDiagnostics(preview);
    }

    [Fact]
    public async Task Preview_KeyBoundsModesAndUntrustedOriginsAreExplicitRefusals()
    {
        using JiraPeopleTestFixture fixture = new();
        foreach (string[] keys in new string[][] { [], ["FHIR-1", "FHIR-1"], ["../private-path"], ["fhir-1"], ["FHIR-1\n"],
            Enumerable.Range(1, 2001).Select(index => $"FHIR-{index}").ToArray() })
        {
            Assert.Equal(JiraPublicPeopleCodes.InvalidRequest, (await fixture.Service.PreviewAsync(new() { Keys = keys })).Code);
        }
        Assert.Equal(JiraPublicPeopleCodes.InvalidRequest,
            (await fixture.Service.PreviewAsync(new() { Keys = ["FHIR-1"], EvidenceMode = "automatic" })).Code);
        fixture.AddIssue("FHIR-1");
        await fixture.SeedCache(JiraPeopleTestFixture.Json("FHIR-1", reporter: JiraPeopleTestFixture.Person("private-id", "Raw Name")), trusted: false);
        JiraPublicPeoplePreviewResponse unknown = await fixture.Preview(mode: JiraPublicPeopleEvidenceModes.CacheOnly);
        Assert.Equal(JiraPublicPeopleCodes.UnknownCacheOrigin, unknown.Code);
        Assert.Equal(0, fixture.Http.RequestCount);
        Assert.Null(unknown.PreviewToken);
    }

    [Fact]
    public async Task Preview_TrustedCacheRequiresMatchingSourceKeyAndContentDigests()
    {
        using JiraPeopleTestFixture fixture = new();
        fixture.AddIssue("FHIR-1");
        string body = JiraPeopleTestFixture.Json("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"));
        await fixture.SeedCache(body, trusted: true);
        Assert.True((await fixture.Preview(mode: JiraPublicPeopleEvidenceModes.CacheOnly)).CanApply);
        await fixture.SeedCache(body.Replace("Public Name", "Modified Name"), trusted: false);
        Assert.Equal(JiraPublicPeopleCodes.UnknownCacheOrigin, (await fixture.Preview(mode: JiraPublicPeopleEvidenceModes.CacheOnly)).Code);
        await fixture.SeedCache(body, trusted: true);
        fixture.Options.Value.BaseUrl = "https://different.example.test";
        Assert.Equal(JiraPublicPeopleCodes.UnknownCacheOrigin, (await fixture.Preview(mode: JiraPublicPeopleEvidenceModes.CacheOnly)).Code);
        Assert.Equal(0, fixture.Http.RequestCount);
    }

    [Fact]
    public async Task Preview_CacheReadsAreReadOnlyAndNormalAuthenticatedDownloadsRecordOrigin()
    {
        using JiraPeopleTestFixture fixture = new();
        string body = JiraPeopleTestFixture.Json("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"));
        fixture.Http.SearchBody = $$"""{"total":1,"issues":[{{body}}]}""";
        IngestionResult ingestion = await fixture.Source.DownloadAllAsync("FHIR", null, default);
        Assert.Equal(1, ingestion.ItemsNew);
        Assert.Equal(0, ingestion.ItemsFailed);
        int calls = fixture.Http.RequestCount;
        string before = fixture.Snapshot();
        string[] cacheKeys = fixture.Cache.EnumerateKeys("jira").ToArray();
        JiraPublicPeoplePreviewResponse preview = await fixture.Preview(mode: JiraPublicPeopleEvidenceModes.CacheOnly);
        Assert.True(preview.CanApply);
        Assert.Equal(before, fixture.Snapshot());
        Assert.Equal(cacheKeys, fixture.Cache.EnumerateKeys("jira").ToArray());
        Assert.Equal(calls, fixture.Http.RequestCount);
        Assert.Equal(JiraPublicPeopleCodes.NoChange, (await fixture.Apply(preview)).Code);
    }

    [Theory]
    [InlineData("io", "cache-io-failure")]
    [InlineData("access", "cache-access-denied")]
    [InlineData("cancel", "cache-write-cancelled")]
    public async Task CacheOrigin_OperationalReceiptFailuresPreserveNormalIngestionOutcomes(string failure, string classification)
    {
        using JiraPeopleTestFixture fixture = new();
        string body = JiraPeopleTestFixture.Json("FHIR-1", reporter: JiraPeopleTestFixture.Person("exact-account", "Public Name"));
        fixture.Http.SearchBody = $$"""{"total":1,"issues":[{{body}}]}""";
        using CancellationTokenSource cancellation = new();
        fixture.SourceCache.BeforeWrite = key =>
        {
            if (!key.StartsWith("_support/", StringComparison.Ordinal)) return;
            if (failure == "cancel")
            {
                cancellation.Cancel();
                throw new OperationCanceledException(JiraPeopleTestFixture.FailureDetails, cancellation.Token);
            }
            if (failure == "access")
                throw new UnauthorizedAccessException(JiraPeopleTestFixture.FailureDetails);
            throw new IOException(JiraPeopleTestFixture.FailureDetails);
        };

        IngestionResult result = await fixture.Source.DownloadAllAsync("FHIR", null, cancellation.Token);

        Assert.Equal(1, result.ItemsProcessed);
        Assert.Equal(1, result.ItemsNew);
        Assert.Equal(0, result.ItemsFailed);
        Assert.Empty(result.Errors);
        Assert.Single(fixture.Cache.EnumerateKeys("jira"));
        Assert.Equal("Public Name", fixture.ReadItem("FHIR-1").People!.Reporter);
        Assert.Contains(fixture.Logs, line => line.Contains($"failure: {classification}", StringComparison.Ordinal));
        fixture.AssertSafeDiagnostics(result);
        Assert.Equal(JiraPublicPeopleCodes.UnknownCacheOrigin,
            (await fixture.Preview(mode: JiraPublicPeopleEvidenceModes.CacheOnly)).Code);
    }

    [Fact]
    public async Task CacheOrigin_ProgrammingFailureIsNotReportedAsUnknownOrigin()
    {
        using JiraPeopleTestFixture fixture = new();
        string body = JiraPeopleTestFixture.Json("FHIR-1");
        fixture.Http.SearchBody = $$"""{"total":1,"issues":[{{body}}]}""";
        InvalidOperationException failure = new(JiraPeopleTestFixture.FailureDetails);
        fixture.SourceCache.BeforeWrite = key =>
        {
            if (key.StartsWith("_support/", StringComparison.Ordinal)) throw failure;
        };
        string before = fixture.Snapshot();

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Source.DownloadAllAsync("FHIR", null, default)));

        Assert.Equal(before, fixture.Snapshot());
        Assert.DoesNotContain(JiraPublicPeopleCodes.UnknownCacheOrigin, string.Join("\n", fixture.Logs));
        fixture.AssertSafeDiagnostics();
    }
}

/// <summary>All files, SQLite connections and HTTP responses belong to this test fixture.</summary>
internal sealed class JiraPeopleTestFixture : IDisposable
{
    internal const string RevisionText = "2026-09-15T17:00:00+00:00";
    internal const string FailureDetails = @"private-account Raw Name C:\private\cache upstream-body";
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"jira_people_{Guid.NewGuid():N}");
    internal JiraDatabase Db { get; }
    internal IOptions<JiraServiceOptions> Options { get; }
    internal FileSystemResponseCache Cache { get; }
    internal PeopleTestCache SourceCache { get; }
    internal JiraUserMapper Mapper { get; } = new();
    internal PeopleTestSource Source { get; }
    internal PeopleTestIndexBuilder Builder { get; } = new();
    internal JiraIngestionPipeline Pipeline { get; }
    internal JiraPublicPeopleBackfillService Service { get; }
    internal PeopleTestHttpFactory Http { get; }
    internal PeopleTestClock Clock { get; } = new();
    internal List<string> Logs { get; } = [];
    internal LocalProcessingController LocalProcessing => new(Db, Options);

    internal JiraPeopleTestFixture()
    {
        Directory.CreateDirectory(Root);
        Options = Microsoft.Extensions.Options.Options.Create(new JiraServiceOptions
        {
            DatabasePath = Path.Combine(Root, "jira.db"), CachePath = Path.Combine(Root, "cache"),
            BaseUrl = "https://jira.example.test", AuthMode = "apitoken", Email = "fixture@example.test", ApiToken = "fixture-token",
        });
        Options.Value.RateLimiting.MaxRetries = 0;
        Db = new(Options.Value.DatabasePath, NullLogger<JiraDatabase>.Instance);
        Db.Initialize();
        Cache = new(Options.Value.CachePath);
        SourceCache = new(Cache);
        Http = new(Options);
        Source = new(Options, Http, Db, SourceCache, Mapper, new PeopleTestLogger<JiraSource>(Logs));
        Pipeline = new(Source, Db, null!, Builder, null!, Http, Options, new IndexTracker(), Cache,
            null!, null!, NullLogger<JiraIngestionPipeline>.Instance);
        Source.GateHeld = () => Pipeline.IsRunning;
        Service = NewService();
    }

    internal JiraPublicPeopleBackfillService NewService() =>
        new(Source, Db, Pipeline, Builder, new PeopleTestLogger<JiraPublicPeopleBackfillService>(Logs), Clock);

    internal Task<JiraPublicPeoplePreviewResponse> Preview(
        string[]? keys = null, string mode = JiraPublicPeopleEvidenceModes.Upstream, CancellationToken ct = default) =>
        Service.PreviewAsync(new() { Keys = keys ?? ["FHIR-1"], EvidenceMode = mode }, ct);

    internal Task<JiraPublicPeopleApplyResponse> Apply(
        JiraPublicPeoplePreviewResponse preview, bool acknowledge = true, CancellationToken ct = default)
    {
        Assert.NotNull(preview.PreviewToken);
        return Service.ApplyAsync(new() { PreviewToken = preview.PreviewToken, AcknowledgeSharedUserImpact = acknowledge }, ct);
    }

    internal JiraUserRecord AddUser(string identity, string name, bool explicitName = false, bool account = true)
    {
        using SqliteConnection connection = Db.OpenConnection();
        JiraUserRecord user = new()
        {
            Id = JiraUserRecord.GetIndex(), Username = identity, DisplayName = name,
            HasAccountUsername = account, HasExplicitDisplayName = explicitName,
        };
        JiraUserRecord.Insert(connection, user);
        return user;
    }

    internal JiraIssueBaseRecord AddIssue(
        string key, string table = "jira_issues", int? reporter = null, int? assignee = null, string? legacyReporter = "Legacy reporter")
    {
        using JsonDocument document = JsonDocument.Parse(Json(key));
        JiraIssueBaseRecord issue = table switch
        {
            "jira_pss" => JiraFieldMapper.MapProjectScopeStatement(document.RootElement),
            "jira_baldef" => JiraFieldMapper.MapBaldef(document.RootElement),
            "jira_ballot" => JiraFieldMapper.MapBallot(document.RootElement),
            _ => JiraFieldMapper.MapIssue(document.RootElement),
        };
        issue.ReporterUserId = reporter;
        issue.AssigneeUserId = assignee;
        issue.Reporter = legacyReporter;
        issue.Assignee = "Legacy assignee";
        issue.Description = "Expensive <b>description</b>";
        issue.DescriptionPlain = "Expensive description";
        issue.Summary = "Expensive summary";
        issue.Labels = "preserved-label";
        issue.ProcessedLocallyAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        using SqliteConnection connection = Db.OpenConnection();
        switch (issue)
        {
            case JiraIssueRecord fhir: JiraIssueRecord.Insert(connection, fhir); break;
            case JiraProjectScopeStatementRecord pss: JiraProjectScopeStatementRecord.Insert(connection, pss); break;
            case JiraBaldefRecord baldef: JiraBaldefRecord.Insert(connection, baldef); break;
            case JiraBallotRecord ballot: JiraBallotRecord.Insert(connection, ballot); break;
        }
        return issue;
    }

    internal void AddRequester(string key, int userId)
    {
        using SqliteConnection connection = Db.OpenConnection();
        JiraIssueInPersonRecord.Insert(connection, new JiraIssueInPersonRecord { Id = JiraIssueInPersonRecord.GetIndex(), IssueKey = key, UserId = userId });
    }

    internal void SetEvidence(string key, string? reporter = null, string? assignee = null, string? requesters = null, string revision = RevisionText) =>
        Http.Issues[key] = Json(key, reporter, assignee, requesters, revision);

    internal static string Person(string identity, string? name) =>
        JsonSerializer.Serialize(new { name = identity, displayName = name });

    internal static string Json(string key, string? reporter = null, string? assignee = null, string? requesters = null, string revision = RevisionText)
    {
        string additional = (reporter is null ? "" : $",\"reporter\":{reporter}")
            + (assignee is null ? "" : $",\"assignee\":{assignee}")
            + (requesters is null ? "" : $",\"customfield_11000\":{requesters}");
        return "{\"key\":" + JsonSerializer.Serialize(key)
            + ",\"fields\":{\"summary\":\"Fixture\",\"created\":\"2020-01-01T00:00:00Z\",\"updated\":"
            + JsonSerializer.Serialize(revision) + additional + "}}";
    }

    internal async Task SeedCache(string body, bool trusted, string file = "observation.json", JiraPeopleFormat format = JiraPeopleFormat.Json)
    {
        string key = $"FHIR/{(format == JiraPeopleFormat.Json ? "json" : "xml")}/{file}";
        using (MemoryStream stream = new(Encoding.UTF8.GetBytes(body)))
            await Cache.PutAsync("jira", key, stream, default);
        if (!trusted) return;
        // Test-created analogue of a source-written authenticated acquisition
        // receipt. No caller can supply one through the maintenance API.
        byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = JiraPublicPeopleObservationReader.CurrentVersion,
            SourceFingerprint = Source.PublicPeopleSourceFingerprint,
            KeyDigest = JiraPublicPeopleObservationReader.Digest(key),
            BodyDigest = JiraPublicPeopleObservationReader.Digest(body),
            Format = (int)format,
        });
        using MemoryStream receiptStream = new(receipt);
        await Cache.PutAsync("jira",
            $"_support/public-people-origins/_meta_{JiraPublicPeopleObservationReader.Digest(key)}.json", receiptStream, default);
    }

    internal JiraIssueRecord ReadIssue(string key)
    {
        using SqliteConnection connection = Db.OpenConnection();
        return JiraIssueRecord.SelectSingle(connection, Key: key)!;
    }

    internal ItemResponse ReadItem(string key) =>
        Assert.IsType<ItemResponse>(Assert.IsType<OkObjectResult>(
            new ItemsController(Db, Options).GetItem(key, true, true)).Value);

    internal long Revision
    {
        get { using SqliteConnection connection = Db.OpenConnection(); return JiraDatabase.ReadSourceState(connection).ContentRevision; }
    }

    internal void RebuildLookups()
    {
        using SqliteConnection connection = Db.OpenConnection();
        Builder.RebuildIndexTables(connection);
        Builder.PeopleRebuilds = 0;
    }

    internal void Execute(string sql)
    {
        using SqliteConnection connection = Db.OpenConnection();
        Execute(connection, sql);
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = new(sql, connection);
        command.ExecuteNonQuery();
    }

    internal string? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = new(sql, connection);
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : value.ToString();
    }

    internal string Snapshot(bool protectedOnly = false)
    {
        using SqliteConnection connection = Db.OpenConnection();
        List<string> tables = [];
        using (SqliteCommand command = new("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name", connection))
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string table = reader.GetString(0);
                // FTS shadow storage can change layout without changing content.
                // Ordinary source rows and every unrelated lookup/BM25 table are pinned.
                if (table.Contains("_fts", StringComparison.Ordinal) || table.StartsWith("sqlite_", StringComparison.Ordinal)) continue;
                if (protectedOnly && table is "jira_users" or "jira_issue_inpersons" or "jira_index_users" or "jira_index_inpersons" or "jira_source_state") continue;
                tables.Add(table);
            }
        }
        Dictionary<string, List<Dictionary<string, string?>>> data = new(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            using SqliteCommand command = new($"SELECT * FROM \"{table}\" ORDER BY rowid", connection);
            using SqliteDataReader reader = command.ExecuteReader();
            List<Dictionary<string, string?>> rows = [];
            while (reader.Read())
            {
                Dictionary<string, string?> values = new(StringComparer.Ordinal);
                for (int index = 0; index < reader.FieldCount; index++)
                {
                    string name = reader.GetName(index);
                    if (protectedOnly && name is "ReporterUserId" or "AssigneeUserId") continue;
                    values[name] = reader.IsDBNull(index) ? null : reader.GetValue(index).ToString();
                }
                rows.Add(values);
            }
            data[table] = rows;
        }
        return JsonSerializer.Serialize(data);
    }

    internal string BindingSnapshot()
    {
        using SqliteConnection connection = Db.OpenConnection();
        JiraPeopleState state = JiraDatabase.ReadPublicPeopleState(connection, default);
        return JsonSerializer.Serialize(new
        {
            Bindings = state.Issues.Select(issue => new
            {
                issue.Table, issue.Id, issue.Key, issue.ReporterUserId, issue.AssigneeUserId,
            }).ToArray(),
            state.Requesters,
        });
    }

    internal void AssertSafeDiagnostics(object? response = null)
    {
        string diagnostics = JsonSerializer.Serialize(response) + string.Join("\n", Logs);
        foreach (string forbidden in new[] { "private-account", "Raw Name", @"C:\private\cache", "upstream-body", Root })
            Assert.DoesNotContain(forbidden, diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Http.Dispose();
        Db.Dispose();
        TestFileCleanup.SafeDeleteDirectory(Root);
    }
}

internal sealed class PeopleTestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-09-15T20:00:00Z");
    public override DateTimeOffset GetUtcNow() => _now;
    internal void Advance(TimeSpan span) => _now += span;
}

internal sealed class PeopleTestLogger<T>(List<string> entries) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (entries) entries.Add(formatter(state, exception) + (exception is null ? "" : $"\n{exception}"));
    }
}

internal sealed class PeopleTestCache(IResponseCache inner) : IResponseCache
{
    internal Action? BeforeEnumerate { get; set; }
    internal Action<string>? BeforeRead { get; set; }
    internal Action<string>? BeforeWrite { get; set; }
    public string RootPath => inner.RootPath;

    public bool TryGet(string source, string key, [NotNullWhen(true)] out Stream? content)
    {
        BeforeRead?.Invoke(key);
        return inner.TryGet(source, key, out content);
    }

    public Task PutAsync(string source, string key, Stream content, CancellationToken ct)
    {
        BeforeWrite?.Invoke(key);
        return inner.PutAsync(source, key, content, ct);
    }

    public IEnumerable<string> EnumerateKeys(string source)
    {
        BeforeEnumerate?.Invoke();
        return inner.EnumerateKeys(source);
    }

    public IEnumerable<string> EnumerateKeys(string source, string subPath)
    {
        BeforeEnumerate?.Invoke();
        return inner.EnumerateKeys(source, subPath);
    }

    public void Remove(string source, string key) => inner.Remove(source, key);
    public void Clear(string source) => inner.Clear(source);
    public void ClearAll() => inner.ClearAll();
    public CacheStats GetStats(string source, bool forceRefresh = false) => inner.GetStats(source, forceRefresh);
    public Task<Stream?> TryGetAsync(string source, string key, CancellationToken ct = default) => inner.TryGetAsync(source, key, ct);
    public Task RemoveAsync(string source, string key, CancellationToken ct = default) => inner.RemoveAsync(source, key, ct);
    public Task ClearAsync(string source, CancellationToken ct = default) => inner.ClearAsync(source, ct);
    public Task ClearAllAsync(CancellationToken ct = default) => inner.ClearAllAsync(ct);
}

internal sealed class PeopleTestIndexBuilder() : JiraIndexBuilder(NullLogger<JiraIndexBuilder>.Instance)
{
    internal Action<SqliteConnection>? AfterRebuild { get; set; }
    internal int PeopleRebuilds { get; set; }
    public override void RebuildPeopleIndexes(SqliteConnection connection)
    {
        base.RebuildPeopleIndexes(connection);
        PeopleRebuilds++;
        AfterRebuild?.Invoke(connection);
    }
}

internal sealed class PeopleTestSource(
    IOptions<JiraServiceOptions> options, IHttpClientFactory http, JiraDatabase db, IResponseCache cache,
    JiraUserMapper mapper, ILogger<JiraSource> logger) : JiraSource(options, http, db, cache, mapper, logger)
{
    internal int ClearCount { get; private set; }
    internal bool ClearedWhileGateHeld { get; private set; }
    internal Func<bool>? GateHeld { get; set; }
    internal Action? AfterClear { get; set; }
    internal override void ClearUserCache()
    {
        base.ClearUserCache();
        ClearCount++;
        ClearedWhileGateHeld = GateHeld?.Invoke() == true;
        AfterClear?.Invoke();
    }
}

internal sealed class PeopleTestHttpFactory(IOptions<JiraServiceOptions> options) : HttpMessageHandler, IHttpClientFactory
{
    internal Dictionary<string, string> Issues { get; } = new(StringComparer.Ordinal);
    internal Func<CancellationToken, Task>? BeforeResponse { get; set; }
    internal HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
    internal string? SearchBody { get; set; }
    internal string? ResponseBody { get; set; }
    internal string? LastClient { get; private set; }
    internal int RequestCount;
    public HttpClient CreateClient(string name)
    {
        LastClient = name;
        // Exercise the existing authentication handler without making a network call.
        return new HttpClient(new JiraAuthHandler(options) { InnerHandler = new NonDisposingHandler(this) });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref RequestCount);
        if (BeforeResponse is not null) await BeforeResponse(ct);
        string key = Uri.UnescapeDataString(request.RequestUri!.Segments.Last());
        string body = ResponseBody ?? (request.RequestUri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
            ? SearchBody ?? """{"total":0,"issues":[]}"""
            : Issues.GetValueOrDefault(key, JiraPeopleTestFixture.Json("FHIR-1")));
        return new(StatusCode) { Content = new StringContent(body), RequestMessage = request };
    }

    private sealed class NonDisposingHandler(PeopleTestHttpFactory owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => owner.SendAsync(request, ct);
    }
}
