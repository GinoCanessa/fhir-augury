using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Queue;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Discovery;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processing.Jira.Common.Tests.Authoring;

namespace FhirAugury.Processing.Jira.Common.Tests.Filtering;

public sealed class JiraConfiguredTicketSelectorTests
{
    private static readonly DateTimeOffset Revision =
        new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Select_InactiveLabelsUsesBoundedLocalCandidatesWithoutHttp(bool blankLists)
    {
        using JiraAuthoringTestFixture fixture = new();
        await SeedPoolAsync(fixture, 3);
        ResolvedJiraProcessingFilters filters = new()
        {
            TicketStatuses = ["Triaged"],
            LabelsToInclude = blankLists ? [null!, "", "  "] : null,
            LabelsToExclude = blankLists ? ["\t", ""] : null,
        };

        IReadOnlyList<JiraProcessingSourceTicketRecord> selected =
            await fixture.Selector.SelectAsync(filters, 2, CancellationToken.None);

        Assert.Equal(["FHIR-0001", "FHIR-0002"], selected.Select(ticket => ticket.Key));
        Assert.Empty(fixture.Matcher.Calls);
    }

    [Fact]
    public async Task Select_EmptyPoolDoesNotCallMatcher()
    {
        using JiraAuthoringTestFixture fixture = new();

        Assert.Empty(await fixture.Selector.SelectAsync(
            ActiveFilters(), 10, CancellationToken.None));

        Assert.Empty(fixture.Matcher.Calls);
    }

    [Fact]
    public async Task Select_ContinuesPastTwoRejectedBatchesBeforeApplyingLimit()
    {
        using JiraAuthoringTestFixture fixture = new();
        await SeedPoolAsync(fixture, 1008);
        fixture.Matcher.Enqueue([]);
        fixture.Matcher.Enqueue([]);
        fixture.Matcher.Enqueue(["FHIR-1008", "FHIR-1007", "FHIR-1006"]);
        ResolvedJiraProcessingFilters filters = ActiveFilters();

        IReadOnlyList<JiraProcessingSourceTicketRecord> selected =
            await fixture.Selector.SelectAsync(filters, 2, CancellationToken.None);

        Assert.Equal(["FHIR-1006", "FHIR-1007"], selected.Select(ticket => ticket.Key));
        Assert.Equal([500, 500, 8], fixture.Matcher.Calls.Select(call => call.Keys.Count));
        Assert.Equal(
            Enumerable.Range(1, 1008).Select(index => $"FHIR-{index:D4}"),
            fixture.Matcher.Calls.SelectMany(call => call.Keys));
        Assert.All(fixture.Matcher.Calls, call =>
        {
            Assert.InRange(call.Keys.Count, 1, 500);
            Assert.Equal(call.Keys.Count, call.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Same(filters, call.Filters);
        });
    }

    [Fact]
    public async Task Select_PreservesLocalOrderAndIgnoresUnsubmittedKeys()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.SeedAsync("FHIR-3", Revision);
        await fixture.SeedAsync("FHIR-1", Revision);
        await fixture.SeedAsync("FHIR-5", Revision.AddMinutes(1));
        await fixture.SeedAsync("FHIR-4", Revision);
        await fixture.SeedAsync("FHIR-2", Revision);
        await fixture.SeedAsync("FHIR-9", Revision.AddMinutes(-1));
        fixture.Matcher.Enqueue(
            ["FHIR-999", "FHIR-5", "fhir-3", "FHIR-2", "fhir-1", "FHIR-9"]);

        IReadOnlyList<JiraProcessingSourceTicketRecord> selected =
            await fixture.Selector.SelectAsync(ActiveFilters(), 10, CancellationToken.None);

        Assert.Equal(
            ["FHIR-9", "FHIR-1", "FHIR-2", "FHIR-3", "FHIR-5"],
            selected.Select(ticket => ticket.Key));
        Assert.Equal(
            ["FHIR-9", "FHIR-1", "FHIR-2", "FHIR-3", "FHIR-4", "FHIR-5"],
            Assert.Single(fixture.Matcher.Calls).Keys);
        Assert.DoesNotContain(selected, ticket => ticket.Key is "FHIR-4" or "FHIR-999");
    }

