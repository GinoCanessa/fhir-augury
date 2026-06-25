using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Git;
using FhirAugury.Processor.GitHub.Fhir.BallotNotes.Hydration.Sources;
using Microsoft.Data.Sqlite;

namespace FhirAugury.Processor.GitHub.Fhir.BallotNotes.Tests;

/// <summary>
/// Exercises <see cref="PrTicketResolver"/> against a seeded throwaway
/// <c>github.db</c>: gap commits resolve to their PR(s), whose title/body Jira
/// keys are harvested once per PR using the known-prefix extraction rules.
/// </summary>
public sealed class PrTicketResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public PrTicketResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "prticket-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "github.db");
        SeedDb();
    }

    public void Dispose() => TestFileCleanup.SafeDeleteDirectory(_tempDir);

    private void SeedDb()
    {
        using SqliteConnection conn = new($"Data Source={_dbPath};Pooling=False");
        conn.Open();
        using (SqliteCommand create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE github_commit_pr_links (" +
                "Id INTEGER PRIMARY KEY, CommitSha TEXT, PrNumber INTEGER, RepoFullName TEXT);" +
                "CREATE TABLE github_issues (" +
                "Id INTEGER PRIMARY KEY, UniqueKey TEXT, RepoFullName TEXT, Number INTEGER, " +
                "IsPullRequest INTEGER, Title TEXT, Body TEXT);";
            create.ExecuteNonQuery();
        }

        // PR 4163 (HL7/fhir): two commits, ticket only in PR title/body.
        Link(conn, 1, "sha-aaa", 4163, "HL7/fhir");
        Link(conn, 2, "sha-bbb", 4163, "HL7/fhir");
        Issue(conn, 1, "HL7/fhir#4163", "HL7/fhir", 4163, isPr: true,
            "Fix FHIR-1234 in Patient", "Body also mentions fhir-1234 once more.");

        // PR 50 (HL7/fhir): linked to a non-PR issue row by the same number space.
        Link(conn, 3, "sha-ccc", 50, "HL7/fhir");
        Issue(conn, 2, "HL7/fhir#50", "HL7/fhir", 50, isPr: false,
            "Issue FHIR-9999 discussion", "Not a PR.");

        // PR 77 (HL7/fhir): body has only non-ticket tokens.
        Link(conn, 4, "sha-ddd", 77, "HL7/fhir");
        Issue(conn, 3, "HL7/fhir#77", "HL7/fhir", 77, isPr: true,
            "Encoding update UTF-8", "Bumped to version ABC-1 internally.");
    }

    private static void Link(SqliteConnection conn, int id, string sha, int prNumber, string repo)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO github_commit_pr_links (Id, CommitSha, PrNumber, RepoFullName) " +
            "VALUES ($id, $sha, $pr, $repo)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$sha", sha);
        cmd.Parameters.AddWithValue("$pr", prNumber);
        cmd.Parameters.AddWithValue("$repo", repo);
        cmd.ExecuteNonQuery();
    }

    private static void Issue(SqliteConnection conn, int id, string uniqueKey, string repo, int number, bool isPr, string title, string body)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO github_issues (Id, UniqueKey, RepoFullName, Number, IsPullRequest, Title, Body) " +
            "VALUES ($id, $key, $repo, $num, $pr, $title, $body)";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$key", uniqueKey);
        cmd.Parameters.AddWithValue("$repo", repo);
        cmd.Parameters.AddWithValue("$num", number);
        cmd.Parameters.AddWithValue("$pr", isPr ? 1 : 0);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$body", body);
        cmd.ExecuteNonQuery();
    }

    private static WindowCommit Commit(string sha) => new()
    {
        Sha = sha,
        ShortSha = sha[..Math.Min(7, sha.Length)],
        AuthorDate = "2026-06-01T00:00:00+00:00",
    };

    [Fact]
    public void Resolve_harvests_ticket_from_pr_title_and_body()
    {
        IReadOnlyList<PrTicketHarvest> harvests = PrTicketResolver.Resolve(_dbPath, [Commit("sha-aaa")]);

        PrTicketHarvest harvest = Assert.Single(harvests);
        Assert.Equal("HL7/fhir#4163", harvest.PrKey);
        Assert.Equal(["FHIR-1234"], harvest.TicketKeys);
        Assert.Equal(["sha-aaa"], harvest.ContributingShas);
    }

    [Fact]
    public void Resolve_dedupes_one_pr_across_multiple_gap_commits()
    {
        IReadOnlyList<PrTicketHarvest> harvests = PrTicketResolver.Resolve(
            _dbPath, [Commit("sha-aaa"), Commit("sha-bbb")]);

        PrTicketHarvest harvest = Assert.Single(harvests);
        Assert.Equal("HL7/fhir#4163", harvest.PrKey);
        Assert.Equal(["FHIR-1234"], harvest.TicketKeys);
        Assert.Equal(["sha-aaa", "sha-bbb"], harvest.ContributingShas);
    }

    [Fact]
    public void Resolve_ignores_non_pr_issue_rows()
        => Assert.Empty(PrTicketResolver.Resolve(_dbPath, [Commit("sha-ccc")]));

    [Fact]
    public void Resolve_returns_empty_when_db_missing()
        => Assert.Empty(PrTicketResolver.Resolve(
            Path.Combine(_tempDir, "nope.db"), [Commit("sha-aaa")]));

    [Fact]
    public void Resolve_returns_empty_when_no_links()
        => Assert.Empty(PrTicketResolver.Resolve(_dbPath, [Commit("sha-unlinked")]));

    [Fact]
    public void Resolve_uses_known_prefix_rules()
        => Assert.Empty(PrTicketResolver.Resolve(_dbPath, [Commit("sha-ddd")]));
}
