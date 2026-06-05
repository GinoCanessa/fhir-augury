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
