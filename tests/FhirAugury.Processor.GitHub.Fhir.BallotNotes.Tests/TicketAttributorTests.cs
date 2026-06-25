using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Attribution;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Configuration;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

public sealed class TicketAttributorTests : IDisposable
{
    private const int OrchestratorPort = 5150;
    private const int JiraPort = 5160;

    private readonly string _tempDir;

    public TicketAttributorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ticketattr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    [Fact]
    public void ExtractTicketKeys_extracts_dedupes_and_preserves_order()
    {
        IReadOnlyList<string> keys = TicketAttributor.ExtractTicketKeys(
            "FHIR-100 fixes things; see fhir-100 and FHIR-205. Unrelated ABC-1.");

        Assert.Equal(["FHIR-100", "FHIR-205"], keys);
    }

    [Fact]
    public void ExtractTicketKeys_returns_empty_for_no_match()
        => Assert.Empty(TicketAttributor.ExtractTicketKeys("no tickets here"));

    [Fact]
    public void ExtractTicketKeys_extracts_up_and_upsm_distinctly()
    {
        IReadOnlyList<string> keys = TicketAttributor.ExtractTicketKeys(
            "Terminology UP-796 and UPSM-411 referenced; ignore UTF-8 and ABC-1.");

        Assert.Equal(["UP-796", "UPSM-411"], keys);
    }

