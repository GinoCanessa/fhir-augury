using System.Globalization;
using System.Security.Cryptography;
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

    [Fact]
    public void PresentationUsesUtcDateAndInvariantEnglish()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            CultureInfo.CurrentUICulture = new CultureInfo("fr-FR");

            TicketSitePresentation presentation =
                TicketSitePresentation.CreateDiscussion(
                    "Tickets",
                    new DateTimeOffset(
                        2026,
                        9,
                        8,
                        1,
                        0,
                        0,
                        TimeSpan.FromHours(2)),
                    new ResolvedFilters("FHIR", null, null));

            Assert.Equal(
                new DateTimeOffset(
                    2026,
                    9,
                    7,
                    23,
                    0,
                    0,
                    TimeSpan.Zero),
                presentation.JiraSourceLastSuccessfulRefreshAt);
            Assert.Equal(
                "Tickets - Built September 07, 2026 (filtered: spec=FHIR)",
                presentation.SiteName);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
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
                "Tickets for Discussion",
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
                "Tickets for Discussion - Built September 10, 2026",
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
            Assert.True(built.Presentation.Readiness.IsReady);
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
    public async Task V2ProjectionSuppressesFreshnessWhenAnyCoordinateIsNull()
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
                "Tickets for Discussion",
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
                "Tickets for Discussion - Built September 08, 2026 " +
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

    private static async Task ExecuteAsync(
        string databasePath,
        string sql)
    {
        await using SqliteConnection connection = new(
            $"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
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