    [Fact]
    public async Task Select_StopsAtLimitWithoutCallingUnneededBatches()
    {
        using JiraAuthoringTestFixture fixture = new();
        await SeedPoolAsync(fixture, 501);
        fixture.Matcher.Enqueue(["FHIR-0501", "FHIR-0001"]);

        IReadOnlyList<JiraProcessingSourceTicketRecord> selected =
            await fixture.Selector.SelectAsync(ActiveFilters(), 1, CancellationToken.None);

        Assert.Equal("FHIR-0001", Assert.Single(selected).Key);
        Assert.Equal(500, Assert.Single(fixture.Matcher.Calls).Keys.Count);
    }

    [Fact]
    public async Task Select_PreservesFacetAndFrozenRevisionEligibility()
    {
        using JiraAuthoringTestFixture fixture = new();
        await fixture.ActivateAsync();
        JiraProcessingSourceTicketRecord frozen =
            await fixture.SeedAsync("FHIR-1", Revision.AddDays(-2));
        JiraProcessingSourceTicketRecord oldRevision =
            await fixture.SeedAsync("FHIR-2", Revision.AddDays(-1));
        await fixture.Coordinator.CreateExplicitRunAsync([frozen, oldRevision], databaseOnly: true);
        JiraProcessingSourceTicketRecord updated =
            await fixture.SeedAsync("FHIR-2", Revision);
        await fixture.SourceStore.MarkCompleteAsync(updated, Revision, CancellationToken.None);
        JiraProcessingSourceTicketRecord failed =
            await fixture.SeedAsync("FHIR-3", Revision.AddMinutes(1));
        await fixture.SourceStore.MarkErrorAsync(
            failed, "legacy failure", 1, Revision, CancellationToken.None);

        JiraIssueSummaryEntry candidate = new()
        {
            Key = "FHIR-10",
            Title = "Rejected facet",
            ProjectKey = "FHIR",
            Status = "Triaged",
            Type = "Change Request",
            WorkGroup = "FHIR-I",
            Specification = "FHIR Core",
            UpdatedAt = Revision.AddDays(-3),
        };
        JiraIssueSummaryEntry[] rejected =
        [
            candidate with { Key = "FHIR-10", ProjectKey = "OTHER" },
            candidate with { Key = "FHIR-11", Status = "Submitted" },
            candidate with { Key = "FHIR-12", WorkGroup = "Other" },
            candidate with { Key = "FHIR-13", Type = "Bug" },
            candidate with { Key = "FHIR-14", Specification = "Other" },
        ];
        foreach (JiraIssueSummaryEntry ticket in rejected)
        {
            await fixture.SourceStore.UpsertAsync(ticket, "fhir", false, CancellationToken.None);
        }
        await fixture.SourceStore.UpsertAsync(
            candidate with { Key = "FHIR-15" }, "pss", false, CancellationToken.None);
        ResolvedJiraProcessingFilters filters = ActiveFilters() with
        {
            SourceTicketShape = "FHIR",
            Projects = ["fhir"],
            TicketStatuses = ["tRiAgEd"],
            TicketTypes = ["change request"],
            WorkGroups = ["fhir-i"],
            Specifications = ["fhir core"],
        };
        fixture.Matcher.Enqueue(["FHIR-3", "FHIR-2", "FHIR-1", "FHIR-10"]);

        IReadOnlyList<JiraProcessingSourceTicketRecord> selected =
            await fixture.Selector.SelectAsync(filters, 10, CancellationToken.None);

        Assert.Equal(["FHIR-2", "FHIR-3"], Assert.Single(fixture.Matcher.Calls).Keys);
        Assert.Equal(["FHIR-2", "FHIR-3"], selected.Select(ticket => ticket.Key));
        Assert.Equal(Revision, selected[0].LastUpdated);
        Assert.Equal(ProcessingStatusValues.Complete, selected[0].ProcessingStatus);
        Assert.Equal(ProcessingStatusValues.Error, selected[1].ProcessingStatus);
    }

