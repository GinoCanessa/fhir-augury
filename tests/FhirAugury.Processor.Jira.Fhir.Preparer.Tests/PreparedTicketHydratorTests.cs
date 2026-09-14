using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FhirAugury.Common.Text;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Preparer.Hydration;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Preparer.Persistence.Models;
using FhirAugury.Processor.Jira.Fhir.Preparer.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Preparer.Tests;

public sealed class PreparedTicketHydratorTests
{
    [Fact]
    public async Task FetchPublicationMetadata_ReadsOnlyStableTrustedJiraFields()
    {
        FakeHandler handler = new();
        DateTimeOffset hydratedAt =
            new(2026, 9, 14, 14, 0, 0, TimeSpan.Zero);
        DateTimeOffset updatedAt =
            new(2026, 9, 13, 9, 30, 0, TimeSpan.FromHours(-5));
        DateTimeOffset refreshAt =
            new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
        handler.AddJsonResponse(
            "/api/v1/jira/items/FHIR-901",
            JsonMetadata(
                new Dictionary<string, string>
                {
                    ["status"] = "Triaged",
                    ["type"] = "Change Request",
                    ["work_group"] = "FHIR-I",
                    ["specification"] = "FHIR",
                    ["description_plain"] = "must not escape",
                },
                title: "Publication ticket",
                url: "https://jira/browse/FHIR-901",
                people: new
                {
                    reporter = "  Ada Example ",
                    assignee = "private@example.org",
                    inPersonRequesters = new[]
                    {
                        " Zoë Example ",
                        "zoë example",
                        "unsafe@example.org",
                    },
                    publicDisplayNamePolicyVersion =
                        PublicDisplayNamePolicy.CurrentVersion,
                },
                provenance: new
                {
                    source = "jira",
                    contentRevision = 91,
                    isStable = true,
                    projectLastSuccessfulRefreshAt =
                        new Dictionary<string, DateTimeOffset?>
                        {
                            ["FHIR"] = refreshAt,
                        },
                },
                updatedAt: updatedAt,
                id: "FHIR-901"));
        OrchestratorHydrationFetcher fetcher = new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost/"),
            },
            NullLogger.Instance);

        PublicationMetadataFetchResult result =
            await fetcher.FetchPublicationMetadataAsync(
                "FHIR-901",
                hydratedAt,
                CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Failure);
        Assert.Equal(
            "2026-09-13T09:30:00.0000000-05:00",
            result.ObservedSourceRevision);
        Assert.Equal("Ada Example", result.Reporter);
        Assert.Null(result.Assignee);
        Assert.Equal(["Zoë Example"], result.InPersonRequesters);
        Assert.Equal("FHIR", result.SourceProject);
        Assert.Equal(refreshAt, result.SourceLastSuccessfulRefreshAt);
        Assert.Equal(91, result.SourceContentRevision);
        Assert.True(result.SourceIsStable);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            result.PublicDisplayNamePolicyVersion);
        Assert.Equal(
            ["/api/v1/jira/items/FHIR-901"],
            handler.RequestedPathsAndQueries);
    }

    [Fact]
    public async Task FetchPublicationMetadata_RejectsUnstableOrUntrustedResponses()
    {
        FakeHandler handler = new();
        handler.AddJsonResponse(
            "/api/v1/jira/items/FHIR-902",
            JsonMetadata(
                [],
                title: "Unstable",
                url: "https://jira/browse/FHIR-902",
                people: PeoplePayload(
                    "present",
                    PublicDisplayNamePolicy.CurrentVersion),
                provenance: new
                {
                    source = "jira",
                    contentRevision = 92,
                    isStable = false,
                    projectLastSuccessfulRefreshAt =
                        new Dictionary<string, DateTimeOffset?>
                        {
                            ["FHIR"] = DateTimeOffset.UtcNow,
                        },
                },
                id: "FHIR-902"));
        handler.AddJsonResponse(
            "/api/v1/jira/items/FHIR-903",
            JsonMetadata(
                [],
                title: "Untrusted",
                url: "https://jira/browse/FHIR-903",
                people: PeoplePayload("missing", null),
                provenance: new
                {
                    source = "jira",
                    contentRevision = 92,
                    isStable = true,
                    projectLastSuccessfulRefreshAt =
                        new Dictionary<string, DateTimeOffset?>
                        {
                            ["FHIR"] = DateTimeOffset.UtcNow,
                        },
                },
                id: "FHIR-903"));
        handler.AddStatusResponse(
            "/api/v1/jira/items/FHIR-904",
            HttpStatusCode.NotFound);
        OrchestratorHydrationFetcher fetcher = new(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost/"),
            },
            NullLogger.Instance);

        PublicationMetadataFetchResult unstable =
            await fetcher.FetchPublicationMetadataAsync(
                "FHIR-902",
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        PublicationMetadataFetchResult untrusted =
            await fetcher.FetchPublicationMetadataAsync(
                "FHIR-903",
                DateTimeOffset.UtcNow,
                CancellationToken.None);
        PublicationMetadataFetchResult missing =
            await fetcher.FetchPublicationMetadataAsync(
                "FHIR-904",
                DateTimeOffset.UtcNow,
                CancellationToken.None);

        Assert.False(unstable.IsSuccess);
        Assert.Equal(
            PublicationMetadataFetchFailureReason.UnstableSource,
            unstable.Failure!.Reason);
        Assert.False(untrusted.IsSuccess);
        Assert.Equal(
            PublicationMetadataFetchFailureReason.PeoplePolicyNotCurrent,
            untrusted.Failure!.Reason);
        Assert.False(missing.IsSuccess);
        Assert.Equal(
            PublicationMetadataFetchFailureReason.TicketNotFound,
            missing.Failure!.Reason);
    }

    [Fact]
    public async Task HydrateWithResult_HappyPath_ReturnsPersistedBatch()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-90");

        FakeHandler handler = new();
        handler.AddJsonResponse(
            "/api/v1/jira/items/FHIR-90",
            JsonMetadata(
                new Dictionary<string, string>
                {
                    ["status"] = "Triaged",
                    ["type"] = "Change Request",
                    ["work_group"] = "FHIR-I",
                    ["specification"] = "FHIR",
                },
                title: "ticket",
                url: "https://jira/browse/FHIR-90"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        HydrationAttemptResult result = await hydrator.HydrateWithResultAsync("FHIR-90", CancellationToken.None);

        HydrationAttemptSuccess success = Assert.IsType<HydrationAttemptSuccess>(result);
        Assert.Equal("FHIR-90", success.Batch.TicketKey);
        Assert.Equal("resolved", success.Batch.Parent.HydrationStatus);
        Assert.Single(success.Batch.JiraRows);

        PreparedTicketHydrationReadModel? persisted = await database.Database.GetHydrationAsync("FHIR-90");
        Assert.NotNull(persisted);
        Assert.Equal(success.Batch.Parent.HydrationStatus, persisted!.Parent!.HydrationStatus);
        Assert.Equal(success.Batch.JiraRows[0].Title, Assert.Single(persisted.JiraRows).Title);
    }

    [Fact]
    public async Task HydrateWithResult_UnexpectedFailure_ReturnsTypedFailure()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-91");

        FakeHandler handler = new();
        handler.AddExceptionResponse("/api/v1/jira/items/FHIR-91", new InvalidOperationException("script failed"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        HydrationAttemptResult result = await hydrator.HydrateWithResultAsync("FHIR-91", CancellationToken.None);

        HydrationAttemptFailure failure = Assert.IsType<HydrationAttemptFailure>(result);
        Assert.Equal("FHIR-91", failure.TicketKey);
        Assert.Equal(HydrationAttemptResult.UnexpectedErrorCode, failure.FailureCode);
        Assert.Equal(nameof(InvalidOperationException), failure.ExceptionType);
        Assert.Equal("script failed", failure.Reason);
        Assert.Null(await database.Database.GetHydrationAsync("FHIR-91"));
    }

    [Fact]
    public async Task Hydrate_Transient503_RetriesThenResolves()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-92");

        FakeHandler handler = new();
        handler.AddStatusResponse("/api/v1/jira/items/FHIR-92", HttpStatusCode.ServiceUnavailable, TimeSpan.Zero);
        handler.AddJsonResponse(
            "/api/v1/jira/items/FHIR-92",
            JsonMetadata(
                new Dictionary<string, string>
                {
                    ["status"] = "Triaged",
                    ["type"] = "Change Request",
                    ["work_group"] = "FHIR-I",
                    ["specification"] = "FHIR",
                },
                title: "retried",
                url: "https://jira/browse/FHIR-92"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-92", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-92");
        Assert.NotNull(read);
        Assert.Equal("resolved", read!.Parent!.HydrationStatus);
        Assert.Equal("resolved", Assert.Single(read.JiraRows).HydrationStatus);
        Assert.True(handler.RequestedPaths.Count(path => path == "/api/v1/jira/items/FHIR-92") >= 3);
    }

    [Fact]
    public async Task Hydrate_HappyPath_WritesResolvedRowsForEverySource()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-100", jiraKey: "FHIR-200", zulipThread: "fhir/infrastructure-wg:ballot", githubItem: "HL7/fhir#42", repo: "HL7/fhir");

        FakeHandler handler = new();
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-100",
            JsonMetadata(new Dictionary<string, string>
            {
                ["priority"] = "Major",
                ["resolution"] = "Persuasive",
                ["specification"] = "FHIR",
                ["comment_count"] = "5",
                ["description_plain"] = "body",
                ["resolution_description"] = "<p>resolution</p>",
                ["reporter"] = "legacy-reporter",
                ["related_artifacts"] = "Patient, Observation",
                ["related_pages"] = "patient.html; observation.html",
            },
            title: "parent",
            url: "https://jira/browse/FHIR-100",
            content: "<p>body</p>",
            people: new
            {
                reporter = "Ada",
                assignee = "Grace",
                inPersonRequesters = new[] { "Ada", "Lin" },
                publicDisplayNamePolicyVersion =
                    PublicDisplayNamePolicy.CurrentVersion,
            }));
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-200",
            JsonMetadata(new Dictionary<string, string>
            {
                ["status"] = "Open",
                ["type"] = "Change Request",
                ["priority"] = "Major",
                ["work_group"] = "FHIR-I",
            },
            title: "related jira",
            url: "https://jira/browse/FHIR-200",
            people: new
            {
                reporter = "Related Reporter",
                assignee = "Related Assignee",
                inPersonRequesters = Array.Empty<string>(),
                publicDisplayNamePolicyVersion =
                    PublicDisplayNamePolicy.CurrentVersion,
            }));
        handler.AddJsonResponse("/api/v1/zulip/threads",
            """{"streamId":42,"stream":"fhir/infrastructure-wg","topic":"ballot","url":"https://chat/x","messageCount":3,"firstMessageAt":"2026-05-01T00:00:00Z","lastMessageAt":"2026-05-02T00:00:00Z","firstMessageExcerpt":"hello"}""");
        handler.AddJsonResponse("/api/v1/github/items/HL7/fhir%2342",
            JsonMetadata(new Dictionary<string, string>
            {
                ["state"] = "open",
                ["is_pull_request"] = "False",
                ["repo"] = "HL7/fhir",
                ["number"] = "42",
                ["labels"] = "tracker-item",
            }, title: "github thing", url: "https://github.com/HL7/fhir/issues/42"));
        handler.AddJsonResponse("/api/v1/github/repos/HL7/fhir",
            """{"fullName":"HL7/fhir","description":"core","category":"FhirCore","url":"https://github.com/HL7/fhir"}""");

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-100", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-100");
        Assert.NotNull(read);
        Assert.Equal("resolved", read!.Parent!.HydrationStatus);
        Assert.Equal("FHIR", read.Parent.Specification);
        Assert.Equal(5, read.Parent.CommentCount);
        Assert.Equal("body", read.Parent.DescriptionPlain);
        Assert.Equal("<p>body</p>", read.Parent.DescriptionHtml);
        Assert.Equal("<p>resolution</p>", read.Parent.ResolutionDescriptionHtml);
        Assert.Equal("Ada", read.Parent.Reporter);
        Assert.Equal("Grace", read.Parent.Assignee);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            read.Parent.PublicDisplayNamePolicyVersion);
        Assert.Equal(
            ["Ada", "Lin"],
            read.InPersonRequesters.Select(row => row.DisplayName).ToArray());
        Assert.All(
            read.InPersonRequesters,
            row => Assert.Equal(
                PublicDisplayNamePolicy.CurrentVersion,
                row.PublicDisplayNamePolicyVersion));
        Assert.NotNull(read.Parent.CreatedAt);
        Assert.Equal("Patient, Observation", read.Parent.RelatedArtifactsRaw);
        Assert.Equal("patient.html; observation.html", read.Parent.RelatedPagesRaw);
        Assert.Equal(2, read.JiraRows.Count);
        PreparedJiraHydrationRow selfJira = Assert.Single(read.JiraRows, r => r.JiraKey == "FHIR-100");
        Assert.Equal("resolved", selfJira.HydrationStatus);
        PreparedJiraHydrationRow related = Assert.Single(read.JiraRows, r => r.JiraKey == "FHIR-200");
        Assert.Equal("resolved", related.HydrationStatus);
        Assert.Equal("Related Reporter", related.Reporter);
        Assert.Equal("Related Assignee", related.Assignee);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            related.PublicDisplayNamePolicyVersion);
        Assert.Single(read.ZulipRows);
        Assert.Equal(42, read.ZulipRows[0].StreamId);
        Assert.Equal(3, read.ZulipRows[0].MessageCount);
        Assert.Contains(handler.RequestedPathsAndQueries,
            p => p.StartsWith("/api/v1/zulip/threads?", StringComparison.Ordinal)
                && p.Contains("streamName=fhir%2Finfrastructure-wg", StringComparison.Ordinal)
                && p.Contains("topic=ballot", StringComparison.Ordinal));
        Assert.Single(read.GitHubRows);
        Assert.Equal("HL7", read.GitHubRows[0].Owner);
        Assert.Equal("fhir", read.GitHubRows[0].Repo);
        Assert.Equal(42, read.GitHubRows[0].Number);
        Assert.False(read.GitHubRows[0].IsPullRequest);
        Assert.Single(read.RepoRows);
        Assert.Equal("core", read.RepoRows[0].Description);
        Assert.Empty(read.JiraXrefRows);
        using Microsoft.Data.Sqlite.SqliteConnection connection = database.Database.OpenConnection();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT DescriptionHtml, ResolutionDescriptionHtml FROM prepared_ticket_jira_content WHERE TicketKey = 'FHIR-100'";
        using Microsoft.Data.Sqlite.SqliteDataReader content = command.ExecuteReader();
        Assert.True(content.Read());
        Assert.Equal("<p>body</p>", content.GetString(0));
        Assert.Equal("<p>resolution</p>", content.GetString(1));
        content.Close();
        command.CommandText =
            "SELECT GROUP_CONCAT(Value, ',') FROM (SELECT Value FROM prepared_ticket_artifacts WHERE TicketKey = 'FHIR-100' ORDER BY Value)";
        Assert.Equal("Observation,Patient", command.ExecuteScalar());
        command.CommandText =
            "SELECT GROUP_CONCAT(Value, ',') FROM (SELECT Value FROM prepared_ticket_pages WHERE TicketKey = 'FHIR-100' ORDER BY Value)";
        Assert.Equal("observation.html,patient.html", command.ExecuteScalar());
    }

    [Fact]
    public async Task Hydrate_PersistsAssigneeAndDeduplicatedInPersonRequesters()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-109");
        DateTimeOffset refreshAt =
            new(2026, 9, 8, 12, 30, 0, TimeSpan.Zero);

        FakeHandler handler = new();
        handler.AddJsonResponse(
            "/api/v1/jira/items/FHIR-109",
            JsonMetadata(
                new Dictionary<string, string>
                {
                    ["reporter"] = "private-reporter-id",
                    ["assignee"] = "private-assignee-id",
                },
                title: "people",
                url: "https://jira/browse/FHIR-109",
                people: new
                {
                    reporter = "  Ada Lovelace ",
                    assignee = " Grace Hopper  ",
                    inPersonRequesters = new[]
                    {
                        "  Zoë Example ",
                        "zoë example",
                        "Alan Example",
                        "alan example",
                        " ",
                    },
                    publicDisplayNamePolicyVersion =
                        PublicDisplayNamePolicy.CurrentVersion,
                },
                provenance: new
                {
                    source = "jira",
                    contentRevision = 42,
                    isStable = true,
                    projectLastSuccessfulRefreshAt =
                        new Dictionary<string, DateTimeOffset?>
                        {
                            ["FHIR"] = refreshAt,
                        },
                }));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        HydrationAttemptSuccess success =
            Assert.IsType<HydrationAttemptSuccess>(
                await hydrator.HydrateWithResultAsync(
                    "FHIR-109",
                    CancellationToken.None));

        PreparedTicketHydrationReadModel read = Assert.IsType<PreparedTicketHydrationReadModel>(
            await database.Database.GetHydrationAsync("FHIR-109"));
        Assert.Equal("private-reporter-id", success.Batch.Parent.Reporter);
        Assert.Equal(
            "Ada Lovelace",
            success.Batch.Parent.StructuredReporter);
        Assert.Equal("Ada Lovelace", read.Parent!.Reporter);
        Assert.Equal("Grace Hopper", read.Parent.Assignee);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            read.Parent.PublicDisplayNamePolicyVersion);
        Assert.Equal("FHIR", read.Parent.SourceProject);
        Assert.Equal(refreshAt, read.Parent.SourceLastSuccessfulRefreshAt);
        Assert.Equal(42, read.Parent.SourceContentRevision);
        Assert.Equal(
            ["Alan Example", "Zoë Example"],
            read.InPersonRequesters.Select(row => row.DisplayName).ToArray());
        PreparedJiraHydrationRow self = Assert.Single(read.JiraRows);
        Assert.Equal("Ada Lovelace", self.Reporter);
        Assert.Equal("Grace Hopper", self.Assignee);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            self.PublicDisplayNamePolicyVersion);
        Assert.DoesNotContain(
            read.InPersonRequesters,
            row => row.DisplayName.Contains("private", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hydrate_CurrentPolicyRejectsEmailValuedPeopleForParentAndRelatedJira()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(
            database,
            "FHIR-111",
            jiraKey: "FHIR-211");

        FakeHandler handler = new();
        handler.AddJsonResponse(
            "/api/v1/jira/items/FHIR-111",
            JsonMetadata(
                [],
                title: "parent",
                url: "https://jira/browse/FHIR-111",
                people: new
                {
                    reporter = "Ada <ada@example.org>",
                    assignee = "grace@example.org",
                    inPersonRequesters = new[]
                    {
                        "Safe Requester",
                        "Unsafe <unsafe@example.org>",
                    },
                    publicDisplayNamePolicyVersion =
                        PublicDisplayNamePolicy.CurrentVersion,
                }));
        handler.AddJsonResponse(
            "/api/v1/jira/items/FHIR-211",
            JsonMetadata(
                [],
                title: "related",
                url: "https://jira/browse/FHIR-211",
                people: new
                {
                    reporter = "related@example.org",
                    assignee = "Related Assignee",
                    inPersonRequesters = Array.Empty<string>(),
                    publicDisplayNamePolicyVersion =
                        PublicDisplayNamePolicy.CurrentVersion,
                }));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        HydrationAttemptSuccess success =
            Assert.IsType<HydrationAttemptSuccess>(
                await hydrator.HydrateWithResultAsync(
                    "FHIR-111",
                    CancellationToken.None));

        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            success.Batch.Parent.PublicDisplayNamePolicyVersion);
        Assert.Null(success.Batch.Parent.StructuredReporter);
        Assert.Null(success.Batch.Parent.Assignee);
        Assert.Equal(
            ["Safe Requester"],
            success.Batch.Parent.InPersonRequesters);
        HydrationJiraRow relatedNeutral = Assert.Single(
            success.Batch.JiraRows,
            row => row.JiraKey == "FHIR-211");
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            relatedNeutral.PublicDisplayNamePolicyVersion);
        Assert.Null(relatedNeutral.StructuredReporter);
        Assert.Equal("Related Assignee", relatedNeutral.Assignee);

        PreparedTicketHydrationReadModel read =
            Assert.IsType<PreparedTicketHydrationReadModel>(
                await database.Database.GetHydrationAsync("FHIR-111"));
        Assert.Null(read.Parent!.Reporter);
        Assert.Null(read.Parent.Assignee);
        PreparedTicketInPersonRequesterRow requester =
            Assert.Single(read.InPersonRequesters);
        Assert.Equal("Safe Requester", requester.DisplayName);
        Assert.Equal(
            PublicDisplayNamePolicy.CurrentVersion,
            requester.PublicDisplayNamePolicyVersion);
        PreparedJiraHydrationRow related = Assert.Single(
            read.JiraRows,
            row => row.JiraKey == "FHIR-211");
        Assert.Null(related.Reporter);
        Assert.Equal("Related Assignee", related.Assignee);
    }

    public static TheoryData<string, int?> NonCurrentPeoplePolicyVersions =>
        new()
        {
            { "missing", null },
            { "old", PublicDisplayNamePolicy.CurrentVersion - 1 },
            { "unknown", -17 },
            { "future", PublicDisplayNamePolicy.CurrentVersion + 1 },
        };

    [Theory]
    [MemberData(nameof(NonCurrentPeoplePolicyVersions))]
    public async Task Hydrate_NonCurrentPolicyRejectsCompletePeoplePayload(
        string markerKind,
        int? policyVersion)
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(
            database,
            "FHIR-112",
            jiraKey: "FHIR-212");
        object people = PeoplePayload(markerKind, policyVersion);

        FakeHandler handler = new();
        handler.AddJsonResponse(
            "/api/v1/jira/items/FHIR-112",
            JsonMetadata(
                [],
                title: "parent",
                url: "https://jira/browse/FHIR-112",
                people: people));
        handler.AddJsonResponse(
            "/api/v1/jira/items/FHIR-212",
            JsonMetadata(
                [],
                title: "related",
                url: "https://jira/browse/FHIR-212",
                people: people));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        HydrationAttemptSuccess success =
            Assert.IsType<HydrationAttemptSuccess>(
                await hydrator.HydrateWithResultAsync(
                    "FHIR-112",
                    CancellationToken.None));

        Assert.Null(success.Batch.Parent.PublicDisplayNamePolicyVersion);
        Assert.Null(success.Batch.Parent.StructuredReporter);
        Assert.Null(success.Batch.Parent.Assignee);
        Assert.Empty(success.Batch.Parent.InPersonRequesters!);
        Assert.All(
            success.Batch.JiraRows,
            row =>
            {
                Assert.Null(row.PublicDisplayNamePolicyVersion);
                Assert.Null(row.StructuredReporter);
                Assert.Null(row.Assignee);
            });

        PreparedTicketHydrationReadModel read =
            Assert.IsType<PreparedTicketHydrationReadModel>(
                await database.Database.GetHydrationAsync("FHIR-112"));
        Assert.Null(read.Parent!.PublicDisplayNamePolicyVersion);
        Assert.Null(read.Parent.Reporter);
        Assert.Null(read.Parent.Assignee);
        Assert.Empty(read.InPersonRequesters);
        Assert.All(
            read.JiraRows,
            row =>
            {
                Assert.Null(row.PublicDisplayNamePolicyVersion);
                Assert.Null(row.Reporter);
                Assert.Null(row.Assignee);
            });
    }

    [Fact]
    public async Task Hydrate_DoesNotFallbackToLegacyPeopleOrUnstableProvenance()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-110");

        FakeHandler handler = new();
        handler.AddJsonResponse(
            "/api/v1/jira/items/FHIR-110",
            JsonMetadata(
                new Dictionary<string, string>
                {
                    ["reporter"] = "legacy-reporter",
                    ["assignee"] = "legacy-assignee",
                },
                title: "legacy only",
                url: "https://jira/browse/FHIR-110",
                provenance: new
                {
                    source = "jira",
                    contentRevision = 99,
                    isStable = false,
                    projectLastSuccessfulRefreshAt =
                        new Dictionary<string, DateTimeOffset?>
                        {
                            ["FHIR"] = DateTimeOffset.UtcNow,
                        },
                }));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        HydrationAttemptSuccess success =
            Assert.IsType<HydrationAttemptSuccess>(
                await hydrator.HydrateWithResultAsync(
                    "FHIR-110",
                    CancellationToken.None));

        PreparedTicketHydrationReadModel read = Assert.IsType<PreparedTicketHydrationReadModel>(
            await database.Database.GetHydrationAsync("FHIR-110"));
        Assert.Equal("legacy-reporter", success.Batch.Parent.Reporter);
        Assert.Null(success.Batch.Parent.StructuredReporter);
        Assert.Null(read.Parent!.Reporter);
        Assert.Null(read.Parent.Assignee);
        Assert.Null(read.Parent.SourceProject);
        Assert.Null(read.Parent.SourceLastSuccessfulRefreshAt);
        Assert.Null(read.Parent.SourceContentRevision);
        Assert.Empty(read.InPersonRequesters);
        HydrationJiraRow neutralSelf = Assert.Single(success.Batch.JiraRows);
        Assert.Equal("legacy-reporter", neutralSelf.Reporter);
        Assert.Null(neutralSelf.StructuredReporter);
        PreparedJiraHydrationRow self = Assert.Single(read.JiraRows);
        Assert.Null(self.Reporter);
        Assert.Null(self.Assignee);
    }

    [Fact]
    public async Task Hydrate_ParentJiraFetch404_WritesUnresolvedParentButContinuesChildren()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-101", jiraKey: "FHIR-300");

        FakeHandler handler = new();
        handler.AddStatusResponse("/api/v1/jira/items/FHIR-101", HttpStatusCode.NotFound);
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-300",
            JsonMetadata(new Dictionary<string, string> { ["status"] = "Open" }, title: "still here", url: "https://jira/browse/FHIR-300"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-101", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-101");
        Assert.NotNull(read);
        Assert.Equal("unresolved", read!.Parent!.HydrationStatus);
        Assert.Equal("orchestrator 404", read.Parent.HydrationReason);
        Assert.Equal(2, read.JiraRows.Count);
        // Self-jira fetch uses the same parent endpoint, which 404'd, so the self-row is unresolved.
        PreparedJiraHydrationRow selfJira = Assert.Single(read.JiraRows, r => r.JiraKey == "FHIR-101");
        Assert.Equal("unresolved", selfJira.HydrationStatus);
        PreparedJiraHydrationRow related = Assert.Single(read.JiraRows, r => r.JiraKey == "FHIR-300");
        Assert.Equal("resolved", related.HydrationStatus);
    }

    [Fact]
    public async Task Hydrate_RelatedJira503_WritesUnresolvedJiraRow_AndOtherSourcesUnaffected()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-102", jiraKey: "FHIR-400", repo: "HL7/fhir");

        FakeHandler handler = new();
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-102", JsonMetadata([], title: "p", url: "x"));
        handler.AddStatusResponse("/api/v1/jira/items/FHIR-400", HttpStatusCode.ServiceUnavailable);
        handler.AddJsonResponse("/api/v1/github/repos/HL7/fhir",
            """{"fullName":"HL7/fhir","description":"core","category":"FhirCore","url":"https://github.com/HL7/fhir"}""");

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-102", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-102");
        Assert.NotNull(read);
        Assert.Equal(2, read!.JiraRows.Count);
        // Self-jira fetch uses the same parent endpoint, which succeeded.
        PreparedJiraHydrationRow selfJira = Assert.Single(read.JiraRows, r => r.JiraKey == "FHIR-102");
        Assert.Equal("resolved", selfJira.HydrationStatus);
        PreparedJiraHydrationRow related = Assert.Single(read.JiraRows, r => r.JiraKey == "FHIR-400");
        Assert.Equal("unresolved", related.HydrationStatus);
        Assert.Equal("orchestrator 503", related.HydrationReason);
        Assert.Single(read.RepoRows);
        Assert.Equal("resolved", read.RepoRows[0].HydrationStatus);
    }

    [Fact]
    public async Task Hydrate_MalformedZulipThreadId_WritesUnresolvedThreadRow()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-103", zulipThread: "no-colon-here");

        FakeHandler handler = new();
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-103", JsonMetadata([], title: "p", url: "x"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-103", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-103");
        Assert.NotNull(read);
        Assert.Single(read!.ZulipRows);
        Assert.Equal("unresolved", read.ZulipRows[0].HydrationStatus);
        Assert.Equal("malformed thread id", read.ZulipRows[0].HydrationReason);
    }

    [Fact]
    public async Task Hydrate_GitHubFilePathKey_PopulatesPathAndNullsNumber()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-104", githubItem: "HL7/fhir:source/datatypes/dosage.html");

        FakeHandler handler = new();
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-104", JsonMetadata([], title: "p", url: "x"));
        handler.AddJsonResponse("/api/v1/github/items/HL7/fhir:source/datatypes/dosage.html",
            JsonMetadata(new Dictionary<string, string>
            {
                ["repo"] = "HL7/fhir",
                ["file_path"] = "source/datatypes/dosage.html",
            }, title: "dosage", url: "https://github.com/HL7/fhir/blob/main/source/datatypes/dosage.html"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-104", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-104");
        Assert.NotNull(read);
        PreparedGitHubHydrationRow row = Assert.Single(read!.GitHubRows);
        Assert.Equal("HL7", row.Owner);
        Assert.Equal("fhir", row.Repo);
        Assert.Equal("source/datatypes/dosage.html", row.Path);
        Assert.Null(row.Number);
        Assert.Equal("resolved", row.HydrationStatus);
    }

    [Fact]
    public async Task Hydrate_DuplicateOfDeRefDoesNotDoubleCountAgentPickedKey()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-105", jiraKey: "FHIR-500");

        FakeHandler handler = new();
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-105",
            JsonMetadata(new Dictionary<string, string>
            {
                ["duplicate_of"] = "FHIR-500",
                ["related_issues"] = "FHIR-501",
            }, title: "p", url: "x"));
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-500", JsonMetadata([], title: "dup", url: "x"));
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-501", JsonMetadata([], title: "rel", url: "x"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-105", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-105");
        Assert.NotNull(read);
        Assert.Equal(3, read!.JiraRows.Count);
        Assert.Contains(read.JiraRows, r => r.JiraKey == "FHIR-105");
        Assert.Contains(read.JiraRows, r => r.JiraKey == "FHIR-500");
        Assert.Contains(read.JiraRows, r => r.JiraKey == "FHIR-501");
        Assert.Equal(2, read.JiraXrefRows.Count);
        Assert.Contains(read.JiraXrefRows, x => x.Source == "DuplicateOf" && x.JiraKey == "FHIR-500");
        Assert.Contains(read.JiraXrefRows, x => x.Source == "RelatedIssues" && x.JiraKey == "FHIR-501");
    }

    [Fact]
    public async Task Hydrate_RelatedArtifactsNonJiraKey_IsDroppedFromXref()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-106");

        FakeHandler handler = new();
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-106",
            JsonMetadata(new Dictionary<string, string>
            {
                ["related_artifacts"] = "FHIR-9999, R4/observation",
            }, title: "p", url: "x"));
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-9999", JsonMetadata([], title: "art", url: "x"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-106", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-106");
        Assert.NotNull(read);
        PreparedTicketJiraXrefRow xref = Assert.Single(read!.JiraXrefRows);
        Assert.Equal("FHIR-9999", xref.JiraKey);
        Assert.Equal("RelatedArtifacts", xref.Source);
    }

    [Fact]
    public async Task Hydrate_OrchestratorTimeout_NeverThrows()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-107");

        FakeHandler handler = new();
        handler.AddExceptionResponse("/api/v1/jira/items/FHIR-107", new HttpRequestException("boom"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-107", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-107");
        Assert.NotNull(read);
        Assert.Equal("unresolved", read!.Parent!.HydrationStatus);
        Assert.StartsWith("orchestrator error:", read.Parent.HydrationReason);
    }

    [Fact]
    public async Task Hydrate_SecondCall_ReplacesPriorRows()
    {
        using TestDatabase database = CreateDatabase();
        await SeedAgentRowsAsync(database, "FHIR-108", jiraKey: "FHIR-600");

        FakeHandler firstHandler = new();
        firstHandler.AddJsonResponse("/api/v1/jira/items/FHIR-108", JsonMetadata([], title: "p1", url: "x"));
        firstHandler.AddJsonResponse("/api/v1/jira/items/FHIR-600", JsonMetadata([], title: "first", url: "x"));
        PreparedTicketHydrator firstHydrator = CreateHydrator(database, firstHandler);
        await firstHydrator.HydrateAsync("FHIR-108", CancellationToken.None);
        PreparedTicketHydrationReadModel? firstRead = await database.Database.GetHydrationAsync("FHIR-108");
        Assert.Equal(2, firstRead!.JiraRows.Count);
        Assert.Equal("first", Assert.Single(firstRead.JiraRows, r => r.JiraKey == "FHIR-600").Title);

        FakeHandler secondHandler = new();
        secondHandler.AddJsonResponse("/api/v1/jira/items/FHIR-108", JsonMetadata([], title: "p2", url: "x"));
        secondHandler.AddJsonResponse("/api/v1/jira/items/FHIR-600", JsonMetadata([], title: "second", url: "x"));
        PreparedTicketHydrator secondHydrator = CreateHydrator(database, secondHandler);
        await secondHydrator.HydrateAsync("FHIR-108", CancellationToken.None);

        PreparedTicketHydrationReadModel? secondRead = await database.Database.GetHydrationAsync("FHIR-108");
        Assert.NotNull(secondRead);
        Assert.Equal(2, secondRead!.JiraRows.Count);
        PreparedJiraHydrationRow row = Assert.Single(secondRead.JiraRows, r => r.JiraKey == "FHIR-600");
        Assert.Equal("second", row.Title);
    }

    [Fact]
    public async Task PreparedTicketHydrator_AlwaysWritesSelfJiraRow()
    {
        // Self-Jira inclusion contract (Phase 1 §5): a ticket's own
        // prepared_jira_hydration row always exists, even if the agent
        // omitted the self-key from RelatedJiraKeys. The
        // PreparedTicketHydrationController reads self-rows where
        // JiraKey == TicketKey, and this test prevents accidental
        // regression of that behavior.
        using TestDatabase database = CreateDatabase();
        // Seed with NO related jira keys.
        await SeedAgentRowsAsync(database, "FHIR-777");

        FakeHandler handler = new();
        handler.AddJsonResponse("/api/v1/jira/items/FHIR-777",
            JsonMetadata(new Dictionary<string, string>
            {
                ["status"] = "Triaged",
                ["work_group"] = "FHIR-I",
                ["specification"] = "FHIR",
            }, title: "Self ticket", url: "https://jira/browse/FHIR-777"));

        PreparedTicketHydrator hydrator = CreateHydrator(database, handler);
        await hydrator.HydrateAsync("FHIR-777", CancellationToken.None);

        PreparedTicketHydrationReadModel? read = await database.Database.GetHydrationAsync("FHIR-777");
        Assert.NotNull(read);
        PreparedJiraHydrationRow self = Assert.Single(read!.JiraRows);
        Assert.Equal("FHIR-777", self.JiraKey);
        Assert.Equal("FHIR-777", self.TicketKey);
        Assert.Equal("resolved", self.HydrationStatus);
        Assert.Equal("Self ticket", self.Title);
        Assert.Equal("FHIR-I", self.WorkGroup);
        Assert.Equal("FHIR", self.Specification);
    }

    private static PreparedTicketHydrator CreateHydrator(TestDatabase database, FakeHandler handler)
    {
        HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };
        return new PreparedTicketHydrator(client, database.Database, NullLogger<PreparedTicketHydrator>.Instance);
    }

    private static async Task SeedAgentRowsAsync(TestDatabase database, string ticketKey, string? jiraKey = null, string? zulipThread = null, string? githubItem = null, string? repo = null)
    {
        PreparedTicketPayload payload = new()
        {
            Key = ticketKey,
            RequestSummary = "rs",
            CommentSummary = "cs",
            LinkedTicketSummary = "ls",
            RelatedTicketSummary = "rts",
            RelatedZulipSummary = "rzs",
            RelatedGitHubSummary = "rgs",
            ExistingProposed = "ep",
            ProposalA = "a",
            ProposalAJustification = "aj",
            ProposalAImpact = "Non-substantive",
            ProposalB = "b",
            ProposalBJustification = "bj",
            ProposalBImpact = "Compatible, substantive",
            ProposalC = "c",
            ProposalCJustification = "cj",
            Recommendation = "A",
            RecommendationJustification = "rj",
            SavedAt = DateTimeOffset.UtcNow,
            Repos = repo is null ? [] : [new PreparedTicketRepoPayload { Repo = repo, RepoCategory = "FhirCore", Justification = "r" }],
            RelatedJiraTickets = jiraKey is null ? [] : [new PreparedTicketRelatedJiraPayload { AssociatedTicketKey = jiraKey, LinkType = "related", Justification = "j" }],
            RelatedZulipThreads = zulipThread is null ? [] : [new PreparedTicketRelatedZulipPayload { ZulipThreadId = zulipThread, Justification = "z" }],
            RelatedGitHubItems = githubItem is null ? [] : [new PreparedTicketRelatedGitHubPayload { GitHubItemId = githubItem, Justification = "g" }],
        };
        await database.Database.SavePreparedTicketAsync(payload);
    }

    private static string JsonMetadata(
        Dictionary<string, string> metadata,
        string title,
        string url,
        string? content = null,
        object? people = null,
        object? provenance = null,
        DateTimeOffset? updatedAt = null,
        string id = "x")
    {
        var payload = new
        {
            id,
            title,
            content,
            url,
            createdAt = "2026-04-01T00:00:00Z",
            updatedAt,
            metadata,
            people,
            provenance,
        };
        return JsonSerializer.Serialize(payload);
    }

    private static object PeoplePayload(
        string markerKind,
        int? policyVersion)
    {
        Dictionary<string, object?> people = new()
        {
            ["reporter"] = "Reporter Person",
            ["assignee"] = "Assignee Person",
            ["inPersonRequesters"] = new[] { "Requester Person" },
        };
        if (!string.Equals(markerKind, "missing", StringComparison.Ordinal))
        {
            people["publicDisplayNamePolicyVersion"] = policyVersion;
        }
        return people;
    }

    private static TestDatabase CreateDatabase()
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "temp", "preparer-hydrator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "preparer.db");
        PreparerDatabase database = new(path, NullLogger<PreparerDatabase>.Instance);
        database.Initialize();
        return new TestDatabase(directory, database);
    }

    private sealed class TestDatabase(string directory, PreparerDatabase database) : IDisposable
    {
        public PreparerDatabase Database { get; } = database;

        public void Dispose()
        {
            Database.Dispose();
            TestFileCleanup.SafeDeleteDirectory(directory);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<ScriptedResponse>> _byPath = new(StringComparer.Ordinal);

        public List<string> RequestedPaths { get; } = [];
        public List<string> RequestedPathsAndQueries { get; } = [];

        public void AddJsonResponse(string path, string json)
            => AddResponse(path, new ScriptedResponse(HttpStatusCode.OK, json, null, null));

        public void AddStatusResponse(string path, HttpStatusCode statusCode, TimeSpan? retryAfter = null)
            => AddResponse(path, new ScriptedResponse(statusCode, null, null, retryAfter));

        public void AddExceptionResponse(string path, Exception exception)
            => AddResponse(path, new ScriptedResponse(HttpStatusCode.OK, null, exception, null));

        private void AddResponse(string path, ScriptedResponse response)
        {
            if (!_byPath.TryGetValue(path, out Queue<ScriptedResponse>? responses))
            {
                responses = new Queue<ScriptedResponse>();
                _byPath[path] = responses;
            }

            responses.Enqueue(response);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            RequestedPaths.Add(path);
            RequestedPathsAndQueries.Add(request.RequestUri?.PathAndQuery ?? path);

            if (!_byPath.TryGetValue(path, out Queue<ScriptedResponse>? responses) || responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($"{{\"error\":\"unmocked path {path}\"}}", Encoding.UTF8, "application/json"),
                });
            }

            ScriptedResponse scripted = responses.Count > 1 ? responses.Dequeue() : responses.Peek();
            if (scripted.Exception is not null)
            {
                throw scripted.Exception;
            }

            HttpResponseMessage response = new(scripted.StatusCode);
            if (scripted.Json is not null)
            {
                response.Content = new StringContent(scripted.Json, Encoding.UTF8, "application/json");
            }
            if (scripted.RetryAfter is { } retryAfter)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
            }

            return Task.FromResult(response);
        }

        private sealed record ScriptedResponse(
            HttpStatusCode StatusCode,
            string? Json,
            Exception? Exception,
            TimeSpan? RetryAfter);
    }
}
