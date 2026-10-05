using FhirAugury.Common.Api;
using FhirAugury.Processing.Common.Authoring;
using FhirAugury.Processing.Common.Database;
using FhirAugury.Processing.Common.Database.Records;
using FhirAugury.Processing.Contracts;
using FhirAugury.Processing.Jira.Common.Authoring;
using FhirAugury.Processing.Jira.Common.Configuration;
using FhirAugury.Processing.Jira.Common.Database;
using FhirAugury.Processing.Jira.Common.Database.Records;
using FhirAugury.Processing.Jira.Common.Filtering;
using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Api;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannerPersistenceTests
{
    [Fact]
    public async Task Initialize_MissingCompletionId_PreservesPopulatedPlannerState()
    {
        using DatabaseFixture donor = new();
        RetainedAuthoring authoring = await SeedPopulatedCurrentStateAsync(donor.Database);
        Dictionary<string, SqliteRows>? before = null;
        Dictionary<string, byte[]>? artifacts = null;

        using DatabaseFixture fixture = new(path =>
        {
            using SqliteConnection connection = OpenConn(path);
            SeedHandwrittenLegacySource(connection);
            CopyCompanionState(donor.Path, connection);
            Assert.False(HasCompletionColumn(connection));
            Assert.Equal(24, ReadTypedRows(connection, "PRAGMA table_info(jira_processing_source_tickets)").Values.Length);
            before = CaptureTables(connection);
            AssertPopulatedCompanions(before);
            InstallPreservationGuards(connection, before.Keys);
            artifacts = CreateImmutableSentinels(path);
            // This callback runs before the target's first Planner Initialize,
            // not after generating and then damaging a current source table.
            Assert.False(HasCompletionColumn(connection));
        });

        Assert.NotNull(before);
        Assert.NotNull(artifacts);
        for (int initialization = 0; initialization < 2; initialization++)
        {
            if (initialization != 0)
            {
                fixture.Database.Initialize();
            }
            using (SqliteConnection connection = OpenConn(fixture.Path))
            {
                AssertRetainedTables(before, connection);
                Assert.True(HasCompletionColumn(connection));
                Assert.Equal(
                    before[SourceTable].Columns.Append("CompletionId"),
                    ReadTable(connection, SourceTable).Columns);
                Assert.Equal(0, ScalarCount(connection,
                    "SELECT COUNT(*) FROM jira_processing_source_tickets WHERE CompletionId IS NOT NULL"));
            }
            await AssertPopulatedApisAsync(fixture.Database, authoring, hasCompletionIds: false);
            AssertImmutableSentinels(artifacts);
        }
    }

    [Fact]
    public async Task Initialize_CurrentSchema_PreservesCompletionAndAuthoringState()
    {
        using DatabaseFixture fixture = new();
        RetainedAuthoring authoring = await SeedPopulatedCurrentStateAsync(fixture.Database);
        Dictionary<string, SqliteRows> before;
        using (SqliteConnection connection = OpenConn(fixture.Path))
        {
            before = CaptureTables(connection);
            AssertPopulatedCompanions(before);
            InstallPreservationGuards(connection, before.Keys);
        }
        Dictionary<string, byte[]> artifacts = CreateImmutableSentinels(fixture.Path);

        for (int initialization = 0; initialization < 2; initialization++)
        {
            fixture.Database.Initialize();
            using (SqliteConnection connection = OpenConn(fixture.Path))
            {
                AssertRetainedTables(before, connection);
            }
            await AssertPopulatedApisAsync(fixture.Database, authoring, hasCompletionIds: true);
            AssertImmutableSentinels(artifacts);
        }
    }

    [Fact]
    public void EnsureSchema_CreatesAllHydrationAndTopicTables()
    {
        using DatabaseFixture fixture = new();
        using SqliteConnection conn = OpenConn(fixture.Path);

        string[] expected =
        [
            "planned_ticket_hydration",
            "planned_jira_hydration",
            "planned_zulip_hydration",
            "planned_github_hydration",
            "planned_repo_hydration",
            "planned_ticket_related_jira",
            "planned_ticket_related_zulip",
            "planned_ticket_related_github",
            "planned_ticket_jira_xref",
            "planned_ticket_topics",
            "planned_ticket_topic_groups",
            "planned_ticket_topic_members",
            "planned_ticket_topic_repos",
            "planned_ticket_jira_content",
            "planned_ticket_authoring_state",
            "planned_ticket_partition_receipts",
            "planned_ticket_run_item_partitions",
            "jira_review_workgroups",
            "planner_schema_migrations",
        ];
        foreach (string table in expected)
        {
            Assert.True(TableExists(conn, table), $"Expected table {table} to exist.");
        }

        // Composite-unique index on (TopicRowId, RepoKey).
        Assert.True(IndexExists(conn, "idx_planned_ticket_topic_repos_topic_repo"));
    }

    [Fact]
    public async Task SaveHydrationAsync_RoundTrips_NeutralHydrationBatch()
    {
        using DatabaseFixture fixture = new();
        IHydrationTargetDatabase target = fixture.Database;
        DateTimeOffset at = DateTimeOffset.UtcNow;

        HydrationBatch batch = new(
            TicketKey: "FHIR-42",
            Parent: new HydrationTicketRow(
                "FHIR-42", "Major", "Persuasive", null, "FHIR", "5.0.0", null, null, "Compatible, substantive", null, 3, "desc",
                at, "resolved", null),
            JiraRows:
            [
                new HydrationJiraRow("FHIR-42", "FHIR-42", "Self ticket", "Triaged", "Change Request", null, null, null,
                    "FHIR Infrastructure", "FHIR", null, "https://example/FHIR-42", at, "resolved", null),
                new HydrationJiraRow("FHIR-42", "FHIR-43", "Linked", null, null, null, null, null,
                    null, null, null, "https://example/FHIR-43", at, "resolved", null),
            ],
            ZulipRows:
            [
                new HydrationZulipRow("FHIR-42", "implementers:foo", 11, "implementers", "foo", 3,
                    at, at, "first msg", "https://chat/x", at, "resolved", null),
            ],
            GitHubRows:
            [
                new HydrationGitHubRow("FHIR-42", "HL7/fhir#1", "HL7", "fhir", 1, null, "Issue", "open", false,
                    null, at, "https://github.com/HL7/fhir/issues/1", at, "resolved", null),
            ],
            RepoRows:
            [
                new HydrationRepoRow("FHIR-42", "HL7/fhir", "core", null, null, "FhirCore", "https://github.com/HL7/fhir",
                    at, "resolved", null),
            ],
            JiraXrefRows:
            [
                new HydrationJiraXrefRow("FHIR-42", "FHIR-9", "DuplicateOf"),
            ]);

        await target.SaveHydrationAsync(batch, CancellationToken.None);
        PlannedTicketHydrationReadModel? read = await fixture.Database.GetHydrationAsync("FHIR-42");
        Assert.NotNull(read);
        Assert.NotNull(read!.Parent);
        Assert.Equal("FHIR", read.Parent!.Specification);
        Assert.Equal(2, read.JiraRows.Count);
        Assert.Contains(read.JiraRows, r => r.JiraKey == "FHIR-42" && r.WorkGroupClean is not null);
        Assert.Single(read.ZulipRows);
        Assert.Single(read.GitHubRows);
        Assert.False(read.GitHubRows[0].IsPullRequest);
        Assert.Single(read.RepoRows);
        Assert.Single(read.JiraXrefRows);

        // Idempotent: a re-save replaces rather than accumulates.
        await target.SaveHydrationAsync(batch, CancellationToken.None);
        PlannedTicketHydrationReadModel? read2 = await fixture.Database.GetHydrationAsync("FHIR-42");
        Assert.NotNull(read2);
        Assert.Equal(2, read2!.JiraRows.Count);
    }

    [Fact]
    public async Task SaveTopicGrouping_RoundTripsSpannedReposAndDeduplicatesCaseInsensitive()
    {
        using DatabaseFixture fixture = new();
        PlannedTicketTopicGroupingPayload payload = new()
        {
            WorkGroupClean = "fhir-infrastructure",
            WorkGroupDisplay = "FHIR Infrastructure",
            Specification = "FHIR",
            Type = "Change Request",
            Topics =
            [
                new PlannedTicketTopicPayload
                {
                    ShortDescription = "Coordinate Patient changes across core + extensions",
                    LongerDescription = "Long description.",
                    RenderOrderHint = 1,
                    SpannedRepos = ["HL7/fhir", "HL7/fhir-extensions", "hl7/FHIR"], // last is a dup-by-case
                    LinkedTicketGroups =
                    [
                        new PlannedTicketTopicGroupPayload
                        {
                            FirstTicketKey = "FHIR-100",
                            Rationale = "Same artifact",
                            Members =
                            [
                                new PlannedTicketTopicGroupMemberPayload { TicketKey = "FHIR-100", Order = 0 },
                                new PlannedTicketTopicGroupMemberPayload { TicketKey = "FHIR-101", Order = 1 },
                            ],
                        },
                    ],
                    RemainingTicketKeys = ["FHIR-200"],
                },
            ],
        };

        await fixture.Database.SaveTopicGroupingAsync(payload);

        PlannedTicketTopicsForCategory? result = await fixture.Database.GetWorkGroupTopicsAsync(
            "fhir-infrastructure", "FHIR", "Change Request");
        Assert.NotNull(result);
        PlannedTicketTopicDetail topic = Assert.Single(result!.Topics);
        // SpannedRepos preserves order and dedupes case-insensitively.
        Assert.Equal(["HL7/fhir", "HL7/fhir-extensions"], topic.SpannedRepos);
        PlannedTicketTopicGroup group = Assert.Single(topic.LinkedTicketGroups);
        Assert.Equal("FHIR-100", group.FirstTicketKey);
        Assert.Equal(2, group.Members.Count);
        Assert.Single(topic.RemainingTicketKeys);
        Assert.Equal("FHIR-200", topic.RemainingTicketKeys[0]);
    }

    [Fact]
    public async Task SavePlannedTicketAsync_RoundTripsAgentPayload()
    {
        using DatabaseFixture fixture = new();
        PlannedTicketPayload payload = new()
        {
            Key = "FHIR-77",
            Resolution = "Persuasive",
            ResolutionSummary = "Adopt proposal A.",
            FeatureProposal = "Add foo.",
            DesignRationale = "Because.",
            Repos =
            [
                new PlannedTicketRepoPayload { RepoKey = "HL7/fhir", RepoRevision = "abc123", Justification = "primary" },
            ],
            RepoChanges =
            [
                new PlannedTicketRepoChangePayload
                {
                    TicketRepoId = "tr1",
                    RepoKey = "HL7/fhir",
                    ChangeSequence = 0,
                    FilePath = "source/foo.html",
                    ChangeTitle = "add x",
                    ChangeDescription = "details",
                    ReplacementLines = ["line a", "line b"],
                    Reason = "spec change",
                },
            ],
            OpenQuestions =
            [
                new PlannedTicketOpenQuestionPayload { TicketRepoId = "tr1", RepoKey = "HL7/fhir", QuestionSequence = 0, Question = "What about y?" },
            ],
        };

        await fixture.Database.SavePlannedTicketAsync(payload);

        PlannedTicketDetail? detail = await fixture.Database.GetPlannedTicketAsync("FHIR-77");
        Assert.NotNull(detail);
        Assert.Equal("Adopt proposal A.", detail!.Ticket.ResolutionSummary);
        Assert.Single(detail.Repos);
        Assert.Equal("abc123", detail.Repos[0].RepoRevision);
        Assert.Single(detail.RepoChanges);
        Assert.Equal(["line a", "line b"], detail.RepoChanges[0].ReplacementLines);
        Assert.Single(detail.OpenQuestions);
    }

    [Fact]
    public void PlannedTicketPayloadValidator_RejectsInvalidKey()
    {
        PlannedTicketPayload payload = new()
        {
            Key = "not-a-jira-key",
            ResolutionSummary = "x",
        };
        IReadOnlyList<string> errors = PlannedTicketPayloadValidator.Validate(payload);
        Assert.Contains(errors, e => e.Contains("Key must be a valid Jira key", StringComparison.Ordinal));
    }

    [Fact]
    public void PlannedTicketTopicGroupingPayloadValidator_RejectsMalformedSpannedRepo()
    {
        PlannedTicketTopicGroupingPayload payload = new()
        {
            WorkGroupClean = "wg",
            WorkGroupDisplay = "WG",
            Specification = "FHIR",
            Type = "Change Request",
            Topics =
            [
                new PlannedTicketTopicPayload
                {
                    ShortDescription = "ok",
                    LongerDescription = "ok",
                    SpannedRepos = ["no-slash", "HL7/fhir"],
                },
            ],
        };
        IReadOnlyList<string> errors = PlannedTicketTopicGroupingPayloadValidator.Validate(payload);
        Assert.Contains(errors, e => e.Contains("no-slash", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------
    // GetClusteringSignalsAsync tests (slot 0605-01 Phase 0)
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetClusteringSignalsAsync_ReturnsNull_WhenWorkgroupHasNoHydration()
    {
        using DatabaseFixture fixture = new();

        PlannedTicketClusteringSignals? signals = await fixture.Database.GetClusteringSignalsAsync("OrdersAndObservations");

        Assert.Null(signals);
    }

    [Fact]
    public async Task GetClusteringSignalsAsync_JoinsRepoChangesAndImpactsForTicket()
    {
        using DatabaseFixture fixture = new();
        await SeedSourceTicketAsync(fixture.Database, "FHIR-1", "Orders and Observations", "Change Request", "FHIR Core");
        await SeedHydrationSelfRowAsync(fixture.Database, "FHIR-1", "Orders and Observations", "Change Request", "FHIR Core", "Title-1", "Open");
        await fixture.Database.SavePlannedTicketAsync(new PlannedTicketPayload
        {
            Key = "FHIR-1",
            Resolution = "Persuasive",
            ResolutionSummary = "summary-1",
            FeatureProposal = "proposal-1",
            DesignRationale = "rationale-1",
            Repos =
            [
                new PlannedTicketRepoPayload { RepoKey = "HL7/fhir", Justification = "primary" },
                new PlannedTicketRepoPayload { RepoKey = "HL7/fhir-extensions", Justification = "secondary" },
            ],
            RepoChanges =
            [
                new PlannedTicketRepoChangePayload
                {
                    TicketRepoId = "tr1",
                    RepoKey = "HL7/fhir",
                    ChangeSequence = 0,
                    FilePath = "source/observation.html",
                },
                new PlannedTicketRepoChangePayload
                {
                    TicketRepoId = "tr2",
                    RepoKey = "HL7/fhir-extensions",
                    ChangeSequence = 0,
                    FilePath = "input/extensions/observation-rendered.xml",
                },
            ],
            RepoImpacts =
            [
                new PlannedTicketRepoImpactPayload
                {
                    TicketRepoId = "tr1",
                    RepoKey = "HL7/fhir",
                    AffectedFilePath = "source/observation-mappings.html",
                },
            ],
        });

        PlannedTicketClusteringSignals? signals = await fixture.Database.GetClusteringSignalsAsync("OrdersAndObservations");

        Assert.NotNull(signals);
        Assert.Equal("OrdersAndObservations", signals!.WorkGroupClean);
        Assert.Equal("Orders and Observations", signals.WorkGroupDisplay);
        PlannedTicketClusteringSignal only = Assert.Single(signals.Tickets);
        Assert.Equal("FHIR-1", only.IssueKey);
        Assert.True(only.HasPlannedTicket);
        Assert.Equal("resolved", only.HydrationStatus);
        Assert.Equal("summary-1", only.ResolutionSummary);
        Assert.Equal("proposal-1", only.FeatureProposal);
        Assert.Equal("rationale-1", only.DesignRationale);
        Assert.Equal(["HL7/fhir", "HL7/fhir-extensions"], only.Repos);
        Assert.Equal(2, only.RepoChanges.Count);
        Assert.Contains(only.RepoChanges, c => c.RepoKey == "HL7/fhir" && c.FilePath == "source/observation.html");
        Assert.Contains(only.RepoChanges, c => c.RepoKey == "HL7/fhir-extensions" && c.FilePath == "input/extensions/observation-rendered.xml");
        PlannedTicketClusteringRepoImpact impact = Assert.Single(only.RepoImpacts);
        Assert.Equal("HL7/fhir", impact.RepoKey);
        Assert.Equal("source/observation-mappings.html", impact.AffectedFilePath);
    }

    [Fact]
    public async Task GetClusteringSignalsAsync_EmitsHydrationOnlyTicketWithEmptyPlanFields()
    {
        using DatabaseFixture fixture = new();
        await SeedSourceTicketAsync(fixture.Database, "FHIR-1", "Orders and Observations", "Change Request", "FHIR Core");
        await SeedHydrationSelfRowAsync(fixture.Database, "FHIR-1", "Orders and Observations", "Change Request", "FHIR Core", "Title-1", "Open");

        PlannedTicketClusteringSignals? signals = await fixture.Database.GetClusteringSignalsAsync("OrdersAndObservations");

        Assert.NotNull(signals);
        PlannedTicketClusteringSignal only = Assert.Single(signals!.Tickets);
        Assert.Equal("FHIR-1", only.IssueKey);
        Assert.False(only.HasPlannedTicket);
        Assert.Equal(string.Empty, only.ResolutionSummary);
        Assert.Equal(string.Empty, only.FeatureProposal);
        Assert.Equal(string.Empty, only.DesignRationale);
        Assert.Empty(only.Repos);
        Assert.Empty(only.RepoChanges);
        Assert.Empty(only.RepoImpacts);
        Assert.Equal("resolved", only.HydrationStatus);
    }

    [Fact]
    public async Task GetClusteringSignalsAsync_SurfacesNullHydrationStatus_WhenPlannedTicketHasNoSelfRow()
    {
        // Set-up: workgroup display is resolvable via a *different* ticket's
        // hydration self-row (FHIR-99). FHIR-1 has a source-ticket row and a
        // plan but no self-row of its own — its HydrationStatus must come
        // back as null so the per-workgroup skill can abort per OQ3.
        using DatabaseFixture fixture = new();
        await SeedSourceTicketAsync(fixture.Database, "FHIR-1", "Orders and Observations", "Change Request", "FHIR Core");
        await SeedSourceTicketAsync(fixture.Database, "FHIR-99", "Orders and Observations", "Change Request", "FHIR Core");
        await SeedHydrationSelfRowAsync(fixture.Database, "FHIR-99", "Orders and Observations", "Change Request", "FHIR Core", "Title-99", "Open");
        await fixture.Database.SavePlannedTicketAsync(new PlannedTicketPayload
        {
            Key = "FHIR-1",
            Resolution = "Persuasive",
            ResolutionSummary = "summary-1",
            Repos = [new PlannedTicketRepoPayload { RepoKey = "HL7/fhir", Justification = "primary" }],
        });

        PlannedTicketClusteringSignals? signals = await fixture.Database.GetClusteringSignalsAsync("OrdersAndObservations");

        Assert.NotNull(signals);
        Assert.Equal(2, signals!.Tickets.Count);
        PlannedTicketClusteringSignal fhir1 = signals.Tickets.Single(t => t.IssueKey == "FHIR-1");
        Assert.Null(fhir1.HydrationStatus);
        Assert.True(fhir1.HasPlannedTicket);
        PlannedTicketClusteringSignal fhir99 = signals.Tickets.Single(t => t.IssueKey == "FHIR-99");
        Assert.Equal("resolved", fhir99.HydrationStatus);
        Assert.False(fhir99.HasPlannedTicket);
    }

    [Fact]
    public async Task GetClusteringSignalsAsync_SurfacesUnresolvedHydrationStatus_ForAbortDecision()
    {
        using DatabaseFixture fixture = new();
        await SeedSourceTicketAsync(fixture.Database, "FHIR-1", "Orders and Observations", "Change Request", "FHIR Core");
        await SeedHydrationSelfRowAsync(fixture.Database, "FHIR-1", "Orders and Observations", "Change Request", "FHIR Core", "Title-1", "Open", hydrationStatus: "unresolved");

        PlannedTicketClusteringSignals? signals = await fixture.Database.GetClusteringSignalsAsync("OrdersAndObservations");

        Assert.NotNull(signals);
        PlannedTicketClusteringSignal only = Assert.Single(signals!.Tickets);
        Assert.Equal("unresolved", only.HydrationStatus);
    }

    [Fact]
    public async Task GetClusteringSignalsAsync_OrdersByIssueKey()
    {
        using DatabaseFixture fixture = new();
        foreach (string key in new[] { "FHIR-3", "FHIR-1", "FHIR-2" })
        {
            await SeedSourceTicketAsync(fixture.Database, key, "Orders and Observations", "Change Request", "FHIR Core");
            await SeedHydrationSelfRowAsync(fixture.Database, key, "Orders and Observations", "Change Request", "FHIR Core", title: key, status: "Open");
        }

        PlannedTicketClusteringSignals? signals = await fixture.Database.GetClusteringSignalsAsync("OrdersAndObservations");

        Assert.NotNull(signals);
        Assert.Equal(["FHIR-1", "FHIR-2", "FHIR-3"], signals!.Tickets.Select(t => t.IssueKey).ToArray());
    }

    [Fact]
    public async Task GetClusteringSignalsAsync_IgnoresNonSelfHydrationRows()
    {
        using DatabaseFixture fixture = new();
        await SeedSourceTicketAsync(fixture.Database, "FHIR-1", "Orders and Observations", "Change Request", "FHIR Core");
        await SeedHydrationSelfRowAsync(fixture.Database, "FHIR-1", "Orders and Observations", "Change Request", "FHIR Core", "Title-1", "Open");
        // Non-self row: same IssueKey but different JiraKey (a linked ticket).
        // Must not surface a second clustering row or shadow the self row.
        await SeedHydrationLinkedRowAsync(fixture.Database, "FHIR-1", "FHIR-555", "Orders and Observations");

        PlannedTicketClusteringSignals? signals = await fixture.Database.GetClusteringSignalsAsync("OrdersAndObservations");

        Assert.NotNull(signals);
        PlannedTicketClusteringSignal only = Assert.Single(signals!.Tickets);
        Assert.Equal("FHIR-1", only.IssueKey);
        Assert.Equal("resolved", only.HydrationStatus);
    }

    [Fact]
    public async Task ResolveWorkGroupDisplayAsync_PrefersTopicRowOverHydrationFallback()
    {
        // Seed a hydration self-row with one display form, then write a topic
        // payload with a *different* display form for the same WG-clean slug.
        // The clustering-signals envelope must surface the topic-row display
        // (preparer parity).
        using DatabaseFixture fixture = new();
        await SeedSourceTicketAsync(fixture.Database, "FHIR-1", "OrdersAndObservations", "Change Request", "FHIR Core");
        await SeedHydrationSelfRowAsync(fixture.Database, "FHIR-1", "OrdersAndObservations", "Change Request", "FHIR Core", "Title-1", "Open");
        await fixture.Database.SaveTopicGroupingAsync(new PlannedTicketTopicGroupingPayload
        {
            WorkGroupClean = "OrdersAndObservations",
            WorkGroupDisplay = "Orders & Observations",
            Specification = "FHIR Core",
            Type = "Change Request",
            Topics =
            [
                new PlannedTicketTopicPayload
                {
                    ShortDescription = "seeded",
                    LongerDescription = "seeded",
                    RemainingTicketKeys = ["FHIR-1"],
                },
            ],
        });

        PlannedTicketClusteringSignals? signals = await fixture.Database.GetClusteringSignalsAsync("OrdersAndObservations");

        Assert.NotNull(signals);
        Assert.Equal("Orders & Observations", signals!.WorkGroupDisplay);
    }

    [Fact]
    public async Task SaveTopicGroupingAsync_EmptyTopicsList_WipesExistingTuple()
    {
        // The per-tuple wipe primitive Phase 1 documents but does not invoke.
        using DatabaseFixture fixture = new();
        await fixture.Database.SaveTopicGroupingAsync(new PlannedTicketTopicGroupingPayload
        {
            WorkGroupClean = "FHIRInfrastructure",
            WorkGroupDisplay = "FHIR Infrastructure",
            Specification = "FHIR",
            Type = "Change Request",
            Topics =
            [
                new PlannedTicketTopicPayload
                {
                    ShortDescription = "to-be-wiped",
                    LongerDescription = "to-be-wiped",
                    SpannedRepos = ["HL7/fhir"],
                    LinkedTicketGroups =
                    [
                        new PlannedTicketTopicGroupPayload
                        {
                            FirstTicketKey = "FHIR-100",
                            Rationale = "rationale",
                            Members =
                            [
                                new PlannedTicketTopicGroupMemberPayload { TicketKey = "FHIR-100", Order = 0 },
                                new PlannedTicketTopicGroupMemberPayload { TicketKey = "FHIR-101", Order = 1 },
                            ],
                        },
                    ],
                    RemainingTicketKeys = ["FHIR-200"],
                },
            ],
        });

        PlannedTicketTopicsForCategory? before = await fixture.Database.GetWorkGroupTopicsAsync(
            "FHIRInfrastructure", "FHIR", "Change Request");
        Assert.NotNull(before);
        Assert.Single(before!.Topics);

        await fixture.Database.SaveTopicGroupingAsync(new PlannedTicketTopicGroupingPayload
        {
            WorkGroupClean = "FHIRInfrastructure",
            WorkGroupDisplay = "FHIR Infrastructure",
            Specification = "FHIR",
            Type = "Change Request",
            Topics = [],
        });

        PlannedTicketTopicsForCategory? after = await fixture.Database.GetWorkGroupTopicsAsync(
            "FHIRInfrastructure", "FHIR", "Change Request");
        // Read endpoint returns null when there are no topic rows for the
        // tuple (matches the Phase 0 controller-level 404 semantics).
        Assert.Null(after);

        // Belt-and-suspenders: raw row counts for the three child tables.
        using SqliteConnection conn = OpenConn(fixture.Path);
        Assert.Equal(0, ScalarCount(conn, "SELECT COUNT(*) FROM planned_ticket_topics WHERE WorkGroupClean = 'FHIRInfrastructure' AND Specification = 'FHIR' AND Type = 'Change Request'"));
        Assert.Equal(0, ScalarCount(conn, "SELECT COUNT(*) FROM planned_ticket_topic_groups"));
        Assert.Equal(0, ScalarCount(conn, "SELECT COUNT(*) FROM planned_ticket_topic_members"));
        Assert.Equal(0, ScalarCount(conn, "SELECT COUNT(*) FROM planned_ticket_topic_repos"));
    }

    // Current companion state deliberately isolates the source-table upgrade.
    // The donor is synthetic, and its source table/indices are never copied.
    private const string SourceTable = "jira_processing_source_tickets";
    private static readonly DateTimeOffset RetainedAt =
        new(2026, 9, 1, 12, 30, 0, TimeSpan.Zero);
    private static readonly string[] AuthoredKeys = ["FHIR-101", "FHIR-102", "FHIR-103"];
    private static readonly string[] CompanionTables =
    [
        "planned_tickets",
        "planned_ticket_repos",
        "planned_ticket_repo_changes",
        "planned_ticket_repo_impacts",
        "planned_ticket_change_validations",
        "planned_ticket_testing_considerations",
        "planned_ticket_open_questions",
        "planned_ticket_jira_content",
        "planned_ticket_hydration",
        "planned_jira_hydration",
        "planned_zulip_hydration",
        "planned_github_hydration",
        "planned_repo_hydration",
        "planned_ticket_related_jira",
        "planned_ticket_related_zulip",
        "planned_ticket_related_github",
        "planned_ticket_jira_xref",
        "planned_ticket_topics",
        "planned_ticket_topic_groups",
        "planned_ticket_topic_members",
        "planned_ticket_topic_repos",
        "jira_review_workgroups",
        "planner_schema_migrations",
        "authoring_processor_modes",
        "authoring_runs",
        "authoring_run_items",
        "authoring_run_input_provenance",
        "authoring_run_attempts",
        "authoring_result_receipts",
        "authoring_run_stages",
        "authoring_mutation_fences",
        "authoring_review_snapshots",
        "authoring_revalidation_lineage",
        "planned_ticket_authoring_state",
        "planned_ticket_partition_receipts",
        "planned_ticket_run_item_partitions",
        "planned_ticket_applier_projection_pending",
        "fixture_retained_sentinel",
    ];

    private sealed record SqliteRows(string[] Columns, object?[][] Values);

    private sealed record RetainedAuthoring(
        AuthoringProcessorModeRecord Mode,
        AuthoringRunRecord Run,
        IReadOnlyList<AuthoringRunItemRecord> Items,
        IReadOnlyList<AuthoringResultReceipt> Receipts,
        IReadOnlyList<AuthoringRunInputProvenanceRecord> Provenance,
        IReadOnlyList<AuthoringRunStageRecord> Stages,
        PlannedTicketRunPartition Partition,
        AuthoringRunStageReceipt GroupingReceipt);

    private static async Task<RetainedAuthoring> SeedPopulatedCurrentStateAsync(PlannerDatabase database)
    {
        JiraProcessingSourceTicketStore sourceStore = new(database.DatabasePath);
        AuthoringRunStore store = new(database);
        JiraAuthoringRunCoordinator coordinator = new(
            store,
            sourceStore,
            new JiraConfiguredTicketSelector(sourceStore, new TestJiraTicketLabelMatcher()),
            new JiraProcessingFilterResolver(),
            Options.Create(new JiraProcessingOptions
            {
                AgentCliCommand = "unused {ticketKey}",
                JiraSourceAddress = "https://synthetic.invalid",
                SourceTicketShape = "fhir",
            }));

        List<JiraProcessingSourceTicketRecord> sources = [];
        foreach (string key in AuthoredKeys)
        {
            sources.Add(await sourceStore.UpsertAsync(
                SourceEntry(key), "fhir", false, RetainedAt.AddHours(-1), 731L, CancellationToken.None));
            await database.SaveHydrationAsync(PopulatedHydration(key), CancellationToken.None);
            await database.UpsertRelatedJiraAsync(key, "FHIR-900", "Linked");
            await database.UpsertRelatedZulipAsync(key, "implementers:retained");
            await database.UpsertRelatedGitHubAsync(key, "HL7/fhir#81");
        }
        await sourceStore.UpsertAsync(SourceEntry("FHIR-104"), "fhir", false, CancellationToken.None);
        await database.SaveWorkGroupCatalogAsync(
            [new HydrationWorkGroupRow("fhir", "FHIR Infrastructure", "FHIRInfrastructure", RetainedAt)]);
        await store.EnsureProcessorModeAsync("jira-fhir");
        await store.TransitionProcessorModeAsync("jira-fhir", "legacy", "cutting-over");
        await store.TransitionProcessorModeAsync("jira-fhir", "cutting-over", "run-backed");
        JiraAuthoringRunCreation creation = await coordinator.CreateRunAsync(sources, databaseOnly: true);
        Assert.True(await store.TryAcquireMutationFenceAsync("jira-fhir", creation.Run.Id));

        List<AuthoringResultReceipt> receipts = [];
        foreach (AuthoringRunItemRecord item in creation.Items)
        {
            AuthoringOperationClaim claim = Assert.IsType<AuthoringOperationClaim>(
                await store.ClaimItemAsync(creation.Run.Id, item.Id));
            PlannedTicketPayload payload = PopulatedPlan(item.BusinessKey);
            string hash = PlannedTicketAuthoringDtos.ComputeContentHash(payload);
            AuthoringReceiptAcceptance accepted = await store.AcceptResultAsync(
                new AuthoringResultSubmission(
                    creation.Run.Id, item.Id, claim.OperationId, item.ExpectedSourceRevision, hash),
                claim.OperationToken,
                async (connection, ct) =>
                {
                    await JiraProcessingSourceTicketStore.EnsureCurrentSourceRevisionAsync(
                        connection, item.BusinessKey, "fhir", item.ExpectedSourceRevision, ct);
                    await database.SavePlannedTicketForAuthoringAsync(
                        connection, payload, hash, creation.Run.Id, item.Id, claim.OperationId, ct);
                });
            Assert.False(accepted.IsReplay);
            receipts.Add(accepted.Receipt);
            await store.MarkItemCompleteAsync(item.Id, accepted.Receipt.ReceiptId);
        }

        PlannedTicketRunPartition partition = Assert.Single(await database.GetRunPartitionsAsync(creation.Run.Id));
        Assert.Equal(AuthoredKeys, partition.TicketKeys);
        AuthoringRunStageRecord stage = await store.EnsureRunStageAsync(
            creation.Run.Id, "grouping", partition.PartitionKey, partition.InputFingerprint);
        AuthoringRunStageLease lease = Assert.IsType<AuthoringRunStageLease>(
            await store.TryStartRunStageAsync(stage.Id));
        await database.SaveTopicGroupingForRunAsync(
            new PlannedTicketTopicGroupingPayload
            {
                WorkGroupClean = partition.WorkGroupClean,
                WorkGroupDisplay = partition.WorkGroupDisplay,
                Specification = partition.Specification,
                Type = partition.Type,
                SavedAt = RetainedAt,
                Topics =
                [
                    new PlannedTicketTopicPayload
                    {
                        ShortDescription = "Retained coordinated change",
                        LongerDescription = "Retained grouping rationale and ordering.",
                        RenderOrderHint = 7,
                        SpannedRepos = ["HL7/fhir", "HL7/fhir-extensions"],
                        LinkedTicketGroups =
                        [
                            new PlannedTicketTopicGroupPayload
                            {
                                FirstTicketKey = "FHIR-101",
                                Rationale = "The two tickets share an artifact.",
                                Members =
                                [
                                    new() { TicketKey = "FHIR-101", Order = 0 },
                                    new() { TicketKey = "FHIR-102", Order = 1 },
                                ],
                            },
                        ],
                        RemainingTicketKeys = ["FHIR-103"],
                    },
                ],
            },
            creation.Run.Id, stage.Id, lease.LeaseId, partition.InputFingerprint);
        AuthoringRunStageReceipt groupingReceipt = Assert.IsType<AuthoringRunStageReceipt>(
            await database.GetGroupingReceiptAsync(
                creation.Run.Id, stage.Id, partition.PartitionKey, partition.InputFingerprint));
        await store.CompleteRunStageAsync(stage.Id, lease.LeaseId);
        await store.MarkRunFinalizingAsync(creation.Run.Id);
        await store.CompleteRunAsync(creation.Run.Id, snapshotId: null);

        using (SqliteConnection connection = database.OpenConnection())
        {
            Execute(connection, """
                UPDATE jira_processing_source_tickets
                SET Description = @description, StartedProcessingAt = @at,
                    LastProcessingAttemptAt = @at, ProcessingStatus = 'error',
                    ProcessingError = 'retained processing error', ProcessingAttemptCount = 4,
                    ErrorMessage = 'retained agent error', AgentExitCode = 17, ErrorOccurredAt = @at
                WHERE Key = 'FHIR-104';
                INSERT INTO planner_schema_migrations(Id, AppliedAt) VALUES('synthetic-current-companions', @at);
                CREATE TABLE fixture_retained_sentinel(
                    Id INTEGER PRIMARY KEY, TicketKey TEXT NOT NULL, RunId TEXT NOT NULL,
                    Label TEXT NOT NULL, Payload BLOB NOT NULL, Weight REAL NOT NULL, OptionalValue TEXT);
                INSERT INTO fixture_retained_sentinel
                    VALUES(19, 'FHIR-101', @runId, 'retained sentinel', @payload, 1.25, NULL);
                CREATE INDEX fixture_retained_sentinel_ticket ON fixture_retained_sentinel(TicketKey);
                """,
                ("@at", RetainedAt.ToString("O")),
                ("@description", "Retained source description\nwith a second line."),
                ("@runId", creation.Run.Id),
                ("@payload", new byte[] { 0, 1, 127, 128, 255 }));
        }

        return new RetainedAuthoring(
            await store.GetProcessorModeAsync("jira-fhir"),
            Assert.IsType<AuthoringRunRecord>(await store.GetRunAsync(creation.Run.Id)),
            await store.GetRunItemsAsync(creation.Run.Id),
            receipts,
            await store.GetRunInputProvenanceAsync(creation.Run.Id),
            await store.GetRunStagesAsync(creation.Run.Id),
            partition,
            groupingReceipt);
    }

    private static JiraIssueSummaryEntry SourceEntry(string key) => new()
    {
        Key = key,
        ProjectKey = "FHIR",
        Title = $"Title {key}",
        Status = "Resolved - change required",
        WorkGroup = "FHIR Infrastructure",
        Type = "Change Request",
        Specification = "FHIR",
        UpdatedAt = key == "FHIR-103" ? null : RetainedAt,
    };

    private static PlannedTicketPayload PopulatedPlan(string key) => new()
    {
        Key = key,
        Resolution = "Persuasive",
        ResolutionSummary = $"Summary {key}",
        FeatureProposal = $"Proposal {key}",
        DesignRationale = $"Rationale {key}",
        SavedAt = RetainedAt,
        Repos =
        [
            new() { RepoKey = "HL7/fhir", RepoRevision = "abc123", Justification = "primary specification" },
            new() { RepoKey = "HL7/fhir-extensions", RepoRevision = null, Justification = "companion repository" },
        ],
        RepoChanges =
        [
            new()
            {
                TicketRepoId = $"repo-{key}", RepoKey = "HL7/fhir", ChangeSequence = 2,
                FilePath = "source/observation.html", ChangeTitle = $"Change {key}",
                ChangeDescription = "Retained change details.", SourceLineStart = 12, SourceLineEnd = 14,
                ReplacementLines = ["<p>replacement</p>", "", "second line"], Reason = "Normative clarification.",
            },
        ],
        RepoImpacts =
        [
            new()
            {
                TicketRepoId = $"repo-{key}", RepoKey = "HL7/fhir", TicketRepoChangeId = $"change-{key}",
                AffectedFilePath = "source/observation-mappings.html", HowAffected = "Update the mapping.",
            },
        ],
        ChangeValidations =
        [
            new() { TicketRepoId = $"repo-{key}", RepoKey = "HL7/fhir", ValidationSequence = 3, Action = "Validate examples." },
        ],
        TestingConsiderations =
        [
            new() { TicketRepoId = $"repo-{key}", RepoKey = "HL7/fhir", ConsiderationSequence = 4, Consideration = "Check round-trip behavior." },
        ],
        OpenQuestions =
        [
            new() { TicketRepoId = $"repo-{key}", RepoKey = "HL7/fhir", QuestionSequence = 5, Question = "What about older versions?" },
        ],
    };

    private static HydrationBatch PopulatedHydration(string key) => new(
        key,
        new HydrationTicketRow(
            key, "Major", "Persuasive", "Resolution prose", "FHIR", "5.0.0", "2026 ballot",
            "Clarification", "Compatible, substantive", "retained,synthetic", 8, $"Description {key}",
            RetainedAt, "resolved", "synthetic parent",
            DescriptionHtml: $"<p>Description {key}</p>", ResolutionDescriptionHtml: "<p>Resolution</p>",
            Reporter: "Synthetic Reporter", CreatedAt: RetainedAt.AddDays(-30),
            RelatedArtifactsRaw: "Observation", RelatedPagesRaw: "observation.html"),
        [
            new HydrationJiraRow(
                key, key, $"Title {key}", "Resolved - change required", "Change Request", "Major",
                "Persuasive", "Resolution prose", "FHIR Infrastructure", "FHIR",
                SourceEntry(key).UpdatedAt, $"https://synthetic.invalid/{key}", RetainedAt, "resolved", null,
                DescriptionHtml: "<p>Self description</p>", ResolutionDescriptionHtml: "<p>Self resolution</p>",
                Reporter: "Synthetic Reporter", CreatedAt: RetainedAt.AddDays(-30),
                RelatedArtifactsRaw: "Observation", RelatedPagesRaw: "observation.html"),
            new HydrationJiraRow(
                key, "FHIR-900", "Linked ticket", "Triaged", "Bug", "Minor", null, null,
                "FHIR Infrastructure", "FHIR", RetainedAt.AddDays(-1), "https://synthetic.invalid/FHIR-900",
                RetainedAt, "resolved", "synthetic linked ticket",
                DescriptionHtml: "<p>Linked description</p>", Reporter: "Synthetic Linked Reporter"),
        ],
        [
            new HydrationZulipRow(
                key, "implementers:retained", 11, "implementers", "retained", 3,
                RetainedAt.AddDays(-2), RetainedAt.AddDays(-1), "First retained message",
                "https://synthetic.invalid/chat", RetainedAt, "resolved", null),
        ],
        [
            new HydrationGitHubRow(
                key, "HL7/fhir#81", "HL7", "fhir", 81, "source/observation.html", "Retained pull request",
                "closed", true, "specification", RetainedAt.AddDays(-1),
                "https://synthetic.invalid/pull/81", RetainedAt, "resolved", null),
        ],
        [
            new HydrationRepoRow(
                key, "HL7/fhir", "FHIR core", "FHIR Infrastructure", "FHIR", "FhirCore",
                "https://synthetic.invalid/HL7/fhir", RetainedAt, "resolved", null),
        ],
        [new HydrationJiraXrefRow(key, "FHIR-900", "DuplicateOf")]);

    private static void SeedHandwrittenLegacySource(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE jira_processing_source_tickets(
                RowId INTEGER UNIQUE PRIMARY KEY NOT NULL,
                Id TEXT UNIQUE NOT NULL,
                Key TEXT NOT NULL,
                Title TEXT NOT NULL,
                Description TEXT,
                Project TEXT NOT NULL,
                Status TEXT NOT NULL,
                WorkGroup TEXT NOT NULL,
                Type TEXT NOT NULL,
                Specification TEXT NOT NULL DEFAULT '',
                SourceTicketShape TEXT NOT NULL,
                LastSyncedAt TEXT NOT NULL,
                LastUpdated TEXT,
                SourceProjectLastSuccessfulRefreshAt TEXT,
                SourceContentRevision INTEGER,
                StartedProcessingAt TEXT,
                CompletedProcessingAt TEXT,
                LastProcessingAttemptAt TEXT,
                ProcessingStatus TEXT,
                ProcessingError TEXT,
                ProcessingAttemptCount INTEGER NOT NULL,
                ErrorMessage TEXT,
                AgentExitCode INTEGER,
                ErrorOccurredAt TEXT);
            CREATE UNIQUE INDEX idx_jira_processing_source_tickets_key_shape
                ON jira_processing_source_tickets(Key COLLATE NOCASE, SourceTicketShape COLLATE NOCASE);
            """);
        foreach (int number in new[] { 101, 102, 103, 104 })
        {
            string key = $"FHIR-{number}";
            bool error = number == 104;
            Execute(connection, """
                INSERT INTO jira_processing_source_tickets(
                    RowId, Id, Key, Title, Description, Project, Status, WorkGroup, Type, Specification,
                    SourceTicketShape, LastSyncedAt, LastUpdated, SourceProjectLastSuccessfulRefreshAt,
                    SourceContentRevision, StartedProcessingAt, CompletedProcessingAt, LastProcessingAttemptAt,
                    ProcessingStatus, ProcessingError, ProcessingAttemptCount, ErrorMessage, AgentExitCode, ErrorOccurredAt)
                VALUES(
                    @rowId, @id, @key, @title, @description, 'FHIR', 'Resolved - change required',
                    'FHIR Infrastructure', 'Change Request', 'FHIR', 'fhir', @at, @updated, @refresh,
                    731, @at, @completed, @at, @status, @error, @attempts, @message, @exitCode, @errorAt);
                """,
                ("@rowId", number + 1000), ("@id", $"retained-source-{number}"), ("@key", key),
                ("@title", $"Title {key}"), ("@description", $"Retained source description {key}\nsecond line."),
                ("@at", RetainedAt.ToString("O")),
                ("@updated", SourceEntry(key).UpdatedAt?.ToString("O")),
                ("@refresh", RetainedAt.AddHours(-1).ToString("O")),
                ("@completed", error ? null : RetainedAt.AddHours(1).ToString("O")),
                ("@status", error ? "error" : "complete"),
                ("@error", error ? "retained processing error" : null),
                ("@attempts", error ? 4 : 2),
                ("@message", error ? "retained agent error" : null),
                ("@exitCode", error ? 17 : null),
                ("@errorAt", error ? RetainedAt.ToString("O") : null));
        }
    }

    private static void CopyCompanionState(string donorPath, SqliteConnection target)
    {
        using SqliteConnection donor = OpenConn(donorPath);
        SqliteRows schema = ReadTypedRows(donor,
            "SELECT type, name, tbl_name, sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY type, name");
        Assert.DoesNotContain(SourceTable, CompanionTables);
        foreach (string table in CompanionTables)
        {
            object?[] definition = Assert.Single(schema.Values,
                row => Equals(row[0], "table") && Equals(row[1], table));
            Execute(target, Assert.IsType<string>(definition[3]));
            SqliteRows rows = ReadTable(donor, table);
            foreach (object?[] values in rows.Values)
            {
                string[] parameters = Enumerable.Range(0, rows.Columns.Length).Select(index => $"@p{index}").ToArray();
                Execute(target,
                    $"INSERT INTO {Quote(table)} ({string.Join(", ", rows.Columns.Select(Quote))}) " +
                    $"VALUES ({string.Join(", ", parameters)})",
                    parameters.Select((name, index) => (name, values[index])).ToArray());
            }
        }
        // Receipt projection and other constraints must not fire while copying
        // retained rows. Attach only companion indices/triggers after all seeds.
        foreach (object?[] row in schema.Values.Where(row =>
                     (Equals(row[0], "index") || Equals(row[0], "trigger")) &&
                     CompanionTables.Contains(Assert.IsType<string>(row[2]), StringComparer.Ordinal)))
        {
            Execute(target, Assert.IsType<string>(row[3]));
        }
    }

    private static async Task AssertPopulatedApisAsync(
        PlannerDatabase database,
        RetainedAuthoring expected,
        bool hasCompletionIds)
    {
        foreach (string key in AuthoredKeys)
        {
            PlannedTicketPayload payload = PopulatedPlan(key);
            PlannedTicketDetail detail = Assert.IsType<PlannedTicketDetail>(
                await database.GetPlannedTicketAsync(key));
            Assert.Equal(new PlannedTicketSummary(
                key, payload.Resolution, payload.ResolutionSummary, payload.FeatureProposal,
                payload.DesignRationale, RetainedAt), detail.Ticket);
            Assert.Equal(
                payload.Repos.Select(repo => new PlannedTicketRepoItem(repo.RepoKey, repo.RepoRevision, repo.Justification)),
                detail.Repos);
            PlannedTicketRepoChangeItem change = Assert.Single(detail.RepoChanges);
            Assert.Equal($"repo-{key}", change.TicketRepoId);
            Assert.Equal("HL7/fhir", change.RepoKey);
            Assert.Equal("source/observation.html", change.FilePath);
            Assert.Equal(2, change.ChangeSequence);
            Assert.Equal(12, change.SourceLineStart);
            Assert.Equal(14, change.SourceLineEnd);
            Assert.Equal(payload.RepoChanges[0].ReplacementLines, change.ReplacementLines);
            Assert.Equal("Update the mapping.", Assert.Single(detail.RepoImpacts).HowAffected);
            Assert.Equal($"change-{key}", detail.RepoImpacts[0].TicketRepoChangeId);
            Assert.Equal("Validate examples.", Assert.Single(detail.ChangeValidations).Action);
            Assert.Equal("Check round-trip behavior.", Assert.Single(detail.TestingConsiderations).Consideration);
            Assert.Equal("What about older versions?", Assert.Single(detail.OpenQuestions).Question);

            PlannedTicketHydrationReadModel hydration = Assert.IsType<PlannedTicketHydrationReadModel>(
                await database.GetHydrationAsync(key));
            Assert.NotNull(hydration.Parent);
            Assert.Equal($"Description {key}", hydration.Parent.DescriptionPlain);
            Assert.Equal($"<p>Description {key}</p>", hydration.Parent.DescriptionHtml);
            Assert.Equal("<p>Resolution</p>", hydration.Parent.ResolutionDescriptionHtml);
            Assert.Equal(8, hydration.Parent.CommentCount);
            Assert.Equal("Synthetic Reporter", hydration.Parent.Reporter);
            Assert.Equal(RetainedAt.AddDays(-30), hydration.Parent.CreatedAt);
            Assert.Equal("Observation", hydration.Parent.RelatedArtifactsRaw);
            Assert.Equal("observation.html", hydration.Parent.RelatedPagesRaw);
            Assert.Equal(2, hydration.JiraRows.Count);
            Assert.Equal("FHIRInfrastructure", Assert.Single(hydration.JiraRows, row => row.JiraKey == key).WorkGroupClean);
            Assert.Equal("Linked ticket", Assert.Single(hydration.JiraRows, row => row.JiraKey == "FHIR-900").Title);
            Assert.Equal("First retained message", Assert.Single(hydration.ZulipRows).FirstMessageExcerpt);
            Assert.True(Assert.Single(hydration.GitHubRows).IsPullRequest);
            Assert.Equal("FhirCore", Assert.Single(hydration.RepoRows).CategoryDetail);
            Assert.Equal(new PlannedTicketJiraXrefRow(key, "FHIR-900", "DuplicateOf"), Assert.Single(hydration.JiraXrefRows));
            Assert.Equal(["FHIR-900"], await database.ListRelatedJiraKeysForTicketAsync(key, CancellationToken.None));
            Assert.Equal(["implementers:retained"], await database.ListRelatedZulipThreadIdsForTicketAsync(key, CancellationToken.None));
            Assert.Equal(["HL7/fhir#81"], await database.ListRelatedGitHubItemIdsForTicketAsync(key, CancellationToken.None));
        }

        PlannedTicketTopicsForCategory topics = Assert.IsType<PlannedTicketTopicsForCategory>(
            await database.GetWorkGroupTopicsAsync("FHIRInfrastructure", "FHIR", "Change Request"));
        PlannedTicketTopicDetail topic = Assert.Single(topics.Topics);
        Assert.Equal("Retained coordinated change", topic.ShortDescription);
        Assert.Equal("Retained grouping rationale and ordering.", topic.LongerDescription);
        Assert.Equal(7, topic.RenderOrderHint);
        Assert.Equal(["HL7/fhir", "HL7/fhir-extensions"], topic.SpannedRepos);
        PlannedTicketTopicGroup group = Assert.Single(topic.LinkedTicketGroups);
        Assert.Equal("FHIR-101", group.FirstTicketKey);
        Assert.Equal("The two tickets share an artifact.", group.Rationale);
        Assert.Equal(
            [new PlannedTicketTopicGroupMember("FHIR-101", 0), new PlannedTicketTopicGroupMember("FHIR-102", 1)],
            group.Members);
        Assert.Equal(["FHIR-103"], topic.RemainingTicketKeys);

        PlannedTicketClusteringSignals signals = Assert.IsType<PlannedTicketClusteringSignals>(
            await database.GetClusteringSignalsAsync("FHIRInfrastructure"));
        foreach (string key in AuthoredKeys)
        {
            PlannedTicketClusteringSignal signal = Assert.Single(signals.Tickets, row => row.IssueKey == key);
            Assert.True(signal.HasPlannedTicket);
            Assert.Equal("resolved", signal.HydrationStatus);
            Assert.Equal($"Summary {key}", signal.ResolutionSummary);
            Assert.Equal($"Proposal {key}", signal.FeatureProposal);
            Assert.Equal($"Rationale {key}", signal.DesignRationale);
            Assert.Equal(["HL7/fhir", "HL7/fhir-extensions"], signal.Repos);
            Assert.Equal(new PlannedTicketClusteringRepoChange("HL7/fhir", "source/observation.html"), Assert.Single(signal.RepoChanges));
            Assert.Equal(new PlannedTicketClusteringRepoImpact("HL7/fhir", "source/observation-mappings.html"), Assert.Single(signal.RepoImpacts));
        }

        AuthoringRunStore store = new(database);
        Assert.Equal(expected.Mode, await store.GetProcessorModeAsync("jira-fhir"));
        Assert.Equal(expected.Run, await store.GetRunAsync(expected.Run.Id));
        Assert.Equal(expected.Items, await store.GetRunItemsAsync(expected.Run.Id));
        Assert.Equal(expected.Provenance, await store.GetRunInputProvenanceAsync(expected.Run.Id));
        Assert.Equal(expected.Stages, await store.GetRunStagesAsync(expected.Run.Id));
        Assert.Equal(expected.GroupingReceipt, await database.GetGroupingReceiptAsync(
            expected.Run.Id, expected.GroupingReceipt.StageId,
            expected.Partition.PartitionKey, expected.Partition.InputFingerprint));
        PlannedTicketRunPartition partition = Assert.Single(await database.GetRunPartitionsAsync(expected.Run.Id));
        Assert.Equal(expected.Partition.PartitionKey, partition.PartitionKey);
        Assert.Equal(expected.Partition.InputFingerprint, partition.InputFingerprint);
        Assert.Equal(AuthoredKeys, partition.TicketKeys);
        JiraProcessingSourceTicketStore source = new(database.DatabasePath);
        foreach (AuthoringResultReceipt receipt in expected.Receipts)
        {
            Assert.Equal(receipt, await store.GetReceiptByOperationAsync(receipt.OperationId));
            JiraProcessingSourceTicketRecord ticket = Assert.IsType<JiraProcessingSourceTicketRecord>(
                await source.GetByKeyAsync(receipt.BusinessKey, "fhir", CancellationToken.None));
            Assert.Equal(ticket, await source.GetByIdAsync(ticket.Id, CancellationToken.None));
            Assert.Equal("complete", ticket.ProcessingStatus);
            Assert.Equal(receipt.ExpectedSourceRevision, JiraProcessingSourceTicketStore.GetSourceRevision(ticket));
            Assert.Equal(hasCompletionIds ? receipt.ReceiptId : null, ticket.CompletionId);
            Assert.Equal(hasCompletionIds ? receipt.PersistedAt : RetainedAt.AddHours(1), ticket.CompletedProcessingAt);
        }
    }

    private static bool HasCompletionColumn(SqliteConnection connection) =>
        ReadTypedRows(connection, "PRAGMA table_info(jira_processing_source_tickets)")
            .Values.Any(row => Equals(row[1], "CompletionId"));

    private static string Quote(string name) => $"\"{name.Replace("\"", "\"\"")}\"";

    private static SqliteRows ReadTypedRows(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        string[] columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        List<object?[]> rows = [];
        while (reader.Read())
        {
            rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
        }
        return new SqliteRows(columns, rows.ToArray());
    }

    private static SqliteRows ReadTable(SqliteConnection connection, string table, string[]? columns = null)
    {
        string projection = columns is null ? "*" : string.Join(", ", columns.Select(Quote));
        SqliteRows shape = ReadTypedRows(connection, $"SELECT {projection} FROM {Quote(table)} LIMIT 0");
        return ReadTypedRows(connection,
            $"SELECT {projection} FROM {Quote(table)} ORDER BY {string.Join(", ", shape.Columns.Select(Quote))}");
    }

    private static Dictionary<string, SqliteRows> CaptureTables(SqliteConnection connection) =>
        ReadTypedRows(connection,
            "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name")
            .Values.ToDictionary(row => Assert.IsType<string>(row[0]),
                row => ReadTable(connection, Assert.IsType<string>(row[0])), StringComparer.Ordinal);

    private static void AssertRetainedTables(
        IReadOnlyDictionary<string, SqliteRows> expected,
        SqliteConnection connection)
    {
        Assert.Equal(expected.Keys, CaptureTables(connection).Keys);
        foreach ((string table, SqliteRows rows) in expected)
        {
            SqliteRows actual = ReadTable(connection, table);
            if (table == SourceTable && !rows.Columns.Contains("CompletionId", StringComparer.Ordinal))
            {
                Assert.Equal(rows.Columns.Append("CompletionId"), actual.Columns);
                actual = ReadTable(connection, table, rows.Columns);
            }
            Assert.Equal(rows.Columns, actual.Columns);
            Assert.Equal(rows.Values.Length, actual.Values.Length);
            for (int row = 0; row < rows.Values.Length; row++)
            {
                for (int column = 0; column < rows.Columns.Length; column++)
                {
                    object? value = rows.Values[row][column];
                    object? retained = actual.Values[row][column];
                    Assert.Equal(value?.GetType(), retained?.GetType());
                    if (value is byte[] bytes)
                    {
                        Assert.Equal(bytes, Assert.IsType<byte[]>(retained));
                    }
                    else
                    {
                        Assert.True(Equals(value, retained), $"Retained {table}.{rows.Columns[column]} changed at row {row}.");
                    }
                }
            }
        }
    }

    private static void AssertPopulatedCompanions(IReadOnlyDictionary<string, SqliteRows> tables)
    {
        string[] intentionallyEmpty =
        [
            "authoring_mutation_fences", "authoring_review_snapshots",
            "authoring_revalidation_lineage", "planned_ticket_applier_projection_pending",
        ];
        foreach (string table in CompanionTables.Except(intentionallyEmpty))
        {
            Assert.NotEmpty(tables[table].Values);
        }
    }

    private static void InstallPreservationGuards(SqliteConnection connection, IEnumerable<string> tables)
    {
        foreach (string table in tables)
        {
            foreach (string operation in new[] { "UPDATE", "DELETE" })
            {
                Execute(connection, $"""
                    CREATE TRIGGER {Quote($"fixture_no_{operation}_{table}")}
                    BEFORE {operation} ON {Quote(table)}
                    BEGIN SELECT RAISE(ABORT, 'initializer changed retained fixture data'); END;
                    """);
            }
        }
    }

    private static Dictionary<string, byte[]> CreateImmutableSentinels(string databasePath)
    {
        string directory = System.IO.Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("Fixture directory missing.");
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal)
        {
            [System.IO.Path.Combine(directory, "retained.backup.db")] = [0, 1, 2, 255, 128, 17],
            [System.IO.Path.Combine(directory, "immutable.snapshot.db")] = [7, 0, 8, 0, 9, 255],
            [System.IO.Path.Combine(directory, "immutable.snapshot.json")] =
                """{"snapshotId":"synthetic-retained","immutable":true}"""u8.ToArray(),
        };
        foreach ((string path, byte[] bytes) in files)
        {
            File.WriteAllBytes(path, bytes);
        }
        return files;
    }

    private static void AssertImmutableSentinels(IReadOnlyDictionary<string, byte[]> files)
    {
        foreach ((string path, byte[] bytes) in files)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
    }

    private static void Execute(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }

    // --- helpers ---

    private static SqliteConnection OpenConn(string path)
    {
        SqliteConnection conn = new($"Data Source={path};Pooling=False");
        conn.Open();
        return conn;
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", name);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool IndexExists(SqliteConnection conn, string name)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name=@n";
        cmd.Parameters.AddWithValue("@n", name);
        return cmd.ExecuteScalar() is not null;
    }

    private static int ScalarCount(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task SeedSourceTicketAsync(
        PlannerDatabase database,
        string key,
        string workGroupDisplay,
        string type,
        string specification)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using SqliteConnection connection = database.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jira_processing_source_tickets
                (Id, Key, Title, Description, Project, Status, WorkGroup, Type, Specification, SourceTicketShape,
                 LastSyncedAt, LastUpdated, StartedProcessingAt, CompletedProcessingAt, LastProcessingAttemptAt,
                 ProcessingStatus, ProcessingError, ProcessingAttemptCount,
                 CompletionId, ErrorMessage, AgentExitCode, ErrorOccurredAt)
            VALUES
                (@Id, @Key, @Title, NULL, @Project, @Status, @WorkGroup, @Type, @Specification, @SourceTicketShape,
                 @LastSyncedAt, NULL, NULL, NULL, NULL,
                 NULL, NULL, 0,
                 NULL, NULL, NULL, NULL)
            """;
        command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@Key", key);
        command.Parameters.AddWithValue("@Title", $"title-{key}");
        command.Parameters.AddWithValue("@Project", "FHIR");
        command.Parameters.AddWithValue("@Status", "Open");
        command.Parameters.AddWithValue("@WorkGroup", workGroupDisplay);
        command.Parameters.AddWithValue("@Type", type);
        command.Parameters.AddWithValue("@Specification", specification);
        command.Parameters.AddWithValue("@SourceTicketShape", "fhir");
        command.Parameters.AddWithValue("@LastSyncedAt", now.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedHydrationSelfRowAsync(
        PlannerDatabase database,
        string issueKey,
        string workGroupDisplay,
        string type,
        string specification,
        string? title = "title",
        string? status = "Open",
        string hydrationStatus = "resolved")
    {
        string cleaned = FhirAugury.Common.WorkGroups.Hl7WorkGroupNameCleaner.Clean(workGroupDisplay);
        await InsertHydrationRowAsync(
            database,
            issueKey: issueKey,
            jiraKey: issueKey,
            workGroupDisplay: workGroupDisplay,
            workGroupClean: string.IsNullOrEmpty(cleaned) ? null : cleaned,
            type: type,
            specification: specification,
            title: title,
            status: status,
            hydrationStatus: hydrationStatus);
    }

    private static async Task SeedHydrationLinkedRowAsync(
        PlannerDatabase database,
        string issueKey,
        string jiraKey,
        string workGroupDisplay,
        string hydrationStatus = "resolved")
    {
        string cleaned = FhirAugury.Common.WorkGroups.Hl7WorkGroupNameCleaner.Clean(workGroupDisplay);
        await InsertHydrationRowAsync(
            database,
            issueKey: issueKey,
            jiraKey: jiraKey,
            workGroupDisplay: workGroupDisplay,
            workGroupClean: string.IsNullOrEmpty(cleaned) ? null : cleaned,
            type: null,
            specification: null,
            title: $"linked-{jiraKey}",
            status: null,
            hydrationStatus: hydrationStatus);
    }

    private static async Task InsertHydrationRowAsync(
        PlannerDatabase database,
        string issueKey,
        string jiraKey,
        string workGroupDisplay,
        string? workGroupClean,
        string? type,
        string? specification,
        string? title,
        string? status,
        string hydrationStatus)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using SqliteConnection connection = database.OpenConnection();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO planned_jira_hydration
                (IssueKey, JiraKey, Title, Status, Type, Priority, Resolution, ResolutionDescriptionPlain,
                 WorkGroup, WorkGroupClean, Specification, UpdatedAt, Url, HydratedAt, HydrationStatus, HydrationReason)
            VALUES
                (@IssueKey, @JiraKey, @Title, @Status, @Type, NULL, NULL, NULL,
                 @WorkGroup, @WorkGroupClean, @Specification, NULL, @Url, @HydratedAt, @HydrationStatus, NULL)
            """;
        command.Parameters.AddWithValue("@IssueKey", issueKey);
        command.Parameters.AddWithValue("@JiraKey", jiraKey);
        command.Parameters.AddWithValue("@Title", (object?)title ?? DBNull.Value);
        command.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("@Type", (object?)type ?? DBNull.Value);
        command.Parameters.AddWithValue("@WorkGroup", workGroupDisplay);
        command.Parameters.AddWithValue("@WorkGroupClean", (object?)workGroupClean ?? DBNull.Value);
        command.Parameters.AddWithValue("@Specification", (object?)specification ?? DBNull.Value);
        command.Parameters.AddWithValue("@Url", $"https://jira.example/{jiraKey}");
        command.Parameters.AddWithValue("@HydratedAt", now.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@HydrationStatus", hydrationStatus);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class DatabaseFixture : IDisposable
    {
        public string Path { get; }
        public PlannerDatabase Database { get; }

        public DatabaseFixture(Action<string>? beforeInitialize = null)
        {
            string dir = System.IO.Path.Combine(Environment.CurrentDirectory, "temp", "planner-persistence", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            _dir = dir;
            Path = System.IO.Path.Combine(dir, "planner.db");
            Database = new PlannerDatabase(Path, NullLogger<PlannerDatabase>.Instance);
            try
            {
                beforeInitialize?.Invoke(Path);
                Database.Initialize();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private readonly string _dir;

        public void Dispose()
        {
            Database.Dispose();
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
    }
}