    [Fact]
    public async Task Select_DoesNotMutateSourceRecordsOrProvenance()
    {
        using JiraAuthoringTestFixture fixture = new();
        JiraProcessingSourceTicketRecord source = await fixture.SeedAsync(
            "FHIR-1", Revision, sourceRefresh: Revision.AddHours(-1), contentRevision: 42);
        Assert.True(await fixture.SourceStore.ClaimItemAsync(
            source, Revision, CancellationToken.None));
        await fixture.SourceStore.MarkErrorAsync(
            source, "legacy error", 7, Revision.AddMinutes(1), CancellationToken.None);
        JiraProcessingSourceTicketRecord before = Assert.IsType<JiraProcessingSourceTicketRecord>(
            await fixture.SourceStore.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None));
        fixture.Matcher.Enqueue(async (keys, _, ct) =>
        {
            using (new FileStream(fixture.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
            JiraProcessingSourceTicketRecord during = Assert.IsType<JiraProcessingSourceTicketRecord>(
                await fixture.SourceStore.GetByKeyAsync("FHIR-1", "fhir", ct));
            Assert.Equal(before, during);
            Assert.Equal(1, (await fixture.SourceStore.GetQueueStatsAsync(ct)).ErrorCount);
            return keys;
        });

        JiraProcessingSourceTicketRecord selected = Assert.Single(
            await fixture.Selector.SelectAsync(ActiveFilters(), 10, CancellationToken.None));
        JiraProcessingSourceTicketRecord after = Assert.IsType<JiraProcessingSourceTicketRecord>(
            await fixture.SourceStore.GetByKeyAsync("FHIR-1", "fhir", CancellationToken.None));

        Assert.Equal(before, selected);
        Assert.Equal(before, after);
        Assert.Equal(42, after.SourceContentRevision);
        Assert.Equal(Revision.AddHours(-1), after.SourceProjectLastSuccessfulRefreshAt);
        Assert.Equal(before.LastSyncedAt, after.LastSyncedAt);
        Assert.Equal(
            JiraProcessingSourceTicketStore.GetSourceRevision(before),
            JiraProcessingSourceTicketStore.GetSourceRevision(after));
        Assert.Single(fixture.Matcher.Calls);
    }

    [Fact]
    public async Task Select_LaterBatchFailureDoesNotReturnPartialSelection()
    {
        using JiraAuthoringTestFixture fixture = new();
        await SeedPoolAsync(fixture, 501);
        JiraTicketSelectionUnavailableException failure = new("Selection response was incomplete.");
        fixture.Matcher.Enqueue(["FHIR-0001"]);
        fixture.Matcher.Enqueue((_, _, _) => throw failure);

        JiraTicketSelectionUnavailableException actual =
            await Assert.ThrowsAsync<JiraTicketSelectionUnavailableException>(
                () => fixture.Selector.SelectAsync(ActiveFilters(), 2, CancellationToken.None));

        Assert.Same(failure, actual);
        Assert.Equal([500, 1], fixture.Matcher.Calls.Select(call => call.Keys.Count));
        Assert.Null(await fixture.AuthoringStore.GetOldestQueuedRunAsync(fixture.Coordinator.ProcessorKind));
    }

    [Fact]
    public async Task Select_LaterBatchCancellationDoesNotReturnPartialSelection()
    {
        using JiraAuthoringTestFixture fixture = new();
        using CancellationTokenSource cancellation = new();
        await SeedPoolAsync(fixture, 501);
        fixture.Matcher.Enqueue(["FHIR-0001"]);
        fixture.Matcher.Enqueue((_, _, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<string>>([]);
        });

        OperationCanceledException actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Selector.SelectAsync(ActiveFilters(), 2, cancellation.Token));

        Assert.Equal(cancellation.Token, actual.CancellationToken);
        Assert.Equal([500, 1], fixture.Matcher.Calls.Select(call => call.Keys.Count));
        Assert.All(fixture.Matcher.Calls, call => Assert.Equal(cancellation.Token, call.Token));
    }

    [Fact]
    public async Task Select_AlreadyCanceledDoesNotCallMatcher()
    {
        using JiraAuthoringTestFixture fixture = new();
        using CancellationTokenSource cancellation = new();
        await fixture.SeedAsync("FHIR-1", Revision);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Selector.SelectAsync(ActiveFilters(), 1, cancellation.Token));

        Assert.Empty(fixture.Matcher.Calls);
    }

    private static ResolvedJiraProcessingFilters ActiveFilters() => new()
    {
        TicketStatuses = ["Triaged"],
        LabelsToInclude = ["cohort"],
        LabelsToExclude = ["blocked"],
    };

    private static async Task SeedPoolAsync(JiraAuthoringTestFixture fixture, int count)
    {
        for (int index = 1; index <= count; index++)
        {
            await fixture.SeedAsync($"FHIR-{index:D4}", Revision.AddSeconds(index));
        }
    }
}
