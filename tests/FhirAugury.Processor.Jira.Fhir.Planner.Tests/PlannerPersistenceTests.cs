using FhirAugury.Processor.Jira.Fhir.Hydration.Common;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Contracts;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Database;
using FhirAugury.Processor.Jira.Fhir.Planner.Persistence.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Processor.Jira.Fhir.Planner.Tests;

public sealed class PlannerPersistenceTests
{
    [Fact]
    public void EnsureSchema_CreatesAllHydrationAndTopicTables()
    {
        using DatabaseFixture fixture = new();
        SqliteConnection conn = OpenConn(fixture.Path);

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

    // --- helpers ---

    private static SqliteConnection OpenConn(string path)
    {
        SqliteConnection conn = new($"Data Source={path}");
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

        public DatabaseFixture()
        {
            string dir = System.IO.Path.Combine(Environment.CurrentDirectory, "temp", "planner-persistence", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            Path = System.IO.Path.Combine(dir, "planner.db");
            Database = new PlannerDatabase(Path, NullLogger<PlannerDatabase>.Instance);
            Database.Initialize();
            _dir = dir;
        }

        private readonly string _dir;

        public void Dispose()
        {
            Database.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }
    }
}
