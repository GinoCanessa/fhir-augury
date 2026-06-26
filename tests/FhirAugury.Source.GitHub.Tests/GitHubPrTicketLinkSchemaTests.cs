using FhirAugury.Source.GitHub.Database;
using FhirAugury.Source.GitHub.Database.Records;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace FhirAugury.Source.GitHub.Tests;

public class GitHubPrTicketLinkSchemaTests : IDisposable
{
    private readonly string _dbPath;
    private readonly GitHubDatabase _db;

    public GitHubPrTicketLinkSchemaTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"pr_ticket_schema_{Guid.NewGuid()}.db");
        _db = new GitHubDatabase(_dbPath, NullLogger<GitHubDatabase>.Instance);
        _db.Initialize();
    }

    public void Dispose()
    {
        _db.Dispose();
        TestFileCleanup.SafeDeleteFile(_dbPath);
    }

    private static bool ObjectExists(SqliteConnection connection, string type, string name)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = @type AND name = @name";
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@name", name);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static GitHubPrTicketLinkRecord MakeEdge(string repo, int pr, string jiraKey, string provenance) => new()
    {
        Id = GitHubPrTicketLinkRecord.GetIndex(),
        RepoFullName = repo,
        PrNumber = pr,
        PrUniqueKey = $"{repo}#{pr}",
        JiraKey = jiraKey,
        Provenance = provenance,
    };

    [Fact]
    public void Initialize_CreatesTableAndNaturalUniqueIndex()
    {
        using SqliteConnection connection = _db.OpenConnection();
        Assert.True(ObjectExists(connection, "table", "github_pr_ticket_links"));
        Assert.True(ObjectExists(connection, "index", "ix_github_pr_ticket_links_natural"));
    }

    [Fact]
    public void DuplicateNaturalKey_WithIgnoreDuplicates_IsNoOp()
    {
        using SqliteConnection connection = _db.OpenConnection();

        GitHubPrTicketLinkRecord.Insert(connection, MakeEdge("HL7/fhir", 4213, "FHIR-1", "description"), ignoreDuplicates: true);
        // Same natural key (RepoFullName, PrNumber, JiraKey), different Provenance.
        GitHubPrTicketLinkRecord.Insert(connection, MakeEdge("HL7/fhir", 4213, "FHIR-1", "comment"), ignoreDuplicates: true);

        List<GitHubPrTicketLinkRecord> rows = GitHubPrTicketLinkRecord.SelectList(connection);
        Assert.Single(rows);
        Assert.Equal("description", rows[0].Provenance);
    }
}