    [Fact]
    public async Task AttributeAsync_attributes_up_and_upsm_cross_ref_hits()
    {
        TicketAttributor attributor = BuildAttributor(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("cross-referenced", StringComparison.Ordinal)
                ? """{"value":"x","direction":"cross-referenced","total":2,"hits":[{"sourceType":"github","sourceId":"abc1234def5678abc1234def5678abc1234def56","targetType":"jira","targetId":"UP-796"},{"sourceType":"github","sourceId":"abc1234def5678abc1234def5678abc1234def56","targetType":"jira","targetId":"UPSM-411"}]}"""
                : """{"source":"jira","id":"x","title":"A title","url":"http://jira/x","metadata":{}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        UnitAttribution result = await attributor.AttributeAsync(
            [Commit("Terminology cleanup", "")],
            workGroupHint: null);

        Assert.Contains(result.Tickets, t => t.Key == "UP-796");
        Assert.Contains(result.Tickets, t => t.Key == "UPSM-411");
    }

    [Fact]
    public void SelectOwningWorkGroup_picks_most_recently_attributed()
    {
        List<AttributedTicket> tickets =
        [
            new() { Key = "FHIR-1", WorkGroup = "Old WG", AttributionDate = Date("2026-01-01") },
            new() { Key = "FHIR-2", WorkGroup = "Newest WG", AttributionDate = Date("2026-06-01") },
            new() { Key = "FHIR-3", WorkGroup = "Middle WG", AttributionDate = Date("2026-03-01") },
        ];

        (string workGroup, string workGroupCode) = TicketAttributor.SelectOwningWorkGroup(tickets, hint: null);

        Assert.Equal("Newest WG", workGroup);
        Assert.Equal("NewestWG", workGroupCode);
    }

    [Fact]
    public void SelectOwningWorkGroup_ignores_tickets_without_workgroup()
    {
        List<AttributedTicket> tickets =
        [
            new() { Key = "FHIR-1", WorkGroup = "Has WG", AttributionDate = Date("2026-01-01") },
            new() { Key = "FHIR-2", WorkGroup = "", AttributionDate = Date("2026-06-01") },
        ];

        (string workGroup, _) = TicketAttributor.SelectOwningWorkGroup(tickets, hint: null);

        Assert.Equal("Has WG", workGroup);
    }

    [Fact]
    public void SelectOwningWorkGroup_falls_back_to_hint_when_no_tickets()
    {
        (string workGroup, string workGroupCode) =
            TicketAttributor.SelectOwningWorkGroup([], hint: "FHIR Infrastructure");

        Assert.Equal("FHIR Infrastructure", workGroup);
        Assert.Equal("FHIRInfrastructure", workGroupCode);
    }

    [Fact]
    public async Task AttributeAsync_enriches_via_orchestrator_first_and_dedupes()
    {
        ConcurrentBag<int> hitPorts = [];
        TicketAttributor attributor = BuildAttributor(req =>
        {
            hitPorts.Add(req.RequestUri!.Port);
            return RespondOk(req); // orchestrator (and jira) both healthy
        });

        UnitAttribution result = await attributor.AttributeAsync(
            [Commit("FHIR-100 work", "also FHIR-100 and FHIR-205")],
            workGroupHint: null);

        Assert.Equal(["FHIR-100", "FHIR-205", "FHIR-300"], result.Tickets.Select(t => t.Key));
        // Orchestrator-first: the Jira-source port must never be contacted while
        // the orchestrator answers.
        Assert.All(hitPorts, port => Assert.Equal(OrchestratorPort, port));
    }

    [Fact]
    public async Task AttributeAsync_falls_back_to_jira_when_orchestrator_unreachable()
    {
        ConcurrentBag<int> hitPorts = [];
        TicketAttributor attributor = BuildAttributor(req =>
        {
            hitPorts.Add(req.RequestUri!.Port);
            return req.RequestUri!.Port == OrchestratorPort
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : RespondOk(req);
        });

        UnitAttribution result = await attributor.AttributeAsync(
            [Commit("FHIR-100 work", "")],
            workGroupHint: null);

        Assert.Contains(result.Tickets, t => t.Key == "FHIR-300"); // cross-ref enrichment via fallback
        Assert.Contains(JiraPort, hitPorts);
    }

    [Fact]
    public async Task AttributeAsync_is_best_effort_when_all_upstreams_fail()
    {
        TicketAttributor attributor = BuildAttributor(
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        UnitAttribution result = await attributor.AttributeAsync(
            [Commit("FHIR-100 and FHIR-205", "")],
            workGroupHint: "Fallback WG");

        // Only message-derived keys survive; no cross-ref enrichment, no details.
        Assert.Equal(["FHIR-100", "FHIR-205"], result.Tickets.Select(t => t.Key));
        Assert.All(result.Tickets, t => Assert.Equal(string.Empty, t.Title));
        Assert.Equal("Fallback WG", result.WorkGroup);
    }

    [Fact]
    public async Task AttributeAsync_reads_change_impact_and_category_from_metadata()
    {
        TicketAttributor attributor = BuildAttributor(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("cross-referenced", StringComparison.Ordinal)
                ? """{"value":"x","total":0,"hits":[]}"""
                : """{"source":"jira","id":"FHIR-56060","title":"A title","url":"http://jira/x","metadata":{"work_group":"Orders and Observations (OO)","type":"Technical Correction","change_impact":"Non-substantive","change_category":"Clarification"}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        UnitAttribution result = await attributor.AttributeAsync(
            [Commit("FHIR-56060 clarify wording", "")],
            workGroupHint: null);

        AttributedTicket ticket = Assert.Single(result.Tickets);
        Assert.Equal("FHIR-56060", ticket.Key);
        Assert.Equal("Non-substantive", ticket.ChangeImpact);
        Assert.Equal("Clarification", ticket.ChangeCategory);
        Assert.Equal("Technical Correction", ticket.IssueType);
    }

    [Fact]
    public async Task AttributeAsync_parses_related_ticket_keys_from_metadata_links()
    {
        TicketAttributor attributor = BuildAttributor(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("cross-referenced", StringComparison.Ordinal)
                ? """{"value":"x","total":0,"hits":[]}"""
                : """{"source":"jira","id":"FHIR-100","title":"A title","url":"http://jira/x","metadata":{"related_issues":"FHIR-200, FHIR-300","duplicate_of":"FHIR-400","change_impact":"Compatible, substantive"}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        UnitAttribution result = await attributor.AttributeAsync(
            [Commit("FHIR-100 change", "")],
            workGroupHint: null);

        AttributedTicket ticket = Assert.Single(result.Tickets);
        Assert.Equal("FHIR-200;FHIR-300;FHIR-400", ticket.RelatedTicketKeys);
    }

    [Fact]
    public async Task AttributeAsync_excludes_self_from_related_ticket_keys()
    {
        TicketAttributor attributor = BuildAttributor(req =>
        {
            string path = req.RequestUri!.AbsolutePath;
            string json = path.Contains("cross-referenced", StringComparison.Ordinal)
                ? """{"value":"x","total":0,"hits":[]}"""
                : """{"source":"jira","id":"FHIR-100","title":"t","url":"http://jira/x","metadata":{"related_issues":"FHIR-100, FHIR-200"}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

        UnitAttribution result = await attributor.AttributeAsync([Commit("FHIR-100 change", "")], workGroupHint: null);

        Assert.Equal("FHIR-200", Assert.Single(result.Tickets).RelatedTicketKeys);
    }

    [Fact]
    public async Task AttributeAsync_attributes_pr_ticket_for_unattributed_commit()
    {
        string db = SeedGithubDb(
            links: [("sha-clean1", 4163, "HL7/fhir")],
            prs: [("HL7/fhir#4163", true, "PR fixes FHIR-4242", "details")]);

        TicketAttributor attributor = BuildAttributor(NoCrossRefResponder, db);

        UnitAttribution result = await attributor.AttributeAsync(
            [Commit("sha-clean1", "tidy whitespace", "no ticket here", "2026-06-01T00:00:00+00:00")],
            workGroupHint: null);

        Assert.Contains(result.Tickets, t => t.Key == "FHIR-4242");
        Assert.Contains("FHIR-4242", result.CommitTicketKeys["sha-clean1"]);
    }

    [Fact]
    public async Task AttributeAsync_does_not_pr_fallback_when_commit_names_ticket()
    {
        string db = SeedGithubDb(
            links: [("sha-named", 4163, "HL7/fhir")],
            prs: [("HL7/fhir#4163", true, "PR fixes FHIR-999", "details")]);

        TicketAttributor attributor = BuildAttributor(NoCrossRefResponder, db);

        UnitAttribution result = await attributor.AttributeAsync(
            [Commit("sha-named", "FHIR-100 fix", "", "2026-06-01T00:00:00+00:00")],
            workGroupHint: null);

        Assert.Contains(result.Tickets, t => t.Key == "FHIR-100");
        Assert.DoesNotContain(result.Tickets, t => t.Key == "FHIR-999");
    }

    [Fact]
    public async Task AttributeAsync_counts_pr_ticket_once_per_pr()
    {
        string db = SeedGithubDb(
            links: [("sha-g1", 4163, "HL7/fhir"), ("sha-g2", 4163, "HL7/fhir")],
            prs: [("HL7/fhir#4163", true, "PR fixes FHIR-4242", "details")]);

        TicketAttributor attributor = BuildAttributor(NoCrossRefResponder, db);

        UnitAttribution result = await attributor.AttributeAsync(
            [
                Commit("sha-g1", "tidy a", "", "2026-06-01T00:00:00+00:00"),
                Commit("sha-g2", "tidy b", "", "2026-06-02T00:00:00+00:00"),
            ],
            workGroupHint: null);

        AttributedTicket ticket = Assert.Single(result.Tickets, t => t.Key == "FHIR-4242");
        Assert.Equal(1, ticket.CommitCount);
    }

    [Fact]
    public async Task AttributeAsync_pr_ticket_attribution_date_is_latest_contributing_commit()
    {
        string db = SeedGithubDb(
            links: [("sha-early", 4163, "HL7/fhir"), ("sha-late", 4163, "HL7/fhir")],
            prs: [("HL7/fhir#4163", true, "PR fixes FHIR-4242", "details")]);

        TicketAttributor attributor = BuildAttributor(NoCrossRefResponder, db);

        UnitAttribution result = await attributor.AttributeAsync(
            [
                Commit("sha-early", "tidy a", "", "2026-06-01T00:00:00+00:00"),
                Commit("sha-late", "tidy b", "", "2026-06-09T00:00:00+00:00"),
            ],
            workGroupHint: null);

        AttributedTicket ticket = Assert.Single(result.Tickets, t => t.Key == "FHIR-4242");
        Assert.Equal(Date("2026-06-09"), ticket.AttributionDate);
    }

    [Fact]
    public async Task AttributeAsync_pr_fallback_dedupes_against_existing_keys()
    {
        string db = SeedGithubDb(
            links: [("sha-gap", 4163, "HL7/fhir")],
            prs: [("HL7/fhir#4163", true, "PR fixes FHIR-100", "details")]);

        TicketAttributor attributor = BuildAttributor(NoCrossRefResponder, db);

        UnitAttribution result = await attributor.AttributeAsync(
            [
                Commit("sha-named", "FHIR-100 fix", "", "2026-06-01T00:00:00+00:00"),
                Commit("sha-gap", "tidy", "", "2026-06-02T00:00:00+00:00"),
            ],
            workGroupHint: null);

        // FHIR-100 appears once (named commit + PR gap-fill); count is +1 for the PR.
        Assert.Equal(["FHIR-100"], result.Tickets.Select(t => t.Key));
        Assert.Equal(2, Assert.Single(result.Tickets).CommitCount);
    }

    private string SeedGithubDb(
        IReadOnlyList<(string Sha, int PrNumber, string Repo)> links,
        IReadOnlyList<(string UniqueKey, bool IsPr, string Title, string Body)> prs)
    {
        string dbPath = Path.Combine(_tempDir, "github-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using SqliteConnection conn = new($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using (SqliteCommand create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE github_commit_pr_links (" +
                "Id INTEGER PRIMARY KEY, CommitSha TEXT, PrNumber INTEGER, RepoFullName TEXT);" +
                "CREATE TABLE github_issues (" +
                "Id INTEGER PRIMARY KEY, UniqueKey TEXT, IsPullRequest INTEGER, Title TEXT, Body TEXT);";
            create.ExecuteNonQuery();
        }

        int linkId = 1;
        foreach ((string sha, int prNumber, string repo) in links)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO github_commit_pr_links (Id, CommitSha, PrNumber, RepoFullName) " +
                "VALUES ($id, $sha, $pr, $repo)";
            cmd.Parameters.AddWithValue("$id", linkId++);
            cmd.Parameters.AddWithValue("$sha", sha);
            cmd.Parameters.AddWithValue("$pr", prNumber);
            cmd.Parameters.AddWithValue("$repo", repo);
            cmd.ExecuteNonQuery();
        }

        int issueId = 1;
        foreach ((string uniqueKey, bool isPr, string title, string body) in prs)
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO github_issues (Id, UniqueKey, IsPullRequest, Title, Body) " +
                "VALUES ($id, $key, $pr, $title, $body)";
            cmd.Parameters.AddWithValue("$id", issueId++);
            cmd.Parameters.AddWithValue("$key", uniqueKey);
            cmd.Parameters.AddWithValue("$pr", isPr ? 1 : 0);
            cmd.Parameters.AddWithValue("$title", title);
            cmd.Parameters.AddWithValue("$body", body);
            cmd.ExecuteNonQuery();
        }

        return dbPath;
    }

    // Responds with empty cross-reference hits (so message-less commits stay gaps)
    // and a minimal Jira ticket detail for any item lookup.
    private static HttpResponseMessage NoCrossRefResponder(HttpRequestMessage req)
    {
        string path = req.RequestUri!.AbsolutePath;
        string json = path.Contains("cross-referenced", StringComparison.Ordinal)
            ? """{"value":"x","total":0,"hits":[]}"""
            : """{"source":"jira","id":"x","title":"A title","url":"http://jira/x","metadata":{"work_group":"Orders and Observations (OO)"}}""";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private static WindowCommit Commit(string subject, string body) => new()
    {
        Sha = "abc1234def5678abc1234def5678abc1234def56",
        ShortSha = "abc1234",
        AuthorName = "Dev",
        AuthorDate = "2026-06-01T00:00:00+00:00",
        Subject = subject,
        Body = body,
    };

    private static WindowCommit Commit(string sha, string subject, string body, string authorDate) => new()
    {
        Sha = sha,
        ShortSha = sha[..Math.Min(7, sha.Length)],
        AuthorName = "Dev",
        AuthorDate = authorDate,
        Subject = subject,
        Body = body,
    };

    private static DateTimeOffset Date(string date) => DateTimeOffset.Parse(date + "T00:00:00+00:00");

    private static TicketAttributor BuildAttributor(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string? githubDbPath = null)
    {
        HttpClient client = new(new StubHandler(responder));
        BallotNotesHydrationOptions options = new()
        {
            OrchestratorAddress = $"http://localhost:{OrchestratorPort}",
            JiraSourceAddress = $"http://localhost:{JiraPort}",
        };
        if (githubDbPath is not null) options.GitHubDbPath = githubDbPath;
        return new TicketAttributor(client, Options.Create(options), NullLogger<TicketAttributor>.Instance);
    }

    private static HttpResponseMessage RespondOk(HttpRequestMessage req)
    {
        string path = req.RequestUri!.AbsolutePath;
        string json = path.Contains("cross-referenced", StringComparison.Ordinal)
            ? """{"value":"x","direction":"cross-referenced","total":1,"hits":[{"sourceType":"github","sourceId":"abc1234def5678abc1234def5678abc1234def56","targetType":"jira","targetId":"FHIR-300"}]}"""
            : """{"source":"jira","id":"FHIR-x","title":"A title","url":"http://jira/x","metadata":{"work_group":"Orders and Observations (OO)","specification":"FHIR Core","resolution":"Persuasive"}}""";

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
