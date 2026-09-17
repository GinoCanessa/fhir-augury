using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FhirAugury.Common.Api;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Contracts;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Publishing.Tickets.Tests;

public sealed class DiscussionSiteDatabaseBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"discussion-site-builder-{Guid.NewGuid():N}");

    public DiscussionSiteDatabaseBuilderTests()
        => Directory.CreateDirectory(_root);

    public void Dispose()
        => TestFileCleanup.SafeDeleteDirectory(_root);

    [Theory]
    [InlineData(1, "Jan")]
    [InlineData(2, "Feb")]
    [InlineData(3, "Mar")]
    [InlineData(4, "Apr")]
    [InlineData(5, "May")]
    [InlineData(6, "Jun")]
    [InlineData(7, "Jul")]
    [InlineData(8, "Aug")]
    [InlineData(9, "Sept")]
    [InlineData(10, "Oct")]
    [InlineData(11, "Nov")]
    [InlineData(12, "Dec")]
    public async Task Title_UsesUtcAndFixedEnglishMonths(int month, string label)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");

            DateTimeOffset local = new(
                2026, month, 1, 23, 30, 0, TimeSpan.FromHours(-2));
            TicketSnapshotFixture fixture =
                await TicketSnapshotFixture.CreatePreparerAsync(
                    _root,
                    schemaVersion: 3,
                    firstJiraUpdatedAt: local.ToString("O"));
            ResolvedFilters filters = new("FHIR", null, null);
            DiscussionSiteDatabaseBuilder.BuildResult built =
                await DiscussionSiteDatabaseBuilder.BuildAsync(
                    fixture.DatabasePath, fixture.Descriptor,
                    "Custom & Review", filters);
            try
            {
                Assert.Equal(
                    $"Custom & Review - {label} 2, 2026 (filtered: spec=FHIR)",
                    built.Presentation.SiteName);
                Assert.Equal("Custom & Review", built.Presentation.BaseTitle);
                Assert.Equal(
                    local.ToUniversalTime(),
                    built.Presentation.CorpusSummary.MaxJiraUpdatedAt);
                Assert.Equal(
                    TimeSpan.Zero,
                    built.Presentation.CorpusSummary.MaxJiraUpdatedAt?.Offset);
                await DiscussionSiteDatabaseValidator.ValidateAsync(
                    built.TempDbPath, fixture.DatabasePath, fixture.Descriptor,
                    "Custom & Review", filters);
            }
            finally
            {
                TestFileCleanup.SafeDeleteFile(built.TempDbPath);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Theory]
    [InlineData(null, null, null, 2, 15)]
    [InlineData("FHIR", null, null, 1, 14)]
    [InlineData(null, "FHIR", null, 1, 14)]
    [InlineData(null, null, "FHIR Infrastructure", 1, 14)]
    [InlineData("FHIR", "FHIR", "FHIR Infrastructure", 1, 14)]
    [InlineData(null, "CDS", null, 1, 15)]
    public async Task Title_UsesOnlyExportedSelfJiraUpdatedAt(
        string? specification, string? project, string? workGroup,
        int ticketCount, int day)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, includeSecondTicket: true, schemaVersion: 3,
                useMultipleRuns: true, includeRendererEvidence: true,
                firstJiraUpdatedAt: "2026-09-14T06:00:00Z",
                secondJiraUpdatedAt: "2026-09-15T06:00:00Z");
        await ExecuteAsync(
            fixture.DatabasePath,
            """
            INSERT INTO prepared_jira_hydration(
                Id, TicketKey, JiraKey, Title, UpdatedAt)
            VALUES('unrelated', 'FHIR-9999', 'FHIR-9999',
                   'Later unrelated ticket', '2031-11-27T12:00:00Z');
            """);
        byte[] sourceBytes = await File.ReadAllBytesAsync(fixture.DatabasePath);
        byte[] descriptorBytes = await File.ReadAllBytesAsync(fixture.DescriptorPath);
        ResolvedFilters filters = new(specification, project, workGroup);
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor,
                "Tickets for Discussion", filters);
        try
        {
            DiscussionCorpusSummary summary = built.Presentation.CorpusSummary;
            Assert.Equal(ticketCount, summary.TicketCount);
            Assert.Equal(ticketCount, summary.ExportedProjectCount);
            Assert.Equal(ticketCount, summary.ValidJiraUpdatedAtCount);
            Assert.Equal(ticketCount, summary.TicketsWithPublicReporter);
            Assert.Equal(ticketCount, summary.TicketsWithPublicAssignee);
            Assert.Equal(project == "CDS" ? 0 : 1, summary.TicketsWithPublicRequester);
            Assert.Equal(project == "CDS" ? 0 : 7, summary.LinksByKind.Sum(link => link.TotalRows));
            Assert.Equal(DiscussionDateCoverage.Complete, summary.DateCoverage);
            Assert.Equal(new DateTimeOffset(2026, 9, day, 6, 0, 0, TimeSpan.Zero),
                summary.MaxJiraUpdatedAt);
            Assert.Equal(
                $"Tickets for Discussion - Sept {day}, 2026{filters.ToTitleSuffix()}",
                built.Presentation.SiteName);
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath, fixture.DatabasePath, fixture.Descriptor,
                "Tickets for Discussion", filters);
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(fixture.DatabasePath));
            Assert.Equal(descriptorBytes, await File.ReadAllBytesAsync(fixture.DescriptorPath));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task Title_IgnoresEveryOtherClock()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, includeSecondTicket: true, schemaVersion: 3,
                includeRendererEvidence: true,
                firstJiraUpdatedAt: "2026-09-15T01:00:00Z",
                secondJiraUpdatedAt: "2026-09-14T03:00:00Z",
                ticketSavedAt: new DateTimeOffset(2030, 1, 17, 8, 0, 0, TimeSpan.Zero),
                snapshotCreatedAt: new DateTimeOffset(2032, 2, 18, 9, 0, 0, TimeSpan.Zero));
        await ExecuteAsync(
            fixture.DatabasePath,
            """
            UPDATE prepared_ticket_hydration
            SET SourceLastSuccessfulRefreshAt = '2028-03-19T10:00:00+00:00',
                HydratedAt = '2029-04-20T11:00:00+00:00';
            UPDATE authoring_run_input_provenance
            SET LatestSuccessfulRefreshAt = '2028-05-21T12:00:00+00:00';
            UPDATE authoring_run_items
            SET ExpectedSourceRevision = 'jira-revision-2033-06-22T13:00:00Z';
            UPDATE authoring_result_receipts
            SET ExpectedSourceRevision = 'jira-revision-2033-06-22T13:00:00Z',
                ObservedSourceRevision = 'jira-revision-2033-06-22T13:00:00Z',
                PersistedAt = '2034-07-23T14:00:00+00:00';
            UPDATE prepared_jira_hydration
            SET UpdatedAt = '2035-08-24T15:00:00Z'
            WHERE JiraKey <> TicketKey;
            """);
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor,
                "Tickets for Discussion", ResolvedFilters.None);
        try
        {
            Assert.Equal(
                "Tickets for Discussion - Sept 15, 2026",
                built.Presentation.SiteName);
            Assert.Equal(
                new DateTimeOffset(2028, 5, 21, 12, 0, 0, TimeSpan.Zero),
                built.Presentation.JiraSourceLastSuccessfulRefreshAt);
            Assert.Equal(
                "2030-01-17T08:00:00.0000000+00:00",
                await ScalarAsync<string>(built.TempDbPath,
                    "SELECT SavedAt FROM tickets WHERE Key = 'FHIR-1001'"));
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath, fixture.DatabasePath, fixture.Descriptor,
                "Tickets for Discussion", ResolvedFilters.None);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData(TicketSnapshotFixture.FirstJiraUpdatedAt, TicketSnapshotFixture.SecondJiraUpdatedAt, false, "complete", 2, 6)]
    [InlineData(null, TicketSnapshotFixture.SecondJiraUpdatedAt, false, "partial", 1, 6)]
    [InlineData(TicketSnapshotFixture.FirstJiraUpdatedAt, null, false, "partial", 1, 5)]
    [InlineData(null, null, false, "none", 0, 0)]
    [InlineData(TicketSnapshotFixture.FirstJiraUpdatedAt, TicketSnapshotFixture.SecondJiraUpdatedAt, true, "empty", 0, 0)]
    public async Task Title_RequiresCompleteDateCoverage(
        string? first, string? second, bool empty,
        string coverage, int validCount, int maximumDay)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, includeSecondTicket: true, schemaVersion: 3,
                firstJiraUpdatedAt: first, secondJiraUpdatedAt: second);
        ResolvedFilters filters = empty
            ? new("FHIR", "CDS", null)
            : ResolvedFilters.None;
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor, "Tickets", filters);
        try
        {
            DiscussionCorpusSummary summary = built.Presentation.CorpusSummary;
            Assert.Equal(empty ? 0 : 2, summary.TicketCount);
            Assert.Equal(empty ? 0 : 2, summary.ExportedProjectCount);
            Assert.Equal(validCount, summary.ValidJiraUpdatedAtCount);
            Assert.Equal(coverage, summary.DateCoverage);
            Assert.Equal(
                maximumDay == 0 ? (int?)null : maximumDay,
                summary.MaxJiraUpdatedAt?.Day);
            Assert.Equal(
                "Tickets" + (coverage == "complete" ? " - Sept 6, 2026" : "") +
                filters.ToTitleSuffix(),
                built.Presentation.SiteName);
            if (empty)
            {
                Assert.Equal(0, summary.TicketsWithPublicReporter);
                Assert.Equal(0, summary.TicketsWithPublicAssignee);
                Assert.Equal(0, summary.TicketsWithPublicRequester);
                Assert.All(summary.LinksByKind, links => Assert.Equal(0, links.TotalRows));
            }
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath, fixture.DatabasePath, fixture.Descriptor,
                "Tickets", filters);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData("2026-09-15T00:30:00Z")]
    [InlineData("2026-09-14T19:30:00-05:00")]
    [InlineData("2026-09-15T02:30:00.0000000+02:00")]
    [InlineData("2026-09-15 02:30:00+02:00")]
    [InlineData("2026-09-15 02:30:00.0000000 +02:00")]
    [InlineData("2026-09-15 00:30:00.000Z")]
    public async Task JiraDates_NormalizeEquivalentOffsetBearingInstants(string date)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, includeSecondTicket: true,
                firstJiraUpdatedAt: date,
                secondJiraUpdatedAt: "2026-09-14T17:30:00-07:00");
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor, "Tickets", ResolvedFilters.None);
        try
        {
            Assert.Equal("Tickets - Sept 15, 2026", built.Presentation.SiteName);
            Assert.Equal(
                ["2026-09-15T00:30:00.0000000+00:00"],
                await ReadStringsAsync(built.TempDbPath,
                    "SELECT DISTINCT JiraUpdatedAt FROM tickets"));
            Assert.Equal(TimeSpan.Zero, built.Presentation.CorpusSummary.MaxJiraUpdatedAt?.Offset);
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath, fixture.DatabasePath, fixture.Descriptor,
                "Tickets", ResolvedFilters.None);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task JiraDates_PreserveTickPrecisionWhenSelectingMaximum()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, includeSecondTicket: true,
                firstJiraUpdatedAt: "2026-09-15T02:30:00.1234567+02:00",
                secondJiraUpdatedAt: "2026-09-14T19:30:00.1234566-05:00");
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor, "Tickets", ResolvedFilters.None);
        try
        {
            const string maximum = "2026-09-15T00:30:00.1234567+00:00";
            Assert.Equal(maximum,
                built.Presentation.CorpusSummary.MaxJiraUpdatedAt?.ToString("O", CultureInfo.InvariantCulture));
            Assert.Equal(maximum, await ScalarAsync<string>(
                built.TempDbPath, "SELECT MAX(JiraUpdatedAt) FROM tickets"));
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath, fixture.DatabasePath, fixture.Descriptor,
                "Tickets", ResolvedFilters.None);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData("blob")]
    [InlineData("non-utc")]
    [InlineData("noncanonical")]
    [InlineData("invalid")]
    public async Task RendererDates_RejectNonTextOrNonCanonicalUtcValues(string mutation)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor, "Tickets", ResolvedFilters.None);
        try
        {
            object date = mutation switch
            {
                "blob" => Encoding.UTF8.GetBytes(TicketSnapshotFixture.FirstJiraUpdatedAt),
                "non-utc" => "2026-09-05T14:00:00.0000000+02:00",
                "noncanonical" => "2026-09-05T12:00:00Z",
                "invalid" => "not a date",
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            };
            await ExecuteAsync(built.TempDbPath, "UPDATE tickets SET JiraUpdatedAt = @date",
                ("@date", date));
            if (mutation == "blob")
            {
                await RewriteCorpusSummaryAsync(built,
                    built.Presentation.CorpusSummary with { MaxJiraUpdatedAt = null });
            }
            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    DiscussionSiteDatabaseValidator.ValidateAsync(built.TempDbPath));
            Assert.Contains("JiraUpdatedAt", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("not a date")]
    [InlineData("2026-09-15")]
    [InlineData("2026-09-15T12:30:00")]
    [InlineData("2026-09-15 12:30:00.1234567")]
    [InlineData("09/15/2026 12:30:00+00:00")]
    [InlineData("2026-02-29T12:30:00Z")]
    [InlineData("2026-09-15T12:30:00+25:00")]
    public async Task JiraDates_RejectMalformedOrTimezoneAmbiguousNonNullValues(string date)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, firstJiraUpdatedAt: date);
        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                DiscussionSiteDatabaseBuilder.BuildAsync(
                    fixture.DatabasePath, fixture.Descriptor,
                    "Tickets", ResolvedFilters.None));
        Assert.Contains("UpdatedAt", exception.Message, StringComparison.Ordinal);
        Assert.Contains("explicit time zone", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JiraDates_IgnoreMalformedUnexportedAndLinkedValues()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, includeSecondTicket: true, includeRendererEvidence: true,
                secondJiraUpdatedAt: "timezone-ambiguous");
        await ExecuteAsync(fixture.DatabasePath,
            "UPDATE prepared_jira_hydration SET UpdatedAt = 'malformed linked date' WHERE JiraKey <> TicketKey");
        ResolvedFilters filters = new(null, "FHIR", null);
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor, "Tickets", filters);
        try
        {
            Assert.Equal("Tickets - Sept 5, 2026 (filtered: project=FHIR)",
                built.Presentation.SiteName);
            Assert.Equal(1, built.Presentation.CorpusSummary.ValidJiraUpdatedAtCount);
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath, fixture.DatabasePath, fixture.Descriptor, "Tickets", filters);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LegacySnapshot_DatesDoNotUpgradePeopleOrReadiness(int schemaVersion)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, schemaVersion: schemaVersion,
                firstJiraUpdatedAt: "2026-09-15T01:00:00Z");
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor,
                "Tickets for Discussion", ResolvedFilters.None);
        try
        {
            Assert.Equal("Tickets for Discussion - Sept 15, 2026", built.Presentation.SiteName);
            Assert.False(built.Presentation.Readiness.IsReady);
            Assert.Contains(built.Presentation.Readiness.Reasons, reason =>
                reason.Code == DiscussionPublicationReadinessReasonCodes.LegacySnapshotSchema);
            Assert.Equal(DiscussionDateCoverage.Complete, built.Presentation.CorpusSummary.DateCoverage);
            Assert.Equal(0, built.Presentation.CorpusSummary.TicketsWithPublicReporter);
            Assert.Equal(0, built.Presentation.CorpusSummary.TicketsWithPublicAssignee);
            Assert.Equal(0, built.Presentation.CorpusSummary.TicketsWithPublicRequester);
            Assert.Equal(0, await ScalarAsync<long>(
                built.TempDbPath, "SELECT COUNT(*) FROM ticket_people WHERE DisplayName IS NOT NULL"));
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath, fixture.DatabasePath, fixture.Descriptor,
                "Tickets for Discussion", ResolvedFilters.None);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData("ticket-count")]
    [InlineData("project-count")]
    [InlineData("valid-date-count")]
    [InlineData("maximum")]
    [InlineData("coverage")]
    [InlineData("reporter")]
    [InlineData("assignee")]
    [InlineData("requester")]
    [InlineData("link-total")]
    [InlineData("link-resolved")]
    [InlineData("link-unresolved")]
    [InlineData("link-no-url")]
    [InlineData("link-kind")]
    public async Task CorpusSummary_IsValidatedAgainstProjectedRows(string fact)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, includeSecondTicket: true, schemaVersion: 3,
                includeRendererEvidence: true, includeUntrustedPeople: true);
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor, "Tickets", ResolvedFilters.None);
        try
        {
            DiscussionCorpusSummary summary = built.Presentation.CorpusSummary;
            Assert.Equal(2, summary.TicketCount);
            Assert.Equal(2, summary.ExportedProjectCount);
            Assert.Equal(2, summary.ValidJiraUpdatedAtCount);
            Assert.Equal(1, summary.TicketsWithPublicReporter);
            Assert.Equal(1, summary.TicketsWithPublicAssignee);
            Assert.Equal(1, summary.TicketsWithPublicRequester);
            Assert.Equal(
                [
                    new DiscussionLinkCoverage("github", 1, 0, 0, 1),
                    new DiscussionLinkCoverage("jira", 3, 2, 1, 0),
                    new DiscussionLinkCoverage("jira-xref", 1, 1, 0, 0),
                    new DiscussionLinkCoverage("repo", 1, 1, 0, 0),
                    new DiscussionLinkCoverage("zulip", 1, 1, 0, 0),
                ],
                summary.LinksByKind);
            Assert.Equal(
                TicketSitePresentationJson.Serialize(summary),
                await ScalarAsync<string>(built.TempDbPath,
                    "SELECT CorpusSummaryJson FROM site_metadata"));
            await DiscussionSiteDatabaseValidator.ValidateAsync(built.TempDbPath);

            DiscussionLinkCoverage[] ChangeLink(
                Func<DiscussionLinkCoverage, DiscussionLinkCoverage> change)
                => summary.LinksByKind.Select(link =>
                    link.Kind == "zulip" ? change(link) : link).ToArray();
            DiscussionCorpusSummary forged = fact switch
            {
                "ticket-count" => summary with { TicketCount = 3 },
                "project-count" => summary with { ExportedProjectCount = 1 },
                "valid-date-count" => summary with { ValidJiraUpdatedAtCount = 1 },
                "maximum" => summary with { MaxJiraUpdatedAt = summary.MaxJiraUpdatedAt?.AddHours(1) },
                "coverage" => summary with { DateCoverage = DiscussionDateCoverage.Partial },
                "reporter" => summary with { TicketsWithPublicReporter = 2 },
                "assignee" => summary with { TicketsWithPublicAssignee = 0 },
                "requester" => summary with { TicketsWithPublicRequester = 3 },
                "link-total" => summary with { LinksByKind = ChangeLink(link => link with { TotalRows = 2 }) },
                "link-resolved" => summary with { LinksByKind = ChangeLink(link =>
                    link with { ResolvedSafeLinks = 0, UnresolvedWithRetainedSafeLinks = 1 }) },
                "link-unresolved" => summary with { LinksByKind = ChangeLink(link =>
                    link with { UnresolvedWithRetainedSafeLinks = 1 }) },
                "link-no-url" => summary with { LinksByKind = ChangeLink(link =>
                    link with { ResolvedSafeLinks = 0, WithoutUsableUrl = 1 }) },
                "link-kind" => summary with { LinksByKind = summary.LinksByKind
                    .Where(link => link.Kind != "repo").ToArray() },
                _ => throw new ArgumentOutOfRangeException(nameof(fact)),
            };
            await RewriteCorpusSummaryAsync(built, forged);
            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    DiscussionSiteDatabaseValidator.ValidateAsync(built.TempDbPath));
            Assert.Contains("projected ticket, people, and link rows",
                exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData("whitespace")]
    [InlineData("missing-field")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("null")]
    [InlineData("non-utc-maximum")]
    public async Task CorpusSummary_RequiresCanonicalCompleteJson(string mutation)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(_root);
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor, "Tickets", ResolvedFilters.None);
        try
        {
            string json = TicketSitePresentationJson.Serialize(built.Presentation.CorpusSummary);
            JsonObject value = JsonNode.Parse(json)!.AsObject();
            if (mutation == "missing-field")
                Assert.True(value.Remove("ticketsWithPublicRequester"));
            if (mutation == "unknown-field")
                value["unknown"] = 0;
            if (mutation == "non-utc-maximum")
                value["maxJiraUpdatedAt"] = "2026-09-05T14:00:00+02:00";
            string forged = mutation switch
            {
                "whitespace" => json + "\n",
                "duplicate-field" => json.Insert(1, "\"ticketCount\":1,"),
                "null" => "null",
                _ => value.ToJsonString(),
            };
            await ExecuteAsync(built.TempDbPath,
                "UPDATE site_metadata SET CorpusSummaryJson = @json", ("@json", forged));
            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    DiscussionSiteDatabaseValidator.ValidateAsync(built.TempDbPath));
            Assert.Contains("corpus-summary", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CorpusSummary_RejectsPlausibleWrongSourceMaximum(int schemaVersion)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, includeSecondTicket: true, schemaVersion: schemaVersion,
                includeRendererEvidence: true);
        string originalHash = await ComputeHashAsync(fixture.DatabasePath);
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor, "Tickets", ResolvedFilters.None);
        try
        {
            string linkedDate = await ScalarAsync<string>(fixture.DatabasePath,
                "SELECT UpdatedAt FROM prepared_jira_hydration WHERE JiraKey = 'FHIR-2002'");
            await ExecuteAsync(built.TempDbPath,
                "UPDATE tickets SET JiraUpdatedAt = @date WHERE Key = 'FHIR-1001'",
                ("@date", linkedDate));
            await RewriteCorpusSummaryAsync(built,
                built.Presentation.CorpusSummary with
                {
                    MaxJiraUpdatedAt = DateTimeOffset.Parse(linkedDate, CultureInfo.InvariantCulture),
                });

            DiscussionSiteDatabaseValidator.ValidationResult internallyConsistent =
                await DiscussionSiteDatabaseValidator.ValidateAsync(built.TempDbPath);
            Assert.Equal("Tickets - Sept 20, 2026", internallyConsistent.Presentation.SiteName);
            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    DiscussionSiteDatabaseValidator.ValidateAsync(
                        built.TempDbPath, fixture.DatabasePath, fixture.Descriptor,
                        "Tickets", ResolvedFilters.None));
            Assert.Contains("immutable source projection", exception.Message, StringComparison.Ordinal);
            Assert.Equal(originalHash, await ComputeHashAsync(fixture.DatabasePath));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData(ZulipReferenceBacking.TypedResolver, ZulipReferenceLookupOutcome.NotFound, true, "resolved")]
    [InlineData(ZulipReferenceBacking.LegacyIndexedContext, ZulipReferenceLookupOutcome.Timeout, true, "resolved")]
    [InlineData(ZulipReferenceBacking.Unverified, ZulipReferenceLookupOutcome.HttpFailure, false, "resolved")]
    [InlineData(ZulipReferenceBacking.None, ZulipReferenceLookupOutcome.SourceUnavailable, false, "resolved")]
    [InlineData(ZulipReferenceBacking.TypedResolver, ZulipReferenceLookupOutcome.Resolved, true, "resolved")]
    [InlineData(ZulipReferenceBacking.TypedResolver, ZulipReferenceLookupOutcome.Resolved, true, "unresolved")]
    [InlineData(ZulipReferenceBacking.TypedResolver, ZulipReferenceLookupOutcome.Resolved, true, null)]
    public async Task ZulipProjection_DecodesOutcomesAndDistinguishesBacking(
        ZulipReferenceBacking backing, ZulipReferenceLookupOutcome outcome,
        bool sourceBacked, string? status)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, includeSecondTicket: true, schemaVersion: 3,
                includeRendererEvidence: true);
        const string suppliedUrl = "http://chat.example/review?view=compact#message-123";
        string metadata = ZulipReferenceHydrationReason.Serialize(new()
        {
            Backing = backing,
            LatestOutcome = outcome,
            Diagnostics = [ZulipReferenceDiagnosticCode.InvalidTimestamp],
        });
        await ExecuteAsync(fixture.DatabasePath,
            """
            UPDATE prepared_zulip_hydration
            SET Url = @url, HydrationReason = @reason, HydrationStatus = @status
            """,
            ("@url", suppliedUrl), ("@reason", metadata), ("@status", status));
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor, "Tickets", ResolvedFilters.None);
        try
        {
            bool resolved = outcome == ZulipReferenceLookupOutcome.Resolved && status == "resolved";
            string reason = await ScalarAsync<string>(built.TempDbPath,
                "SELECT HydrationReason FROM related_items WHERE Kind = 'zulip'");
            Assert.Contains("invalid optional source timestamp", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("zulip-reference-", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("\"backing\"", reason, StringComparison.Ordinal);
            if (!resolved)
            {
                Assert.Contains(
                    outcome == ZulipReferenceLookupOutcome.Resolved
                        ? "does not confirm the recorded resolved outcome"
                        : "Zulip lookup failed:",
                    reason, StringComparison.Ordinal);
                Assert.Contains(sourceBacked ? "Last-known source-backed" : "Unverified link",
                    reason, StringComparison.Ordinal);
            }
            Assert.Equal(resolved ? "resolved" : "unresolved",
                await ScalarAsync<string>(built.TempDbPath,
                    "SELECT HydrationStatus FROM related_items WHERE Kind = 'zulip'"));
            Assert.Equal(suppliedUrl, await ScalarAsync<string>(built.TempDbPath,
                "SELECT Url FROM related_items WHERE Kind = 'zulip'"));
            Assert.Equal(suppliedUrl, await ScalarAsync<string>(built.TempDbPath,
                "SELECT Url FROM summary_sources WHERE SummaryKind = 'related-zulip'"));
            Assert.Equal(new DiscussionLinkCoverage("zulip", 1, resolved ? 1 : 0, resolved ? 0 : 1, 0),
                built.Presentation.CorpusSummary.LinksByKind.Single(link => link.Kind == "zulip"));
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath, fixture.DatabasePath, fixture.Descriptor,
                "Tickets", ResolvedFilters.None);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData("zulip-reference-v9:{}", "unknown outcome metadata version")]
    [InlineData("zulip-reference-v1:{bad", "malformed outcome metadata")]
    [InlineData("zulip-reference-v1:{\"backing\":\"future\",\"latestOutcome\":\"resolved\",\"diagnostics\":[]}", "malformed outcome metadata")]
    public async Task ZulipProjection_ReportsMalformedTaggedMetadataAsUnverifiedFailure(
        string metadata, string diagnostic)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, includeSecondTicket: true, includeRendererEvidence: true);
        await ExecuteAsync(fixture.DatabasePath,
            "UPDATE prepared_zulip_hydration SET HydrationReason = @reason",
            ("@reason", metadata));
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor, "Tickets", ResolvedFilters.None);
        try
        {
            string reason = await ScalarAsync<string>(built.TempDbPath,
                "SELECT HydrationReason FROM related_items WHERE Kind = 'zulip'");
            Assert.Contains(diagnostic, reason, StringComparison.Ordinal);
            Assert.Contains("Unverified link", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("Last-known source-backed", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("zulip-reference-", reason, StringComparison.Ordinal);
            Assert.Equal("unresolved", await ScalarAsync<string>(built.TempDbPath,
                "SELECT HydrationStatus FROM related_items WHERE Kind = 'zulip'"));
            Assert.Equal(new DiscussionLinkCoverage("zulip", 1, 0, 1, 0),
                built.Presentation.CorpusSummary.LinksByKind.Single(link => link.Kind == "zulip"));
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath, fixture.DatabasePath, fixture.Descriptor,
                "Tickets", ResolvedFilters.None);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData(0, "resolved", false)]
    [InlineData(0, "unresolved", false)]
    [InlineData(4, "unresolved", true)]
    [InlineData(4, "resolved", true)]
    public async Task ZulipProjection_LegacyResolvedFlagAloneIsNotBacking(
        int messageCount, string status, bool backed)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, includeSecondTicket: true, includeRendererEvidence: true);
        await ExecuteAsync(fixture.DatabasePath,
            "UPDATE prepared_zulip_hydration SET MessageCount = @count, HydrationStatus = @status",
            ("@count", messageCount), ("@status", status));
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor, "Tickets", ResolvedFilters.None);
        try
        {
            bool resolved = backed && status == "resolved";
            Assert.Equal(resolved ? "resolved" : "unresolved",
                await ScalarAsync<string>(built.TempDbPath,
                    "SELECT HydrationStatus FROM related_items WHERE Kind = 'zulip'"));
            if (!resolved)
            {
                string reason = await ScalarAsync<string>(built.TempDbPath,
                    "SELECT HydrationReason FROM related_items WHERE Kind = 'zulip'");
                Assert.Contains(backed ? "Last-known source-backed" : "Unverified link",
                    reason, StringComparison.Ordinal);
            }
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath, fixture.DatabasePath, fixture.Descriptor,
                "Tickets", ResolvedFilters.None);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///private/path")]
    [InlineData(null)]
    public async Task CorpusSummary_CountsOnlyUsableProjectedUrls(string? url)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root, includeSecondTicket: true, includeRendererEvidence: true);
        await ExecuteAsync(fixture.DatabasePath,
            """
            UPDATE prepared_zulip_hydration SET Url = @url;
            UPDATE prepared_repo_hydration SET Url = @url;
            """,
            ("@url", url));
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor, "Tickets", ResolvedFilters.None);
        try
        {
            foreach (string kind in new[] { "zulip", "repo", "github" })
            {
                Assert.Equal(new DiscussionLinkCoverage(kind, 1, 0, 0, 1),
                    built.Presentation.CorpusSummary.LinksByKind.Single(link => link.Kind == kind));
            }
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath, fixture.DatabasePath, fixture.Descriptor,
                "Tickets", ResolvedFilters.None);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task V1ProjectionUsesExactRendererSchemaWithoutMutatingSource()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                includeSecondTicket: true,
                includeRendererEvidence: true);
        string sourceHash = await ComputeHashAsync(fixture.DatabasePath);

        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets for Discussion",
                ResolvedFilters.None);
        try
        {
            DiscussionSiteDatabaseValidator.ValidationResult validation =
                await DiscussionSiteDatabaseValidator.ValidateAsync(
                    built.TempDbPath,
                    fixture.DatabasePath,
                    fixture.Descriptor.SchemaVersion,
                    "Tickets for Discussion",
                    ResolvedFilters.None);

            Assert.Equal(2, built.SurvivingTicketCount);
            Assert.Null(
                validation.Presentation.JiraSourceLastSuccessfulRefreshAt);
            Assert.Equal(
                "Tickets for Discussion - Sept 6, 2026",
                validation.Presentation.SiteName);
            Assert.False(validation.Presentation.Readiness.IsReady);
            Assert.Equal(
                [
                    DiscussionPublicationReadinessReasonCodes
                        .LegacySnapshotSchema,
                    DiscussionPublicationReadinessReasonCodes
                        .MissingOrdinaryProvenance,
                    DiscussionPublicationReadinessReasonCodes
                        .MissingPeoplePolicyProof,
                ],
                validation.Presentation.Readiness.Reasons
                    .Select(reason => reason.Code));
            Assert.Equal(
                7,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    "SELECT COUNT(*) FROM facet_dimensions"));
            Assert.Equal(
                ["project", "wg", "type", "artifact", "page", "impact", "spec"],
                await ReadStringsAsync(
                    built.TempDbPath,
                    """
                    SELECT Dimension
                    FROM facet_dimensions
                    ORDER BY SortOrder
                    """));
            Assert.Equal(
                "by-project",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT Route
                    FROM facet_dimensions
                    WHERE Dimension = 'project'
                    """));
            Assert.Equal(
                DiscussionRendererSchema.Tables
                    .Select(table => table.Name)
                    .Order(StringComparer.Ordinal),
                await ReadSchemaTableNamesAsync(built.TempDbPath));
            Assert.Equal(
                "Snapshot title",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    "SELECT Title FROM tickets WHERE Key = 'FHIR-1001'"));
            Assert.Equal(
                "FHIR",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    "SELECT Specification FROM tickets WHERE Key = 'FHIR-1001'"));
            Assert.Equal(
                "Major",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    "SELECT Priority FROM tickets WHERE Key = 'FHIR-1001'"));
            Assert.Equal(
                "<p>request html</p>",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    "SELECT RequestHtml FROM tickets WHERE Key = 'FHIR-1001'"));
            Assert.Equal(
                4,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    "SELECT COUNT(*) FROM ticket_people"));
            Assert.Equal(
                0,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    "SELECT COUNT(*) FROM ticket_people WHERE DisplayName IS NOT NULL"));
            Assert.Equal(
                sourceHash,
                await ComputeHashAsync(fixture.DatabasePath));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task V2ProjectionDerivesCompleteCorpusFreshnessAndSourcesWithoutPeople()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                includeSecondTicket: true,
                schemaVersion: PreparedTicketSnapshotSchemaV2.Version,
                useMultipleRuns: true,
                includeRendererEvidence: true);

        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets for Discussion",
                ResolvedFilters.None);
        try
        {
            DiscussionSiteDatabaseValidator.ValidationResult validation =
                await DiscussionSiteDatabaseValidator.ValidateAsync(
                    built.TempDbPath,
                    fixture.DatabasePath,
                    fixture.Descriptor.SchemaVersion,
                    "Tickets for Discussion",
                    ResolvedFilters.None);

            DateTimeOffset expectedRefresh =
                new(2026, 9, 10, 23, 30, 0, TimeSpan.Zero);
            Assert.Equal(
                expectedRefresh,
                validation.Presentation.JiraSourceLastSuccessfulRefreshAt);
            Assert.Equal(
                "Tickets for Discussion - Sept 6, 2026",
                validation.Presentation.SiteName);
            Assert.Equal(
                "2026-09-10T23:30:00.0000000+00:00",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT JiraSourceLastSuccessfulRefreshAt
                    FROM site_metadata
                    """));

            Assert.Null(
                await ScalarNullableStringAsync(
                    built.TempDbPath,
                    """
                    SELECT DisplayName
                    FROM ticket_people
                    WHERE TicketKey = 'FHIR-1001'
                      AND Role = 'reporter'
                    """));
            Assert.Equal(
                DiscussionRendererSchema.PersonUnavailable,
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT Availability
                    FROM ticket_people
                    WHERE TicketKey = 'FHIR-1001'
                      AND Role = 'reporter'
                    """));
            Assert.Null(
                await ScalarNullableStringAsync(
                    built.TempDbPath,
                    """
                    SELECT DisplayName
                    FROM ticket_people
                    WHERE TicketKey = 'FHIR-1001'
                      AND Role = 'assignee'
                    """));
            Assert.Empty(
                await ReadStringsAsync(
                    built.TempDbPath,
                    """
                    SELECT DisplayName
                    FROM ticket_people
                    WHERE TicketKey = 'FHIR-1001'
                      AND Role = 'in-person-requester'
                    ORDER BY OrderInRole
                    """));

            Assert.Equal(
                "CDS-2001",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    $"""
                    SELECT TicketKey
                    FROM ticket_facets
                    WHERE Dimension = 'wg'
                      AND ValueKey = '{DiscussionRendererSchema.UnknownValueKey}'
                    """));
            Assert.Equal(
                1,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    """
                    SELECT COUNT(*)
                    FROM ticket_facets
                    WHERE TicketKey = 'FHIR-1001'
                      AND Dimension = 'impact'
                    """));
            Assert.Equal(
                2,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    """
                    SELECT COUNT(*)
                    FROM ticket_facets
                    WHERE TicketKey = 'FHIR-1001'
                      AND Dimension = 'artifact'
                      AND DisplayValue = '(unknown)'
                    """));
            Assert.Equal(
                $"{DiscussionRendererSchema.NamedValueKeyPrefix}__UNKNOWN__",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT ValueKey
                    FROM ticket_facets
                    WHERE TicketKey = 'FHIR-1001'
                      AND Dimension = 'artifact'
                      AND LOWER(DisplayValue) = '__unknown__'
                    """));
            Assert.Equal(
                "__UNKNOWN__",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT DisplayValue
                    FROM ticket_facets
                    WHERE TicketKey = 'FHIR-1001'
                      AND Dimension = 'artifact'
                      AND LOWER(DisplayValue) = '__unknown__'
                    """));
            Assert.Equal(
                1,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    """
                    SELECT COUNT(*)
                    FROM ticket_facets
                    WHERE TicketKey = 'FHIR-1001'
                      AND Dimension = 'artifact'
                      AND LOWER(DisplayValue) = '__unknown__'
                      AND IsUnknown = 0
                    """));
            Assert.Equal(
                1,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    """
                    SELECT SUM(IsUnknown)
                    FROM ticket_facets
                    WHERE TicketKey = 'FHIR-1001'
                      AND Dimension = 'artifact'
                      AND DisplayValue = '(unknown)'
                    """));

            Assert.Equal(
                4,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    "SELECT COUNT(*) FROM summary_sources"));
            Assert.Equal(
                "https://jira.example/FHIR-2002",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT Url
                    FROM summary_sources
                    WHERE SummaryKind = 'linked-jira'
                    """));
            Assert.Equal(
                "https://jira.hl7.org/browse/BALLOT-77",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT Url
                    FROM summary_sources
                    WHERE SourceKey = 'BALLOT-77'
                    """));
            Assert.Equal(
                "FHIR \u203a Ticket discussion",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT Label
                    FROM summary_sources
                    WHERE SummaryKind = 'related-zulip'
                    """));
            Assert.Equal(
                0,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    """
                    SELECT COUNT(*)
                    FROM summary_sources
                    WHERE SummaryKind LIKE '%github%'
                    """));
            Assert.Null(
                await ScalarNullableStringAsync(
                    built.TempDbPath,
                    """
                    SELECT Url
                    FROM related_items
                    WHERE Kind = 'github'
                    """));

            Assert.Equal(
                ["FHIR-1001", "CDS-2001"],
                await ReadStringsAsync(
                    built.TempDbPath,
                    """
                    SELECT TicketKey
                    FROM topic_members
                    ORDER BY OrderInContainer
                    """));
        }

        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task RefreshProofQualifiesFrozenSourceEvidenceAndFingerprints()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: PreparedTicketSnapshotSchemaV3.Version);
        DateTimeOffset frozenRefresh =
            new(2026, 9, 14, 16, 45, 0, TimeSpan.Zero);
        await fixture.AttachValidPublicationRefreshProofAsync(
            frozenRefresh,
            sourceContentRevision: 4242);

        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        try
        {
            Assert.True(
                built.Presentation.Readiness.IsReady,
                string.Join(
                    ", ",
                    built.Presentation.Readiness.Reasons.Select(
                        reason => reason.Code)));
            Assert.Equal(
                DiscussionPublicationReadinessEvidence.PublicationRefresh,
                built.Presentation.Readiness.Evidence);
            Assert.Equal(
                4242,
                built.Presentation.Readiness.JiraSourceContentRevision);
            Assert.Equal(
                frozenRefresh,
                built.Presentation.JiraSourceLastSuccessfulRefreshAt);
            Assert.Empty(built.Presentation.Readiness.Reasons);
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath,
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }

        AuthoringSnapshotPublicationProof proof =
            Assert.IsType<AuthoringSnapshotPublicationProof>(
                fixture.Descriptor.PublicationProof);
        await fixture.SetPublicationProofAsync(
            proof with
            {
                GroupingFingerprint = new string('0', 64),
            });
        DiscussionSiteDatabaseBuilder.BuildResult degraded =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        try
        {
            Assert.False(degraded.Presentation.Readiness.IsReady);
            Assert.Equal("Tickets - Sept 5, 2026", degraded.Presentation.SiteName);
            Assert.Equal(DiscussionDateCoverage.Complete,
                degraded.Presentation.CorpusSummary.DateCoverage);
            Assert.Null(
                degraded.Presentation.JiraSourceLastSuccessfulRefreshAt);
            Assert.Contains(
                degraded.Presentation.Readiness.Reasons,
                reason => reason.Code ==
                    DiscussionPublicationReadinessReasonCodes
                        .InvalidRefreshProof);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(degraded.TempDbPath);
        }
    }

    [Fact]
    public async Task RealReconciliationSnapshot_IsPublicationReady()
    {
        DateTimeOffset capturedAt =
            new(2026, 9, 16, 18, 30, 0, TimeSpan.Zero);
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePromotedReconciliationAsync(
                _root,
                capturedAt,
                stableJiraGeneration: 5252);
        AuthoringSnapshotPublicationProof proof =
            Assert.IsType<AuthoringSnapshotPublicationProof>(
                fixture.Descriptor.PublicationProof);

        Assert.Equal(
            PreparedTicketPublicationContract.CurrentVersion,
            proof.ContractVersion);
        Assert.NotEqual(proof.SourceRunId, fixture.Descriptor.RunId);

        DiscussionSiteDatabaseBuilder.BuildResult ready =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        try
        {
            Assert.True(ready.Presentation.Readiness.IsReady);
            Assert.Equal(
                DiscussionPublicationReadinessEvidence
                    .PublicationReconciliation,
                ready.Presentation.Readiness.Evidence);
            Assert.Equal(
                5252,
                ready.Presentation.Readiness.JiraSourceContentRevision);
            Assert.Equal(
                capturedAt,
                ready.Presentation.JiraSourceLastSuccessfulRefreshAt);
            Assert.Empty(ready.Presentation.Readiness.Reasons);
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                ready.TempDbPath,
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(ready.TempDbPath);
        }
    }

    [Fact]
    public async Task RealCanonicalEpochRecoverySnapshot_IsPublicationReady()
    {
        DateTimeOffset capturedAt =
            new(2026, 9, 17, 18, 30, 0, TimeSpan.Zero);
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreateCanonicalEpochRecoveryAsync(
                _root,
                capturedAt,
                stableJiraGeneration: 5252);
        AuthoringSnapshotPublicationProof proof =
            Assert.IsType<AuthoringSnapshotPublicationProof>(
                fixture.Descriptor.PublicationProof);
        Assert.Equal(
            PreparedTicketPublicationContract
                .CanonicalEpochRecoveryPurpose,
            proof.Purpose);
        Assert.NotEqual(proof.SourceRunId, fixture.Descriptor.RunId);
        PreparedTicketPublicationFingerprints fingerprints =
            await PreparedTicketPublicationFingerprintReader
                .ReadCanonicalEpochRecoveryAsync(fixture.DatabasePath);
        Assert.Equal(proof.CorpusFingerprint, fingerprints.Corpus);
        Assert.Equal(proof.GroupingFingerprint, fingerprints.Grouping);

        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        try
        {
            Assert.True(
                built.Presentation.Readiness.IsReady,
                string.Join(
                    ", ",
                    built.Presentation.Readiness.Reasons.Select(
                        reason => reason.Code)));
            Assert.Equal(
                DiscussionPublicationReadinessEvidence
                    .CanonicalEpochRecovery,
                built.Presentation.Readiness.Evidence);
            Assert.Equal(
                fixture.Descriptor.AuthoringEpoch,
                built.Presentation.Readiness.JiraSourceContentRevision);
            Assert.Empty(built.Presentation.Readiness.Reasons);
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath,
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData("corpus")]
    [InlineData("grouping")]
    [InlineData("capture-time")]
    [InlineData("abandonment-time")]
    public async Task CanonicalEpochRecoveryProof_RequiresCompleteFingerprintsAndCoordinates(
        string coordinate)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreateCanonicalEpochRecoveryAsync(
                _root,
                new DateTimeOffset(
                    2026,
                    9,
                    17,
                    18,
                    30,
                    0,
                    TimeSpan.Zero),
                stableJiraGeneration: 5252);
        AuthoringSnapshotPublicationProof proof =
            Assert.IsType<AuthoringSnapshotPublicationProof>(
                fixture.Descriptor.PublicationProof);
        await fixture.SetPublicationProofAsync(
            coordinate switch
            {
                "corpus" => proof with { CorpusFingerprint = new string('0', 64) },
                "grouping" => proof with { GroupingFingerprint = new string('0', 64) },
                "capture-time" => proof with { CapturedAt = proof.CapturedAt.AddSeconds(1) },
                "abandonment-time" => proof with
                {
                    SourceLastSuccessfulRefreshAt = proof.SourceLastSuccessfulRefreshAt.AddSeconds(1),
                },
                _ => throw new ArgumentOutOfRangeException(nameof(coordinate)),
            });

        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        try
        {
            Assert.False(built.Presentation.Readiness.IsReady);
            Assert.Equal(
                DiscussionPublicationReadinessEvidence
                    .CanonicalEpochRecovery,
                built.Presentation.Readiness.Evidence);
            Assert.Contains(
                built.Presentation.Readiness.Reasons,
                reason => reason.Code ==
                    DiscussionPublicationReadinessReasonCodes
                        .InvalidCanonicalEpochRecoveryProof);
            Assert.DoesNotContain(
                built.Presentation.Readiness.Reasons,
                reason => reason.Code ==
                    DiscussionPublicationReadinessReasonCodes
                        .InvalidReconciliationProof);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData("topic")]
    [InlineData("linked-group")]
    [InlineData("membership-order")]
    [InlineData("receipt-coordinate")]
    public async Task CanonicalEpochRecoveryProof_RejectsChangedImmutableCorpusOrGrouping(
        string mutation)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreateCanonicalEpochRecoveryAsync(
                _root, new(2026, 9, 17, 18, 30, 0, TimeSpan.Zero), 5252);
        await ExecuteAsync(fixture.DatabasePath, mutation switch
        {
            "topic" => "UPDATE prepared_ticket_topics SET ShortDescription = 'Different topic'",
            "linked-group" => "UPDATE prepared_ticket_topic_groups SET Rationale = 'Different rationale'",
            "membership-order" => "UPDATE prepared_ticket_topic_members SET OrderInContainer = 1 - OrderInContainer",
            "receipt-coordinate" =>
                """
                UPDATE authoring_run_items SET ExpectedSourceRevision = 'different'
                WHERE ItemKind = 'fhir';
                UPDATE authoring_result_receipts
                SET ExpectedSourceRevision = 'different', ObservedSourceRevision = 'different';
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        });
        await fixture.RefreshDescriptorHashAsync();

        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath, fixture.Descriptor, "Tickets", ResolvedFilters.None);
        try
        {
            Assert.False(built.Presentation.Readiness.IsReady);
            Assert.Contains(built.Presentation.Readiness.Reasons,
                reason => reason.Code == DiscussionPublicationReadinessReasonCodes.InvalidCanonicalEpochRecoveryProof);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData(nameof(PreparedTicketPublicationCorpusItem.TicketKey))]
    [InlineData(nameof(PreparedTicketPublicationCorpusItem.ReceiptId))]
    [InlineData(nameof(PreparedTicketPublicationCorpusItem.RunItemId))]
    [InlineData(
        nameof(PreparedTicketPublicationCorpusItem.ContributingRunId))]
    [InlineData(nameof(PreparedTicketPublicationCorpusItem.ItemKind))]
    [InlineData(
        nameof(PreparedTicketPublicationCorpusItem.ExpectedSourceRevision))]
    public async Task RealReconciliationSnapshot_ChangingAnyCorpusCoordinateDegradesReadiness(
        string coordinate)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePromotedReconciliationAsync(
                _root,
                new DateTimeOffset(
                    2026,
                    9,
                    16,
                    18,
                    30,
                    0,
                    TimeSpan.Zero),
                stableJiraGeneration: 5252);
        await fixture.ChangePublicationCorpusCoordinateAsync(coordinate);

        DiscussionSiteDatabaseBuilder.BuildResult degraded =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        try
        {
            Assert.False(degraded.Presentation.Readiness.IsReady);
            Assert.Equal(
                DiscussionPublicationReadinessEvidence
                    .PublicationReconciliation,
                degraded.Presentation.Readiness.Evidence);
            Assert.Null(
                degraded.Presentation.Readiness.JiraSourceContentRevision);
            Assert.Null(
                degraded.Presentation.JiraSourceLastSuccessfulRefreshAt);
            Assert.Contains(
                degraded.Presentation.Readiness.Reasons,
                reason => reason.Code ==
                    DiscussionPublicationReadinessReasonCodes
                        .InvalidReconciliationProof);
            Assert.DoesNotContain(
                degraded.Presentation.Readiness.Reasons,
                reason => reason.Code ==
                    DiscussionPublicationReadinessReasonCodes
                        .InvalidRefreshProof);
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                degraded.TempDbPath,
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(degraded.TempDbPath);
        }
    }

    [Fact]
    public async Task RefreshProofRejectsUnreceiptedLiveTopicPartition()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                includeSecondTicket: true,
                schemaVersion: PreparedTicketSnapshotSchemaV3.Version,
                useMultipleRuns: true);
        await fixture.AttachValidPublicationRefreshProofAsync(
            new DateTimeOffset(2026, 9, 14, 16, 45, 0, TimeSpan.Zero),
            sourceContentRevision: 4242);
        await ExecuteAsync(
            fixture.DatabasePath,
            """
            INSERT INTO prepared_ticket_topics(
                RowId, Id, WorkGroupClean, WorkGroupDisplay, Specification,
                Type, ShortDescription, LongerDescription,
                RenderOrderHint, SavedAt)
            VALUES(
                9001, 'topic-without-receipt', 'PatientAdministration',
                'Patient Administration', 'FHIR', 'Change Request',
                'Unreceipted live topic', 'Must invalidate publication proof',
                1, '2026-09-14T16:45:00.0000000+00:00');
            INSERT INTO prepared_ticket_topic_members(
                RowId, Id, TopicRowId, TopicGroupRowId, TicketKey,
                OrderInContainer)
            VALUES(
                9001, 'unreceipted-member-1', 9001, NULL, 'FHIR-1001', 0);
            INSERT INTO prepared_ticket_topic_members(
                RowId, Id, TopicRowId, TopicGroupRowId, TicketKey,
                OrderInContainer)
            VALUES(
                9002, 'unreceipted-member-2', 9001, NULL, 'CDS-2001', 1);
            """);
        await fixture.RefreshDescriptorHashAsync();

        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        try
        {
            Assert.Equal(
                1,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    """
                    SELECT COUNT(*)
                    FROM topics
                    WHERE Id = 'topic-without-receipt'
                    """));
            Assert.False(built.Presentation.Readiness.IsReady);
            Assert.Null(
                built.Presentation.JiraSourceLastSuccessfulRefreshAt);
            Assert.Contains(
                built.Presentation.Readiness.Reasons,
                reason => reason.Code ==
                    DiscussionPublicationReadinessReasonCodes
                        .InvalidRefreshProof);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task V3ProjectionProjectsOnlyCurrentPolicySafePeople()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                includeSecondTicket: true,
                schemaVersion: PreparedTicketSnapshotSchemaV3.Version,
                useMultipleRuns: true,
                includeRendererEvidence: true,
                includeUntrustedPeople: true);

        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets for Discussion",
                ResolvedFilters.None);
        try
        {
            DiscussionSiteDatabaseValidator.ValidationResult validation =
                await DiscussionSiteDatabaseValidator.ValidateAsync(
                    built.TempDbPath,
                    fixture.DatabasePath,
                    fixture.Descriptor.SchemaVersion,
                    "Tickets for Discussion",
                    ResolvedFilters.None);

            Assert.Equal(
                new DateTimeOffset(
                    2026,
                    9,
                    10,
                    23,
                    30,
                    0,
                    TimeSpan.Zero),
                validation.Presentation.JiraSourceLastSuccessfulRefreshAt);
            Assert.Null(
                await ScalarNullableStringAsync(
                    built.TempDbPath,
                    """
                    SELECT DisplayName
                    FROM ticket_people
                    WHERE TicketKey = 'FHIR-1001'
                      AND Role = 'reporter'
                    """));
            Assert.Equal(
                DiscussionRendererSchema.PersonAvailable,
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT Availability
                    FROM ticket_people
                    WHERE TicketKey = 'FHIR-1001'
                      AND Role = 'reporter'
                    """));
            Assert.Equal(
                "Grace Hopper",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT DisplayName
                    FROM ticket_people
                    WHERE TicketKey = 'FHIR-1001'
                      AND Role = 'assignee'
                    """));
            Assert.Equal(
                "Katherine Johnson",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT DisplayName
                    FROM ticket_people
                    WHERE TicketKey = 'CDS-2001'
                      AND Role = 'reporter'
                    """));
            Assert.Null(
                await ScalarNullableStringAsync(
                    built.TempDbPath,
                    """
                    SELECT DisplayName
                    FROM ticket_people
                    WHERE TicketKey = 'CDS-2001'
                      AND Role = 'assignee'
                    """));
            Assert.Equal(
                ["Alan Turing", "Lin Example", "Trimmed Safe"],
                await ReadStringsAsync(
                    built.TempDbPath,
                    """
                    SELECT DisplayName
                    FROM ticket_people
                    WHERE TicketKey = 'FHIR-1001'
                      AND Role = 'in-person-requester'
                    ORDER BY OrderInRole
                    """));
            Assert.Equal(
                0,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    """
                    SELECT COUNT(*)
                    FROM ticket_people
                    WHERE DisplayName LIKE '%@%'
                       OR DisplayName IN (
                           'Missing Policy',
                           'Old Policy',
                           'Unknown Policy',
                           'Future Policy',
                           'Related Stale'
                       )
                    """));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public async Task V3ProjectionRequiresCurrentParentPeoplePolicy(
        int? policyVersion)
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: PreparedTicketSnapshotSchemaV3.Version);
        string sqlPolicyVersion = policyVersion?.ToString(
            CultureInfo.InvariantCulture) ?? "NULL";
        await ExecuteAsync(
            fixture.DatabasePath,
            $"""
            UPDATE prepared_ticket_hydration
            SET PublicDisplayNamePolicyVersion = {sqlPolicyVersion}
            WHERE TicketKey = 'FHIR-1001';
            """);

        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        try
        {
            Assert.Equal(
                0,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    """
                    SELECT COUNT(*)
                    FROM ticket_people
                    WHERE TicketKey = 'FHIR-1001'
                      AND Role IN ('reporter', 'assignee')
                      AND DisplayName IS NOT NULL
                    """));
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task SummarySourcesUseJiraFallbackAndNeverFabricateZulipUrl()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: PreparedTicketSnapshotSchemaV2.Version);
        await ExecuteAsync(
            fixture.DatabasePath,
            """
            UPDATE prepared_tickets
            SET LinkedTicketSummary = 'FHIR-4040',
                RelatedZulipSummary = 'Unresolved discussion'
            WHERE Key = 'FHIR-1001';
            INSERT INTO prepared_ticket_related_jira(
                RowId, Id, TicketKey, AssociatedTicketKey, LinkType,
                Justification)
            VALUES(
                101, 'fallback-jira', 'FHIR-1001', 'FHIR-4040',
                'linked', 'fallback');
            INSERT INTO prepared_ticket_related_zulip(
                RowId, Id, TicketKey, ZulipThreadId, Justification)
            VALUES
                (101, 'unresolved-zulip', 'FHIR-1001',
                 'thread-missing', 'unresolved'),
                (102, 'stream-only-zulip', 'FHIR-1001',
                 'thread-stream-only', 'stream only'),
                (103, 'topic-only-zulip', 'FHIR-1001',
                 'thread-topic-only', 'topic only');
            INSERT INTO prepared_zulip_hydration(
                RowId, Id, TicketKey, ZulipThreadId, StreamName, Topic)
            VALUES
                (101, 'stream-only-hydration', 'FHIR-1001',
                 'thread-stream-only', 'FHIR', NULL),
                (102, 'topic-only-hydration', 'FHIR-1001',
                 'thread-topic-only', NULL, 'Ticket discussion');
            """);

        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        try
        {
            Assert.Equal(
                "https://jira.hl7.org/browse/FHIR-4040",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT Url
                    FROM summary_sources
                    WHERE SummaryKind = 'linked-jira'
                    """));
            Assert.Equal(
                "thread-missing",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT Label
                    FROM summary_sources
                    WHERE SummaryKind = 'related-zulip'
                      AND SourceKey = 'thread-missing'
                    """));
            Assert.Null(
                await ScalarNullableStringAsync(
                    built.TempDbPath,
                    """
                    SELECT Url
                    FROM summary_sources
                    WHERE SummaryKind = 'related-zulip'
                      AND SourceKey = 'thread-missing'
                    """));
            Assert.Equal(
                "thread-stream-only",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT Label
                    FROM summary_sources
                    WHERE SummaryKind = 'related-zulip'
                      AND SourceKey = 'thread-stream-only'
                    """));
            Assert.Equal(
                "thread-topic-only",
                await ScalarAsync<string>(
                    built.TempDbPath,
                    """
                    SELECT Label
                    FROM summary_sources
                    WHERE SummaryKind = 'related-zulip'
                      AND SourceKey = 'thread-topic-only'
                    """));
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath,
                fixture.DatabasePath,
                fixture.Descriptor.SchemaVersion,
                "Tickets",
                ResolvedFilters.None);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task V2ProjectionKeepsTicketDateWhenProvenanceCoordinateIsNull()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: PreparedTicketSnapshotSchemaV2.Version,
                includeNullProvenance: true);

        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets for Discussion",
                ResolvedFilters.None);
        try
        {
            Assert.Null(
                built.Presentation.JiraSourceLastSuccessfulRefreshAt);
            Assert.Equal(
                "Tickets for Discussion - Sept 5, 2026",
                built.Presentation.SiteName);
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath,
                fixture.DatabasePath,
                fixture.Descriptor.SchemaVersion,
                "Tickets for Discussion",
                ResolvedFilters.None);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task FiltersRecomputeFreshnessAndRemoveInvalidTopics()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                includeSecondTicket: true,
                schemaVersion: PreparedTicketSnapshotSchemaV2.Version,
                useMultipleRuns: true,
                includeRendererEvidence: true);
        ResolvedFilters filters = new(
            "FHIR",
            "FHIR",
            "FHIR Infrastructure");

        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets for Discussion",
                filters);
        try
        {
            Assert.Equal(1, built.SurvivingTicketCount);
            Assert.Equal(
                new DateTimeOffset(
                    2026,
                    9,
                    8,
                    5,
                    0,
                    0,
                    TimeSpan.Zero),
                built.Presentation.JiraSourceLastSuccessfulRefreshAt);
            Assert.Equal(
                "Tickets for Discussion - Sept 5, 2026 " +
                "(filtered: spec=FHIR, project=FHIR, wg=FHIR Infrastructure)",
                built.Presentation.SiteName);
            Assert.Equal(
                0,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    "SELECT COUNT(*) FROM topics"));
            Assert.Equal(
                0,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    "SELECT COUNT(*) FROM topic_groups"));
            Assert.Equal(
                0,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    "SELECT COUNT(*) FROM topic_members"));
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath,
                fixture.DatabasePath,
                fixture.Descriptor.SchemaVersion,
                "Tickets for Discussion",
                filters);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task FilteringFlattensInvalidGroupsAndReindexesMembers()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                includeSecondTicket: true,
                schemaVersion: PreparedTicketSnapshotSchemaV2.Version,
                useMultipleRuns: true,
                includeRendererEvidence: true);
        await ExecuteAsync(
            fixture.DatabasePath,
            """
            INSERT INTO authoring_run_items(
                Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision,
                Status, AcceptedReceiptId, AttemptCount, CreatedAt,
                StartedAt, CompletedAt)
            SELECT
                'item-filter-order', RunId, 'FHIR-1002', ItemKind,
                'rev-filter-order', Status, 'receipt-filter-order',
                AttemptCount, CreatedAt, StartedAt, CompletedAt
            FROM authoring_run_items
            WHERE Id = 'item-1';
            INSERT INTO authoring_result_receipts(
                Id, OperationId, RunId, RunItemId, BusinessKey,
                ContentHash, ExpectedSourceRevision,
                ObservedSourceRevision, AuthoringEpoch, PersistedAt)
            SELECT
                'receipt-filter-order', 'operation-filter-order', RunId,
                'item-filter-order', 'FHIR-1002', ContentHash,
                'rev-filter-order', 'rev-filter-order', AuthoringEpoch,
                PersistedAt
            FROM authoring_result_receipts
            WHERE Id = 'receipt-1';
            INSERT INTO prepared_tickets(
                RowId, Id, Key, RequestSummary, SavedAt)
            SELECT
                101, 'prepared-filter-order', 'FHIR-1002',
                'Filtered order request', SavedAt
            FROM prepared_tickets
            WHERE Key = 'FHIR-1001';
            INSERT INTO prepared_ticket_hydration(
                RowId, Id, TicketKey, Specification, Reporter, Assignee,
                SourceProject, SourceLastSuccessfulRefreshAt,
                SourceContentRevision, HydratedAt, HydrationStatus)
            SELECT
                101, 'hydration-filter-order', 'FHIR-1002',
                Specification, Reporter, Assignee, SourceProject,
                SourceLastSuccessfulRefreshAt, SourceContentRevision,
                HydratedAt, HydrationStatus
            FROM prepared_ticket_hydration
            WHERE TicketKey = 'FHIR-1001';
            INSERT INTO prepared_jira_hydration(
                RowId, Id, TicketKey, JiraKey, Title, Status, Type,
                WorkGroup, WorkGroupClean, Specification, HydratedAt,
                HydrationStatus)
            SELECT
                101, 'jira-filter-order', 'FHIR-1002', 'FHIR-1002',
                'Filtered order title', Status, Type, WorkGroup,
                WorkGroupClean, Specification, HydratedAt,
                HydrationStatus
            FROM prepared_jira_hydration
            WHERE TicketKey = 'FHIR-1001'
              AND JiraKey = 'FHIR-1001';
            UPDATE prepared_ticket_topic_members
            SET OrderInContainer = CASE TicketKey
                WHEN 'FHIR-1001' THEN 7
                ELSE 11
            END
            WHERE TopicGroupRowId IS NOT NULL;
            INSERT INTO prepared_ticket_topic_members(
                RowId, Id, TopicRowId, TopicGroupRowId, TicketKey,
                OrderInContainer)
            SELECT
                101, 'member-filter-order', RowId, NULL, 'FHIR-1002', 19
            FROM prepared_ticket_topics
            WHERE Id = 'topic-renderer';
            """);
        ResolvedFilters filters = new(null, "FHIR", null);

        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                filters);
        try
        {
            Assert.Equal(
                ["FHIR-1001:0", "FHIR-1002:1"],
                await ReadStringsAsync(
                    built.TempDbPath,
                    """
                    SELECT TicketKey || ':' || OrderInContainer
                    FROM topic_members
                    WHERE TopicGroupRowId IS NULL
                    ORDER BY OrderInContainer
                    """));
            Assert.Equal(2, built.Presentation.CorpusSummary.TicketCount);
            Assert.Equal(1, built.Presentation.CorpusSummary.ExportedProjectCount);
            Assert.Equal(
                0,
                await ScalarAsync<long>(
                    built.TempDbPath,
                    "SELECT COUNT(*) FROM topic_groups"));
            await DiscussionSiteDatabaseValidator.ValidateAsync(
                built.TempDbPath,
                fixture.DatabasePath,
                fixture.Descriptor.SchemaVersion,
                "Tickets",
                filters);

            await ExecuteAsync(
                built.TempDbPath,
                """
                UPDATE topic_members
                SET OrderInContainer = OrderInContainer + 2
                WHERE TopicGroupRowId IS NULL;
                """);
            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => DiscussionSiteDatabaseValidator.ValidateAsync(
                        built.TempDbPath));
            Assert.Contains(
                "ungrouped member order",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task ValidatorRequiresEveryTicketToMatchMetadataFilters()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: PreparedTicketSnapshotSchemaV2.Version);
        ResolvedFilters filters = new("FHIR", null, null);
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                filters);
        try
        {
            await ExecuteAsync(
                built.TempDbPath,
                """
                UPDATE facet_dimensions
                SET Route = 'not-the-catalog-route'
                WHERE Dimension = 'impact';
                """);
            InvalidOperationException catalogException =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => DiscussionSiteDatabaseValidator.ValidateAsync(
                        built.TempDbPath));
            Assert.Contains(
                "facet dimension",
                catalogException.Message,
                StringComparison.OrdinalIgnoreCase);
            await ExecuteAsync(
                built.TempDbPath,
                """
                UPDATE facet_dimensions
                SET Route = 'by-impact'
                WHERE Dimension = 'impact';
                """);

            await ExecuteAsync(
                built.TempDbPath,
                $"""
                UPDATE tickets
                SET Specification = 'Other';
                UPDATE ticket_facets
                SET ValueKey = '{DiscussionRendererSchema.NamedValueKeyPrefix}Other',
                    DisplayValue = 'Other',
                    SortKey = 'OTHER'
                WHERE Dimension = 'spec';
                """);

            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => DiscussionSiteDatabaseValidator.ValidateAsync(
                        built.TempDbPath));
            Assert.Contains(
                "filter",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task ValidatorRequiresExactCompleteSummarySourceProjection()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                includeSecondTicket: true,
                schemaVersion: PreparedTicketSnapshotSchemaV2.Version,
                includeRendererEvidence: true);
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        try
        {
            await ExecuteAsync(
                built.TempDbPath,
                """
                UPDATE summary_sources
                SET Label = 'Fabricated label',
                    Url = 'https://example.com/fabricated',
                    SortKey = 'FABRICATED LABEL'
                WHERE SummaryKind = 'linked-jira';
                """);
            InvalidOperationException rewriteException =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => DiscussionSiteDatabaseValidator.ValidateAsync(
                        built.TempDbPath));
            Assert.Contains(
                "complete and exact",
                rewriteException.Message,
                StringComparison.OrdinalIgnoreCase);

            await ExecuteAsync(
                built.TempDbPath,
                """
                UPDATE summary_sources
                SET Label = 'FHIR-2002',
                    Url = 'https://jira.example/FHIR-2002',
                    SortKey = 'FHIR-2002'
                WHERE SummaryKind = 'linked-jira';
                DELETE FROM summary_sources
                WHERE SummaryKind = 'related-zulip';
                """);
            InvalidOperationException omissionException =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => DiscussionSiteDatabaseValidator.ValidateAsync(
                        built.TempDbPath));
            Assert.Contains(
                "complete and exact",
                omissionException.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task ValidatorRequiresContiguousGroupedMemberOrder()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                includeSecondTicket: true,
                schemaVersion: PreparedTicketSnapshotSchemaV2.Version,
                includeRendererEvidence: true);
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        try
        {
            await ExecuteAsync(
                built.TempDbPath,
                """
                UPDATE topic_members
                SET OrderInContainer = OrderInContainer + 3
                WHERE TopicGroupRowId IS NOT NULL;
                """);
            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => DiscussionSiteDatabaseValidator.ValidateAsync(
                        built.TempDbPath));
            Assert.Contains(
                "member order",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task ValidatorPreservesEnumLiteralsDuringDdlComparison()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: PreparedTicketSnapshotSchemaV2.Version);
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        try
        {
            DiscussionRendererTable summarySources =
                DiscussionRendererSchema.Tables.Single(
                    table => table.Name == "summary_sources");
            string mutatedDdl = summarySources.CreateSql.Replace(
                "'linked-jira'",
                "'Linked-jira'",
                StringComparison.Ordinal);
            await ExecuteAsync(
                built.TempDbPath,
                $"""
                ALTER TABLE summary_sources RENAME TO old_summary_sources;
                {mutatedDdl};
                DROP TABLE old_summary_sources;
                """);

            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => DiscussionSiteDatabaseValidator.ValidateAsync(
                        built.TempDbPath));
            Assert.Contains(
                "summary_sources",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task ProjectionRejectsMultipleAcceptedCoordinates()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: PreparedTicketSnapshotSchemaV2.Version);
        await ExecuteAsync(
            fixture.DatabasePath,
            """
            INSERT INTO authoring_run_items(
                Id, RunId, BusinessKey, ItemKind, ExpectedSourceRevision,
                Status, AcceptedReceiptId, AttemptCount, CreatedAt,
                StartedAt, CompletedAt)
            SELECT
                'item-duplicate', RunId, BusinessKey, ItemKind,
                ExpectedSourceRevision, Status, 'receipt-duplicate',
                AttemptCount, CreatedAt, StartedAt, CompletedAt
            FROM authoring_run_items
            WHERE Id = 'item-1';
            INSERT INTO authoring_result_receipts(
                Id, OperationId, RunId, RunItemId, BusinessKey,
                ContentHash, ExpectedSourceRevision,
                ObservedSourceRevision, AuthoringEpoch, PersistedAt)
            SELECT
                'receipt-duplicate', 'operation-duplicate', RunId,
                'item-duplicate', BusinessKey, ContentHash,
                ExpectedSourceRevision, ObservedSourceRevision,
                AuthoringEpoch, PersistedAt
            FROM authoring_result_receipts
            WHERE Id = 'receipt-1';
            """);

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => DiscussionSiteDatabaseBuilder.BuildAsync(
                    fixture.DatabasePath,
                    fixture.Descriptor,
                    "Tickets",
                    ResolvedFilters.None));

        Assert.Contains(
            "exactly one accepted",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidatorRejectsUnsafePeopleWithMatchingSortKey()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: PreparedTicketSnapshotSchemaV3.Version);
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        try
        {
            await ExecuteAsync(
                built.TempDbPath,
                """
                UPDATE ticket_people
                SET DisplayName = 'Name <person@example.org>',
                    SortKey = 'NAME <PERSON@EXAMPLE.ORG>'
                WHERE TicketKey = 'FHIR-1001'
                  AND Role = 'reporter';
                """);

            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => DiscussionSiteDatabaseValidator.ValidateAsync(
                        built.TempDbPath));

            Assert.Contains(
                "unsafe",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "sort key",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    [Fact]
    public async Task ValidatorRejectsSchemaAndProjectionTampering()
    {
        TicketSnapshotFixture fixture =
            await TicketSnapshotFixture.CreatePreparerAsync(
                _root,
                schemaVersion: PreparedTicketSnapshotSchemaV2.Version);
        DiscussionSiteDatabaseBuilder.BuildResult built =
            await DiscussionSiteDatabaseBuilder.BuildAsync(
                fixture.DatabasePath,
                fixture.Descriptor,
                "Tickets",
                ResolvedFilters.None);
        try
        {
            await ExecuteAsync(
                built.TempDbPath,
                """
                UPDATE ticket_facets
                SET DisplayValue = 'tampered'
                WHERE Dimension = 'project';
                """);

            InvalidOperationException exception =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => DiscussionSiteDatabaseValidator.ValidateAsync(
                        built.TempDbPath));
            Assert.Contains(
                "facet",
                exception.Message,
                StringComparison.OrdinalIgnoreCase);

            await ExecuteAsync(
                built.TempDbPath,
                "CREATE VIEW forbidden_view AS SELECT Key FROM tickets");
            InvalidOperationException schemaException =
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => DiscussionSiteDatabaseValidator.ValidateAsync(
                        built.TempDbPath));
            Assert.Contains(
                "schema object",
                schemaException.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestFileCleanup.SafeDeleteFile(built.TempDbPath);
        }
    }

    private static async Task<IReadOnlyList<string>>
        ReadSchemaTableNamesAsync(string databasePath)
    {
        List<string> values = [];
        await using SqliteConnection connection = OpenReadOnly(databasePath);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }
        return values;
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        string databasePath,
        string sql)
    {
        List<string> values = [];
        await using SqliteConnection connection = OpenReadOnly(databasePath);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }
        return values;
    }

    private static async Task<T> ScalarAsync<T>(
        string databasePath,
        string sql)
    {
        await using SqliteConnection connection = OpenReadOnly(databasePath);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(
            value!,
            typeof(T),
            CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarNullableStringAsync(
        string databasePath,
        string sql)
    {
        await using SqliteConnection connection = OpenReadOnly(databasePath);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync();
        return value is null or DBNull
            ? null
            : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static Task RewriteCorpusSummaryAsync(
        DiscussionSiteDatabaseBuilder.BuildResult built,
        DiscussionCorpusSummary summary)
    {
        TicketSitePresentation presentation = TicketSitePresentation.CreateDiscussion(
            built.Presentation.BaseTitle,
            built.Presentation.JiraSourceLastSuccessfulRefreshAt,
            built.Presentation.Filters,
            summary,
            built.Presentation.Readiness);
        return ExecuteAsync(
            built.TempDbPath,
            "UPDATE site_metadata SET CorpusSummaryJson = @json, SiteName = @name",
            ("@json", TicketSitePresentationJson.Serialize(summary)),
            ("@name", presentation.SiteName));
    }

    private static async Task ExecuteAsync(
        string databasePath,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using SqliteConnection connection = new(
            $"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream))
            .ToLowerInvariant();
    }

    private static SqliteConnection OpenReadOnly(string databasePath)
        => new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
}
